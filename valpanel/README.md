# valpanel — the admin dashboard

A small web panel for the live server: who is online right now, and what the box has been
doing for the last month. Passkey login, no passwords, no accounts.

This started as a **copy of what is running** on `valheim.jurenka.software`, pulled off the
droplet into this repo. The box is not a git checkout — `/opt/valpanel` is just files — so
this directory is the only version history the panel has, and `./deploy.sh` here is what puts
it back. The repo root's `build.sh`, `server.sh` and `deploy.sh` are for the game plugins and
have nothing to do with this.

```
valpanel/
  app.py                     the whole server: HTTP, passkeys, player tracking
  metrics.py                 host and game sampling into SQLite, and the range queries
  visitors.py                who has been on: journal pairing, history table, backfill
  static/                    index.html, app.js, app.css - the single page
  requirements.txt           pinned, from the venv on the box
  tests/                     the console cursor arithmetic and visitor history, dependency-free
  deploy/valpanel.service    the systemd unit as it actually runs
  deploy/Caddyfile           the vhost in front of it
  deploy.sh                  ship it to the droplet and restart (does not touch the game)
```

## How it works

**No game integration at all.** The panel never talks to the Valheim server, opens a port
to it or sends it anything. It reads the journal:

```
journalctl -u valheim -o json --since <service start> -f
```

and watches four lines the server prints by itself — `Got handshake from client <id>`,
`Closing socket <id>`, `Got character ZDOID from <name>`, and the periodic
`Connections <n> ZDOS:<n>` snapshot. That is enough for a live roster and a world-object
count. One wart worth knowing: the log never ties a character name to a Steam ID, so a name
is handed to the earliest connection that does not have one yet. With two people joining in
the same second the names can swap - which has happened in the real data, so the history
table says as much under it.

That pairing lives in `visitors.Sessions`, and both the live tail and the history backfill
feed it, so the roster and the table can never disagree about who was on.

## Who's been on

The live roster only knows about now, and journald rotates, so `visitors.py` writes every
connection it sees to `/var/lib/valpanel/visitors.db` - one row per session, holding the
Steam ID, the character name it was paired with, and when it opened and closed. At startup it
replays `journalctl --since '30 days ago'` through the same state machine to fill in whatever
the journal still holds; writes are keyed on (Steam ID, join second), so the replay crossing
the run the live tail is already watching costs nothing and changes nothing.

`GET /api/visitors?window=24h|7d|30d&page=<n>` returns one page of unique Steam IDs seen in
that window, newest first, each with every character name it connected under, its session
count, time on and when it was last seen. Paging is server-side; the table in the panel only
ever holds the page being looked at. `seconds` is clipped to the window, so "time on in the
last 24 hours" does not include yesterday, and a session still open counts up to now.

Two cases that would otherwise lie: a restart closes every session at the first line of the
new PID, since the sockets went with the old one; and a run whose end the journal never shows
(a SIGKILL with no later restart recorded) would leave a session open forever, so those are
closed with no duration rather than a growing one. Rows older than 90 days are pruned - the
longest window is 30 days, and the rest is cheap margin.

`metrics.py` samples every 30 s into `/var/lib/valpanel/metrics.db` (SQLite, WAL), keeps
30 days, and downsamples each range to at most 360 points — so `MAX` for players (a short
visit still shows as a spike) and `AVG` for the rates. It records `core_max`, the busiest
single core, alongside aggregate CPU, because Valheim's simulation is largely
single-threaded: one pegged core on a 2 vCPU box reads as a harmless ~50% in the aggregate.

**Auth is a passkey plus a hardcoded allowlist.** Anyone can create a passkey on the panel —
it lands in `/var/lib/valpanel/registrations.json` and gets them a session with no access at
all. Access comes only from the `APPROVED` dict at the top of `app.py`, which pins each
credential ID to its public key. That pinning is the point: the public key used to check a
signature comes from the source file, not from the file the web process can write, so
nothing an attacker could register at runtime grants access.

Also in there, for free, because it is all stdlib: `__Host-` prefixed `Secure`/`HttpOnly`/
`SameSite=Strict` session cookie with a 12 hour life, an `Origin` check on every POST, 30
requests/minute/IP, a 64 KB body cap, and a CSP that allows nothing but same-origin script
and style.

The unit is hardened to match — `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`,
`NoNewPrivileges`, `MemoryMax=200M`, its own `valpanel` user, and a `StateDirectory` for
the only two paths it may write. The one privilege it has is `SupplementaryGroups=
systemd-journal`, to read the game's journal.

## The console

The dashboard shows everything the server prints, live. It is fed from the **same** journal
tail the player tracker already runs — `LogBuffer` is handed every line before the tracker
applies its own filters — so there is no second `journalctl` process and no second place for
the two views to disagree about what happened.

`GET /api/log?after=<seq>` returns the lines newer than a cursor, at most 400 at a time, plus
a `dropped` count for whatever the client missed entirely. The browser polls it every two
seconds and keeps the last 1500 lines in the DOM; filtering hides rather than refetches, so
clearing a filter costs nothing. Pause stops the *display* and leaves the cursor alone, so
resuming catches up rather than skipping. Lines matching `error|exception|failed|warning` are
tinted.

The ring buffer holds 2000 lines and is deliberately not persisted: journald already has all
of it, with better tools. The cursor arithmetic is covered by `tests/test_logbuffer.py`.

**On secrets in the log.** Worth checking before putting a server log in a browser, and I did:
the only thing that looked like a credential was 35 apparent hits for the server password,
all of which turned out to be Mark saying the word in ordinary sentences — the password was a
dictionary word that appears in his dialogue. No API keys, no tokens. Steam IDs do appear, but
the panel already shows those in the player list. The password has since been removed from the
server altogether, so there is nothing of that shape left to redact.

## Approving a passkey

1. Sign in with it on the panel. It shows "Not approved yet" and prints the exact line.
2. Paste that line into `APPROVED` in `/opt/valpanel/app.py`.
3. `systemctl restart valpanel`.

That restart touches **only the panel**: a different unit, a different user, no connection
to the game. Players will not notice it.

The entry in this copy is a credential ID and a WebAuthn **public** key. Neither is a
secret in the password sense — a signature cannot be produced without the private key,
which never leaves the authenticator — but it is an access-control list, so treat edits to
it as what they are.

## Running it

On the box, as it is now:

```sh
/opt/valpanel/venv/bin/python -u /opt/valpanel/app.py     # what the unit does
```

Locally, for frontend work:

```sh
python3 -m venv venv && ./venv/bin/pip install -r requirements.txt
./venv/bin/python app.py                                  # listens on 127.0.0.1:8088
```

Three things need editing before that is much use. **Signing in** is impossible: WebAuthn is
bound to its origin, and `RP_ID`/`ORIGIN` are `valheim.jurenka.software` over HTTPS — change
both to `localhost` and the pinned `APPROVED` key stops matching anything, which is correct
and also means you get the unapproved view. **The player list** will be empty, since there
is no `valheim` unit in the journal to tail, and the visitor history with it. And `DB_PATH` in `metrics.py` is an absolute
`/var/lib/valpanel/metrics.db`, which the collector opens at startup, so it will not boot
at all until that path exists or you repoint it. Once it does, the charts work - they sample
whatever machine they run on.

## Keeping this copy honest

`/var/lib/valpanel/` — `registrations.json`, `metrics.db` and `visitors.db` — is server
state and is deliberately not here. Everything else is. To re-pull after editing on the box:

```sh
ssh root@valheim.jurenka.software \
  'tar -C /opt -cz --exclude=venv --exclude=__pycache__ valpanel' | tar -xz -C /tmp
```

and diff `/tmp/valpanel` against this directory.
