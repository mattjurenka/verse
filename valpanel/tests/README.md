# valpanel tests

`test_logbuffer.py` covers the console's cursor arithmetic — which lines a poll returns and
how many it admits to having lost — because that is the part with edges: a first poll must not
claim the client missed anything, a client that fell behind the ring must be told, and a
capped response must return the *newest* lines rather than the oldest.

It lifts `LogBuffer` out of `app.py` and execs it rather than importing the module, so it
needs no virtualenv and no `webauthn`:

```sh
python3 tests/test_logbuffer.py     # from the valpanel directory
```

`test_visitors.py` covers the visitor history, which has more edges than the console: a
character name belongs to the earliest connection that hasn't got one, a restart ends every
session at once, a visit straddling the edge of a window counts only the part inside it, and
replaying journal lines that are already recorded has to change nothing. It imports
`visitors.py` directly - that module is stdlib-only - and writes to a temp database:

```sh
python3 tests/test_visitors.py     # from the valpanel directory
```

Nothing else here is unit-tested. The HTTP layer, the passkey flow and the journal tail all
want a real server to say anything useful about, and the panel is small enough to read.

`test_join.py` covers the one line of parsing behind the public `/api/join`: which journal line
the join code is taken from. It is the *activation* line and not `registered with join code`,
because a registered-but-never-activated code is crossplay's silent failure — the server looks
healthy and nothing can reach it — and a website showing that code would be worse than one
showing none.

```sh
python3 tests/test_join.py     # from the valpanel directory
```
