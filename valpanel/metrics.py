"""Samples host and Valheim metrics into SQLite and serves downsampled ranges."""

import os
import sqlite3
import subprocess
import threading
import time

DB_PATH = "/var/lib/valpanel/metrics.db"
INTERVAL = 30  # seconds between samples
RETENTION = 30 * 86400
MAX_POINTS = 360  # per range, after downsampling
RANGES = {"1h": 3600, "6h": 6 * 3600, "24h": 86400, "7d": 7 * 86400, "30d": 30 * 86400}

# (column, sql type, how to combine samples when downsampling)
COLUMNS = [
    ("players", "INTEGER", "MAX"),      # peak, so a short visit still shows
    ("cpu", "REAL", "AVG"),             # whole box, % of all cores
    ("core_max", "REAL", "MAX"),        # busiest single core, % - Valheim's sim is one thread
    ("vh_cpu", "REAL", "AVG"),          # Valheim process, % of all cores
    ("mem_used", "INTEGER", "AVG"),     # bytes, MemTotal - MemAvailable
    ("mem_total", "INTEGER", "MAX"),
    ("swap_used", "INTEGER", "AVG"),
    ("vh_rss", "INTEGER", "AVG"),
    ("disk_used", "INTEGER", "AVG"),    # bytes on /
    ("disk_total", "INTEGER", "MAX"),
    ("net_rx", "REAL", "AVG"),          # bytes/s on the default-route interface
    ("net_tx", "REAL", "AVG"),
]
NAMES = [c[0] for c in COLUMNS]
CLK_TCK = os.sysconf("SC_CLK_TCK")
NCPU = os.cpu_count() or 1


def connect():
    db = sqlite3.connect(DB_PATH, timeout=10)
    db.execute("PRAGMA journal_mode=WAL")
    return db


def init_db():
    with connect() as db:
        cols = ", ".join(f"{name} {kind}" for name, kind, _ in COLUMNS)
        db.execute(f"CREATE TABLE IF NOT EXISTS samples (ts INTEGER PRIMARY KEY, {cols})")


def read_cpu_times():
    """{cpu label: (busy jiffies, total jiffies)} for the aggregate and each core."""
    out = {}
    with open("/proc/stat") as f:
        for line in f:
            if not line.startswith("cpu"):
                break
            name, *vals = line.split()
            vals = [int(v) for v in vals[:8]]  # user nice system idle iowait irq softirq steal
            idle = vals[3] + vals[4]
            out[name] = (sum(vals) - idle, sum(vals))
    return out


def read_meminfo():
    info = {}
    with open("/proc/meminfo") as f:
        for line in f:
            key, val = line.split(":", 1)
            info[key] = int(val.split()[0]) * 1024
    return info


def default_iface():
    with open("/proc/net/route") as f:
        for line in f.readlines()[1:]:
            parts = line.split()
            if parts[1] == "00000000":
                return parts[0]
    return "eth0"


def read_net(iface):
    with open("/proc/net/dev") as f:
        for line in f:
            name, _, rest = line.partition(":")
            if name.strip() == iface:
                vals = rest.split()
                return int(vals[0]), int(vals[8])
    return None


def valheim_pid():
    out = subprocess.run(["systemctl", "show", "valheim", "-p", "MainPID", "--value"],
                         capture_output=True, text=True).stdout.strip()
    return int(out) if out.isdigit() and out != "0" else None


def read_proc(pid):
    """(cpu jiffies, rss bytes) for a process, or None if it's gone."""
    try:
        with open(f"/proc/{pid}/stat") as f:
            fields = f.read().rsplit(")", 1)[1].split()
        with open(f"/proc/{pid}/status") as f:
            rss = next(int(l.split()[1]) * 1024 for l in f if l.startswith("VmRSS:"))
        return int(fields[11]) + int(fields[12]), rss
    except (OSError, StopIteration, IndexError, ValueError):
        return None


def pct(delta_busy, delta_total):
    return round(100 * delta_busy / delta_total, 2) if delta_total > 0 else None


class Collector:
    def __init__(self, tracker):
        self.tracker = tracker
        init_db()
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        iface = default_iface()
        prev = None
        last_prune = 0
        while True:
            now = time.monotonic()
            pid = valheim_pid()
            cur = {"t": now, "cpu": read_cpu_times(), "net": read_net(iface),
                   "pid": pid, "proc": read_proc(pid) if pid else None}
            if prev:
                try:
                    self._store(prev, cur)
                    if time.time() - last_prune > 3600:
                        with connect() as db:
                            db.execute("DELETE FROM samples WHERE ts < ?", (int(time.time()) - RETENTION,))
                        last_prune = time.time()
                except (sqlite3.Error, OSError) as e:
                    print(f"metrics: sample failed: {e!r}", flush=True)
            prev = cur
            time.sleep(max(1, INTERVAL - (time.monotonic() - now)))

    def _store(self, prev, cur):
        dt = cur["t"] - prev["t"]
        cpu = {k: (cur["cpu"][k][0] - prev["cpu"][k][0], cur["cpu"][k][1] - prev["cpu"][k][1])
               for k in cur["cpu"] if k in prev["cpu"]}
        cores = [pct(*v) for k, v in cpu.items() if k != "cpu"]
        mem = read_meminfo()
        disk = os.statvfs("/")
        row = {
            "cpu": pct(*cpu["cpu"]),
            "core_max": max((c for c in cores if c is not None), default=None),
            "mem_used": mem["MemTotal"] - mem["MemAvailable"],
            "mem_total": mem["MemTotal"],
            "swap_used": mem["SwapTotal"] - mem["SwapFree"],
            "disk_used": (disk.f_blocks - disk.f_bfree) * disk.f_frsize,
            "disk_total": disk.f_blocks * disk.f_frsize,
            "vh_cpu": None, "vh_rss": None, "players": None, "net_rx": None, "net_tx": None,
        }
        if cur["net"] and prev["net"]:
            row["net_rx"] = round(max(0, cur["net"][0] - prev["net"][0]) / dt, 1)
            row["net_tx"] = round(max(0, cur["net"][1] - prev["net"][1]) / dt, 1)
        # Only attribute CPU to Valheim when the same process was there for the whole interval.
        if cur["proc"] and prev["proc"] and cur["pid"] == prev["pid"]:
            row["vh_cpu"] = round(100 * (cur["proc"][0] - prev["proc"][0]) / CLK_TCK / dt / NCPU, 2)
            row["vh_rss"] = cur["proc"][1]
        status = self.tracker.status()
        if status["server"] == "active":
            row["players"] = status["online"]
        with connect() as db:
            db.execute(f"INSERT OR REPLACE INTO samples (ts, {', '.join(NAMES)}) "
                       f"VALUES (?, {', '.join('?' for _ in NAMES)})",
                       [int(time.time())] + [row[n] for n in NAMES])


def query(range_key):
    span = RANGES.get(range_key)
    if not span:
        raise ValueError("unknown range")
    now = int(time.time())
    bucket = max(INTERVAL, -(-span // MAX_POINTS))
    aggs = ", ".join(f"{agg}({name})" for name, _, agg in COLUMNS)
    with connect() as db:
        rows = db.execute(
            f"SELECT (ts / ?) * ? AS b, {aggs} FROM samples WHERE ts >= ? GROUP BY b ORDER BY b",
            (bucket, bucket, now - span),
        ).fetchall()
        latest = db.execute(f"SELECT ts, {', '.join(NAMES)} FROM samples ORDER BY ts DESC LIMIT 1").fetchone()
        first = db.execute("SELECT MIN(ts) FROM samples").fetchone()[0]
    return {
        "range": range_key, "from": now - span, "to": now, "bucket": bucket, "interval": INTERVAL,
        "collecting_since": first, "columns": ["ts"] + NAMES,
        "rows": [[r[0]] + [round(v, 2) if isinstance(v, float) else v for v in r[1:]] for r in rows],
        "latest": dict(zip(["ts"] + NAMES, latest)) if latest else None,
    }
