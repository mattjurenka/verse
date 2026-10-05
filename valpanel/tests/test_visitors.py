# Covers the visitor history: the line-by-line pairing of Steam IDs to character names, and
# the windowed, paginated aggregation the panel's table asks for. Both have edges worth
# pinning - a name belongs to the earliest connection without one, a restart ends every
# session, a visit straddling the window edge counts only the part inside it, and replaying
# the journal over data already recorded must change nothing.
#
# visitors.py is stdlib-only, so this imports it directly - no virtualenv needed.
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import visitors

fails = []


def check(name, ok):
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok:
        fails.append(name)


def feed(sessions, lines):
    """[(message, at, pid)] -> every event they produced."""
    out = []
    for msg, at, pid in lines:
        out += sessions.feed(msg, at, pid)
    return out


HANDSHAKE = "Got handshake from client {}"
CHARACTER = "Got character ZDOID from {} : {}:1"
CLOSED = "Closing socket {}"

# --- Sessions: pairing ------------------------------------------------------

s = visitors.Sessions()
events = feed(s, [
    (HANDSHAKE.format("76561190000001"), 100, 7),
    (CHARACTER.format("steve", "111"), 105, 7),
    (CLOSED.format("76561190000001"), 200, 7),
])
check("a join, a name and a leave are three events",
      [(e[0], e[1], e[2]) for e in events] == [("open", "76561190000001", None), ("name", "76561190000001", "steve"), ("close", "76561190000001", None)])
check("and nobody is left online", s.online == {})

s = visitors.Sessions()
feed(s, [(HANDSHAKE.format("76561190000001"), 100, 7), (HANDSHAKE.format("76561190000002"), 110, 7)])
events = feed(s, [(CHARACTER.format("bjorn", "222"), 120, 7)])
check("a name goes to the earliest connection that hasn't got one",
      [(e[1], e[2]) for e in events] == [("76561190000001", "bjorn")])
events = feed(s, [(CHARACTER.format("ricky", "333"), 130, 7)])
check("and the next name to the one after that",
      [(e[1], e[2]) for e in events] == [("76561190000002", "ricky")])

s = visitors.Sessions()
events = feed(s, [
    (HANDSHAKE.format("76561190000001"), 100, 7),
    (CHARACTER.format("steve", "111"), 105, 7),
    (CHARACTER.format("steve", "0"), 150, 7),   # a death: ZDOID 0:0
])
check("a character going away is not a new pairing", len([e for e in events if e[0] == "name"]) == 1)
check("and the player stays online", list(s.online) == ["76561190000001"])

events = feed(s, [(CHARACTER.format("steve", "111"), 160, 7)])
check("respawning under the same name adds nothing", events == [])

s = visitors.Sessions()
feed(s, [(HANDSHAKE.format("76561190000001"), 100, 7), (CHARACTER.format("steve", "111"), 105, 7)])
events = feed(s, [("something from the new process", 300, 9)])
check("a restart closes everyone who was on", [(e[0], e[1], e[3]) for e in events] == [("close", "76561190000001", 300)])
check("and the roster is empty afterwards", s.online == {})

events = feed(s, [(CLOSED.format("76561190000009"), 310, 9)])
check("a socket closing for someone unknown is not an event", events == [])

# --- Store: aggregation, windows and paging ---------------------------------

DAY = 86400
NOW = 1_700_000_000


def store():
    path = os.path.join(tempfile.mkdtemp(), "visitors.db")
    return visitors.Store(path)


def visit(st, sid, name, joined, departed):
    st.apply([("open", sid, None, joined)])
    if name:
        st.apply([("name", sid, name, joined + 1)])
    if departed is not None:
        st.apply([("close", sid, None, departed)])


st = store()
visit(st, "765A", "steve", NOW - 3600, NOW - 1800)          # 30 min, today
visit(st, "765A", "Peenas", NOW - 1700, NOW - 1600)         # same ID, another name
visit(st, "765B", "bjorn", NOW - 3 * DAY, NOW - 3 * DAY + 600)  # last week only
visit(st, "765C", "ricky", NOW - 20 * DAY, NOW - 20 * DAY + 60)  # last month only

page = st.page("24h", now=NOW)
check("the 24h window holds only who was on in it", [r["steam_id"] for r in page["rows"]] == ["765A"])
check("both of that ID's characters are listed",
      sorted(n["name"] for n in page["rows"][0]["names"]) == ["Peenas", "steve"])
check("with a session count per ID", page["rows"][0]["sessions"] == 2)
check("and the time on is the sum of them", page["rows"][0]["seconds"] == 1800 + 100)

check("a week reaches further back", [r["steam_id"] for r in st.page("7d", now=NOW)["rows"]] == ["765A", "765B"])
check("and a month further still", [r["steam_id"] for r in st.page("30d", now=NOW)["rows"]] == ["765A", "765B", "765C"])
check("rows come newest first", [r["last_seen"] for r in st.page("30d", now=NOW)["rows"]] == sorted(
    [r["last_seen"] for r in st.page("30d", now=NOW)["rows"]], reverse=True))

try:
    st.page("12h", now=NOW)
    check("an unknown window is refused", False)
except ValueError:
    check("an unknown window is refused", True)

# A visit that straddles the start of the window counts only the part inside it.
st2 = store()
visit(st2, "765A", "steve", NOW - DAY - 600, NOW - DAY + 600)
page = st2.page("24h", now=NOW)
check("a visit straddling the window edge is counted", len(page["rows"]) == 1)
check("but only for the part inside it", page["rows"][0]["seconds"] == 600)

# Someone still online has no departure yet; their time runs to now.
st3 = store()
visit(st3, "765A", "steve", NOW - 900, None)
page = st3.page("24h", online=["765A"], now=NOW)
check("an open session counts up to now", page["rows"][0]["seconds"] == 900)
check("and is flagged as online", page["rows"][0]["online"] is True)
check("while others are not", st3.page("24h", now=NOW)["rows"][0]["online"] is False)

# Paging.
st4 = store()
for i in range(25):
    visit(st4, f"765{i:02}", f"p{i}", NOW - 3600 + i, NOW - 3500 + i)
first = st4.page("24h", page=1, per=10, now=NOW)
check("a page is as long as asked", len(first["rows"]) == 10)
check("with a total and a page count", (first["total"], first["pages"]) == (25, 3))
last = st4.page("24h", page=3, per=10, now=NOW)
check("the last page holds the remainder", len(last["rows"]) == 5)
check("pages don't overlap", not {r["steam_id"] for r in first["rows"]} & {r["steam_id"] for r in last["rows"]})
beyond = st4.page("24h", page=99, per=10, now=NOW)
check("a page past the end is clamped to the last one", beyond["page"] == 3 and len(beyond["rows"]) == 5)
check("an oversized page size is capped",
      st4.page("24h", page=1, per=10_000, now=NOW)["per"] == visitors.MAX_PER_PAGE)
check("an empty window is an empty page, not an error",
      st4.page("24h", now=NOW - 10 * DAY)["rows"] == [])

# Replaying the journal must not double count.
st5 = store()
lines = [
    (HANDSHAKE.format("765A"), NOW - 3600, 7),
    (CHARACTER.format("steve", "111"), NOW - 3590, 7),
    (CLOSED.format("765A"), NOW - 1800, 7),
]
st5.apply(feed(visitors.Sessions(), lines))
before = st5.page("24h", now=NOW)
st5.apply(feed(visitors.Sessions(), lines))
check("replaying the same journal lines changes nothing", st5.page("24h", now=NOW) == before)
check("the replay left one session, not two", before["rows"][0]["sessions"] == 1)

# A run whose end the journal never shows.
st6 = store()
visit(st6, "765A", "steve", NOW - 10 * DAY, None)
visit(st6, "765B", "bjorn", NOW - 600, None)
st6.close_orphans(keep={"765B"})
page = st6.page("30d", online=["765B"], now=NOW)
rows = {r["steam_id"]: r for r in page["rows"]}
check("an abandoned session gets no runaway duration", rows["765A"]["seconds"] == 0)
check("but still counts as a visit", rows["765A"]["sessions"] == 1)
check("and whoever is genuinely online is left alone", rows["765B"]["seconds"] == 600)

# Retention.
st7 = store()
visit(st7, "765A", "steve", NOW - 200 * DAY, NOW - 200 * DAY + 60)
visit(st7, "765B", "bjorn", NOW - DAY, NOW - DAY + 60)
st7.prune(now=NOW)
check("pruning drops what no window can reach", [r["steam_id"] for r in st7.page("30d", now=NOW)["rows"]] == ["765B"])
check("and keeps the rest", st7.page("30d", now=NOW)["total"] == 1)

print("\nALL PASS" if not fails else f"\n{len(fails)} FAILED")
sys.exit(1 if fails else 0)
