"""Who has been on the server, kept past the journal's own retention.

The journal is still the only source - the panel never talks to the game - but journald
rotates, so every connection and every (Steam id, character name) pairing the tracker sees
is also written to SQLite as it happens, and a backfill at startup replays whatever the
journal still holds.

`Sessions` is the line-by-line state machine that produces those pairings. Both the live
tail in app.py and the backfill below feed it, so the live roster and the history table
cannot disagree about who was on - the same wart applies to both, too: the log never ties a
character name to a Steam id, so a name goes to the earliest connection that hasn't got one
yet, and two people joining in the same second can swap names.
"""

import json
import re
import sqlite3
import subprocess
import threading
import time

DB_PATH = "/var/lib/valpanel/visitors.db"
UNIT = "valheim"
BACKFILL_SINCE = "30 days ago"  # how far back to replay at startup
RETENTION = 90 * 86400          # windows top out at 30 days; the rest is cheap margin
WINDOWS = {"24h": 86400, "7d": 7 * 86400, "30d": 30 * 86400}
DEFAULT_PER_PAGE = 10
MAX_PER_PAGE = 100


class Sessions:
    """Open connections, rebuilt from the three lines the server prints about them.

    Knows nothing about storage or threads: `feed` takes one journal line and returns the
    events it recognised, so the caller can do as it likes with them. A new PID means the
    server restarted, which closes every socket it had.
    """

    HANDSHAKE = re.compile(r"Got handshake from client (\d+)")
    CLOSED = re.compile(r"Closing socket (\d+)")
    CHARACTER = re.compile(r"Got character ZDOID from (.+) : (-?\d+):(\d+)\s*$")

    def __init__(self):
        self.pid = None
        self.online = {}  # steam id -> {"name", "since"}

    def reset(self, pid, at):
        """Forget everyone, reporting them closed as of `at`."""
        gone = [("close", sid, None, at) for sid in self.online]
        self.pid, self.online = pid, {}
        return gone

    def feed(self, msg, at, pid):
        """One line in; a list of ("open"|"name"|"close", steam id, value, when) out."""
        events = self.reset(pid, at) if pid != self.pid else []
        if m := self.HANDSHAKE.search(msg):
            self.online[m[1]] = {"name": None, "since": at}
            events.append(("open", m[1], None, at))
        elif m := self.CLOSED.search(msg):
            if self.online.pop(m[1], None) is not None:
                events.append(("close", m[1], None, at))
        elif m := self.CHARACTER.search(msg):
            name = m[1]
            # (0, 0) is a character going away - a death or a logout - not a join.
            if (m[2], m[3]) != ("0", "0") and not any(p["name"] == name for p in self.online.values()):
                waiting = [kv for kv in self.online.items() if kv[1]["name"] is None]
                if waiting:
                    sid, player = min(waiting, key=lambda kv: kv[1]["since"])
                    player["name"] = name
                    events.append(("name", sid, name, at))
        return events


class Store:
    """One row per connection. `departed` is NULL only while the connection is open."""

    def __init__(self, path=DB_PATH):
        self.path = path
        self.lock = threading.Lock()
        with self.connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS sessions ("
                       "steam_id TEXT NOT NULL, joined INTEGER NOT NULL, "
                       "departed INTEGER, name TEXT, PRIMARY KEY (steam_id, joined))")
            db.execute("CREATE INDEX IF NOT EXISTS sessions_joined ON sessions (joined)")

    def connect(self):
        db = sqlite3.connect(self.path, timeout=10)
        db.execute("PRAGMA journal_mode=WAL")
        return db

    def apply(self, events):
        """Record events from `Sessions.feed`.

        Writes are idempotent on (steam id, join second), so the backfill replaying the run
        the live tail is already watching costs nothing and changes nothing.
        """
        if not events:
            return
        with self.lock, self.connect() as db:
            for kind, sid, value, at in events:
                at = int(at)
                if kind == "open":
                    db.execute("INSERT OR IGNORE INTO sessions (steam_id, joined) VALUES (?, ?)", (sid, at))
                elif kind == "name":
                    db.execute("UPDATE sessions SET name = ? WHERE steam_id = ? AND departed IS NULL "
                               "AND name IS NULL", (value, sid))
                elif kind == "close":
                    db.execute("UPDATE sessions SET departed = ? WHERE steam_id = ? AND departed IS NULL "
                               "AND joined <= ?", (at, sid, at))

    def close_orphans(self, keep):
        """Close rows left open by a run whose end the journal doesn't show.

        Only reachable when a line is missing - a SIGKILLed server with no later restart in
        the journal - and left alone it would count as a session that never ends. There is
        no evidence of when they left, so they get no duration rather than a growing one.
        `keep` is whoever is genuinely online now.
        """
        with self.lock, self.connect() as db:
            placeholders = ",".join("?" for _ in keep)
            where = f" AND steam_id NOT IN ({placeholders})" if keep else ""
            db.execute(f"UPDATE sessions SET departed = joined WHERE departed IS NULL{where}", tuple(keep))

    def prune(self, now=None):
        cutoff = int(now or time.time()) - RETENTION
        with self.lock, self.connect() as db:
            db.execute("DELETE FROM sessions WHERE COALESCE(departed, joined) < ?", (cutoff,))

    def page(self, window, page=1, per=DEFAULT_PER_PAGE, online=(), now=None):
        """Unique Steam ids seen in `window`, newest first, one page at a time.

        A session counts for the window if any part of it falls inside, and `seconds` is only
        the part that does - "time on in the last 24 hours" should not include yesterday.
        """
        span = WINDOWS.get(window)
        if span is None:
            raise ValueError("unknown window")
        per = max(1, min(int(per), MAX_PER_PAGE))
        page = max(1, int(page))
        now = int(now or time.time())
        start = now - span

        # Every session that touches the window, clipped to it: `beg` and `fin` are where it
        # sits inside the window, so a visit straddling the edge counts only the part inside.
        win = ("SELECT steam_id, name, joined, MAX(joined, ?) AS beg, "
               "MIN(COALESCE(departed, ?), ?) AS fin FROM sessions "
               "WHERE joined <= ? AND COALESCE(departed, ?) >= ?")
        win_args = [start, now, now, now, now, start]

        with self.connect() as db:
            total = db.execute(f"SELECT COUNT(DISTINCT steam_id) FROM ({win})", win_args).fetchone()[0]
            pages = max(1, -(-total // per))
            page = min(page, pages)
            rows = db.execute(
                f"SELECT steam_id, COUNT(*), SUM(MAX(0, fin - beg)), MAX(fin) FROM ({win}) "
                "GROUP BY steam_id ORDER BY MAX(fin) DESC, steam_id LIMIT ? OFFSET ?",
                win_args + [per, (page - 1) * per],
            ).fetchall()

            ids = [r[0] for r in rows]
            names, first_seen = {}, {}
            if ids:
                marks = ",".join("?" for _ in ids)
                for sid, name, count, last in db.execute(
                    f"SELECT steam_id, name, COUNT(*), MAX(fin) FROM ({win}) "
                    f"WHERE name IS NOT NULL AND steam_id IN ({marks}) "
                    "GROUP BY steam_id, name ORDER BY MAX(fin) DESC",
                    win_args + ids,
                ).fetchall():
                    names.setdefault(sid, []).append({"name": name, "sessions": count, "last_seen": last})
                # First seen is all-time, not in-window: it answers "is this a new face?".
                for sid, first in db.execute(
                    f"SELECT steam_id, MIN(joined) FROM sessions WHERE steam_id IN ({marks}) "
                    "GROUP BY steam_id", ids,
                ).fetchall():
                    first_seen[sid] = first
            recorded_since = db.execute("SELECT MIN(joined) FROM sessions").fetchone()[0]

        online = set(online)
        return {
            "window": window, "from": start, "to": now, "now": now,
            "page": page, "pages": pages, "per": per, "total": total,
            "recorded_since": recorded_since,
            "rows": [{
                "steam_id": sid,
                "names": names.get(sid, []),
                "sessions": count,
                "seconds": seconds or 0,
                "first_seen": first_seen.get(sid),
                "last_seen": last,
                "online": sid in online,
            } for sid, count, seconds, last in rows],
        }


class History:
    """The store, the backfill that fills it from the journal, and the live feed into it."""

    def __init__(self, path=DB_PATH, since=BACKFILL_SINCE, unit=UNIT):
        self.store = Store(path)
        self.unit = unit
        self.backfilled = None  # None until the replay finishes, then how many events it saw
        threading.Thread(target=self._backfill, args=(since,), daemon=True).start()

    def record(self, events):
        try:
            self.store.apply(events)
        except sqlite3.Error as e:
            print(f"visitors: write failed: {e!r}", flush=True)

    def page(self, *a, **kw):
        return self.store.page(*a, **kw)

    def _backfill(self, since):
        """Replay the journal once, so the table covers more than this panel's uptime."""
        sessions, seen = Sessions(), 0
        try:
            proc = subprocess.Popen(
                ["journalctl", "-u", self.unit, "-o", "json", "--output-fields=MESSAGE,_PID",
                 "--since", since, "--no-pager"],
                stdout=subprocess.PIPE, text=True,
            )
            for line in proc.stdout:
                try:
                    entry = json.loads(line)
                    msg = entry.get("MESSAGE")
                    if isinstance(msg, list):
                        msg = bytes(msg).decode("utf-8", "replace")
                    if not msg or "Hall-Patton]" in msg:
                        continue
                    at = int(entry["__REALTIME_TIMESTAMP"]) / 1e6
                except (ValueError, KeyError):
                    continue
                events = sessions.feed(msg, at, entry.get("_PID"))
                seen += len(events)
                self.record(events)
            proc.wait()
            self.store.close_orphans(keep=set(sessions.online))
            self.store.prune()
        except (OSError, sqlite3.Error) as e:
            print(f"visitors: backfill failed: {e!r}", flush=True)
        self.backfilled = seen
        print(f"visitors: backfilled {seen} event(s) since {since}", flush=True)
