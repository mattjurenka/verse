# Covers the one line of parsing behind /api/join: which journal line the crossplay join
# code is taken from. Lifted out of app.py rather than imported, like the other tests here,
# because importing the module pulls in webauthn.
#
# Worth testing because the wrong line is a live failure that looks fine. PlayFab's worst
# failure mode registers a code and never activates it - the server then looks healthy and
# nothing can reach it (HANDOFF.md) - so a page showing a registered-but-dead code would be
# worse than a page showing none.
import re

src = open("app.py").read()
start = src.index("JOIN_ACTIVE = re.compile(")
end = src.index("\n", start)
ns = {"re": re}
exec(src[start:end], ns)
JOIN_ACTIVE = ns["JOIN_ACTIVE"]

fails = []
def check(name, ok):
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok: fails.append(name)


def code_in(lines):
    """What join_info would end up with, given these journal lines."""
    found = None
    for line in lines:
        hit = JOIN_ACTIVE.search(line)
        if hit:
            found = hit.group(1)
    return found


ACTIVE = ('10/05/2026 04:44:05: Session "_<color=#FFFF00>VERSE</color> - Your Own World" '
          'with join code 968470 and IP 137.184.234.238:2456 is active with 0 player(s)')
REGISTERED = ('10/05/2026 04:44:04: Session "_<color=#FFFF00>VERSE</color> - Your Own World" '
              'registered with join code 968470')

check("the activation line gives up its code", code_in([ACTIVE]) == "968470")

check("a registration on its own does not - that code may never activate",
      code_in([REGISTERED]) is None)

check("a registration followed by an activation does",
      code_in([REGISTERED, ACTIVE]) == "968470")

# A session is re-announced as players come and go, and can be re-registered with a new code
# while the server is up. The newest line is the one that is true now.
LATER = ACTIVE.replace("968470", "123456").replace("0 player(s)", "2 player(s)")
check("the last activation wins, because a code can be reissued",
      code_in([ACTIVE, LATER]) == "123456")

check("the player count on the line is not mistaken for a code",
      code_in([ACTIVE.replace("968470", "7")]) == "7")

check("nothing in an ordinary log line looks like a join code",
      code_in(["10/05/2026 04:44:02: Opened PlayFab server",
               "10/05/2026 04:43:07: Connections 1 ZDOS:49170",
               "10/05/2026 04:44:03: Joined PlayFab Party network with ID \"e3ce5c93\""]) is None)

check("a Steam-backend log has no code at all, which is the honest answer there",
      code_in(["10/05/2026 02:51:47: Opened Steam server",
               "10/05/2026 02:51:47: Registering lobby"]) is None)

print()
if fails:
    print(f"{len(fails)} FAILED: " + ", ".join(fails))
    raise SystemExit(1)
print("ALL PASS")
