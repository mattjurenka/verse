# Exercises LogBuffer straight out of app.py, without importing the module (which would
# pull in webauthn). The class text is lifted and exec'd with just what it needs.
import re, sys, threading
from collections import deque

src = open("app.py").read()
start = src.index("class LogBuffer:")
end = src.index("LOG = LogBuffer()")
ns = {"threading": threading, "deque": deque}
exec(src[start:end], ns)
LogBuffer = ns["LogBuffer"]

fails = []
def check(name, ok):
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok: fails.append(name)

b = LogBuffer(keep=5)
r = b.since(0)
check("an empty buffer returns nothing and a zero cursor", r["lines"] == [] and r["next"] == 0 and r["dropped"] == 0)

for i in range(3):
    b.add(1000.0 + i, f"line {i}")
r = b.since(0)
check("a first poll gets everything", [l["text"] for l in r["lines"]] == ["line 0", "line 1", "line 2"])
check("and reports the newest cursor", r["next"] == 3)

r = b.since(2)
check("a later poll only gets what is new", [l["text"] for l in r["lines"]] == ["line 2"])
check("nothing is reported dropped when the client kept up", r["dropped"] == 0)

r = b.since(3)
check("a caught-up client gets nothing", r["lines"] == [] and r["dropped"] == 0)

# Overflow the ring: seqs 1..3 are gone, 6..10 remain.
for i in range(3, 10):
    b.add(1000.0 + i, f"line {i}")
r = b.since(2)
check("the ring keeps only the newest", [l["seq"] for l in r["lines"]] == [5, 6, 7, 8, 9, 10][1:])
check("and says how many the client missed", r["dropped"] == 3)   # seqs 3, 4 and 5 were evicted

r = b.since(0)
check("a fresh client is not told it missed anything", r["dropped"] == 0 and len(r["lines"]) == 5)

big = LogBuffer(keep=100)
for i in range(50):
    big.add(1.0, f"x{i}")
r = big.since(0, limit=10)
check("a limit returns the newest, not the oldest", [l["text"] for l in r["lines"]] == [f"x{i}" for i in range(40, 50)])
check("and counts the rest as dropped", r["dropped"] == 40)
check("the cursor still points at the true newest", r["next"] == 50)

print("\nALL PASS" if not fails else f"\n{len(fails)} FAILED")
sys.exit(1 if fails else 0)
