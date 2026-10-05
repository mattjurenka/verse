# Handoff — 2026-10-02

Read this first in a fresh session before touching the server again, along with
`docs/verse-design.md` and `docs/valheim-server-plugins.md`.

## Repo & deployment

- Repo: `/home/matthew/verse`. Production server: `root@valheim.jurenka.software` (SSH key
  already works, no auth setup needed).
- Deploy with `./deploy.sh root@valheim.jurenka.software` — builds all 3 plugins, uploads,
  restarts the `valheim` systemd service, waits for startup checks.
- Pattern used for anything risky: dry-run config flag → review the numbers → apply →
  verify via `journalctl -u valheim`. The service occasionally crashes on the *first*
  restart attempt after a deploy with a random transient Harmony/Mono error and
  auto-restarts clean via systemd within a couple seconds — this is normal, not a
  regression. Confirm health via the *second* PID's `all hooks resolved` line
  (`systemctl show valheim -p MainPID,ActiveEnterTimestamp` to get the real current PID,
  since `deploy.sh`'s own log tail can show stale output — or, as happened on the last
  deploy, nothing at all).
- Run `./test.sh` before deploying when a change touches `Commands.cs`/`CommandText.cs`.

## Server config lives outside the repo

Both files are backed up in place as `*.bak-<timestamp>` before every edit.

- `/etc/valheim.env` — `SERVER_NAME`, `WORLD`, `PASSWORD`. Root-readable only. A plain
  `cat` of it may trip a sandbox "credential materialization" guard, since a password lives
  in it; `grep -E '^(SERVER_NAME|WORLD)=' /etc/valheim.env` works, and
  `grep '^PASSWORD=' /etc/valheim.env` is the only way to read the password back — it was
  generated on the box and never printed into a transcript.
- `/etc/systemd/system/valheim.service` — `ExecStart` is now
  `-name "${SERVER_NAME}" -port 2456 -world "${WORLD}" -savedir /opt/valheim/save -public 1 -password "${PASSWORD}"`.
- `/opt/valheim/BepInEx/config/com.matthew.verse.cfg` — the plugin's own config, written by
  BepInEx. A **new** config key does not exist in this file until the new DLL has run once,
  so a deploy that needs a non-default value has to seed the key by hand first or it takes
  two restarts.

## Done and verified working (all deployed)

- `!warp spawn` / `!warp bed` commands.
- `ScatterNewVerses` reverted to off — all verses spawn at the temple, same place.
- **Duplicate command execution bug** fixed — vanilla sends one chat RPC per listener in
  the sender's player list, so a solo player's command fired twice (their own copy, and
  `VerseIdentity`'s synthetic listener). Dedup guard added in `Commands.cs`'s `Overhear`
  patch (same 0.5s-window pattern `GlobalChat.Deliver` already used for its own copy of
  this problem).
- **`Sweep.cs`** fixed in three ways:
  1. Creatures were being swept from the *entire map*, not just near the base (added the
     same proximity gate pieces/items already had).
  2. Pieces required `creator == 0`, which missed live-tagging misses with a real creator
     (removed that gate — migration only gets one shot, anything still untagged later is
     an orphan either way).
  3. The temple itself is now a sweep anchor (`Spawns.TryGetTemple`), not just the legacy
     base — the temple isn't necessarily near the base, and it's the one place every
     verse's players actually stand.
- **`BossStones.cs`** — every new verse automatically gets its own fresh, unused set of
  Guardian Power stones at the temple, hooked into `Verses.OfOrCreate`. No player-facing
  command (there was one, `!verse stones` — removed per explicit request, should be fully
  automatic only). Watch out if touching this again: `ZDOMan.CreateNewZDO`'s prefab
  parameter does **not** actually set the ZDO's prefab field — you must call
  `zdo.SetPrefab(...)` explicitly afterward, or the object exists as data but renders as
  nothing (this bit us once already, cleanup code for the broken leftovers is already in
  `BossStones.Seed`).
- **`PortalRepair.cs`** — fixed cross-verse portal pairing corruption. Valheim's portal
  connection system is global and tag-based, with zero awareness of verses, so verse 1's
  legacy portal hub (which had "shadow" duplicate ZDOs sitting at the identical position as
  each real portal — likely from before live-tagging was reliable) had several portals wired
  to the wrong destination, including one pointing at a test verse's spawn portal instead of
  home. Applied for verse 1 — **15 of 16 tags fixed and confirmed working by the user
  in-game**. **The "Queen" tag was left unresolved** — `PortalRepair.Plan` found 3 position
  clusters instead of the expected 2 and correctly refused to guess rather than risk a wrong
  pairing. Worth a follow-up `!verse diag portalfix 1` and a manual look if it comes up
  again.
- Diagnostic commands under `!verse diag` (admin-only — permanent tools, unlike
  `!verse stones` which was removed): `diag` (dump everything within 100m), `diag world`
  (prefab name search, needles in `DiagWorldNeedles`), `diag keys` (GlobalKeys scoping
  state), `diag portals` (every portal's tag + connection), `diag portalfix <verse>`
  (dry-run portal repair plan), `diag portalapply <verse>` (apply it).

### Server-side terrain editing — works, and three traps found doing it

`src/Verse/Ground.cs` writes real terrain edits from the dedicated server, with no client
involved. Notes for anybody touching it:

- Terrain lives in **one gzipped byte array per zone**: `TCData` on a `_TerrainCompiler` ZDO
  sitting at the zone's centre (`ZoneSystem.GetZonePos`), holding a `levelDelta` and a
  `smoothDelta` per vertex of the zone's 65×65 grid plus a paint mask. The server owns the
  object table, so it can write that array, and the client applies it in
  `Heightmap.ApplyModifiers` exactly as it applies the player's own digging. The format is
  mirrored from `TerrainComp.Save`/`Load`; `Load` **abandons the whole array** on a length
  mismatch with one warning in the *client's* log, so the two declared lengths must be
  `(m_width + 1)²` (4225 today) or the edit silently does nothing.
- **The delta is not against `WorldGenerator.GetHeight`, and this one is worth more than a
  metre.** A zone's vertex heights come from `HeightmapBuilder.Build`, which takes the biome at
  the zone's four *corners* and blends four whole biome heights across the zone with a
  smoothstep when they differ; `GetHeight` asks the biome at the point instead. On the test
  world the two differed by **1.16 m** on a zone straddling a biome edge, and the one the
  terrain is actually built from is the blend. `Ground.Blended` reproduces it, and
  `ArenaSite.HeightAt` now routes every arena placement through it — so a chest or a wall near a
  biome boundary was previously off by that much, levelling or no levelling.
- **`ZDOMan.DestroyZDO` only queues the id** on `m_destroySendList` — the ZDO stays in
  `m_objectsByID`, readable, until the next `ZDOMan` update. So "destroy the compiler to undo
  an edit" reads the old deltas straight back; `Ground.Restore` writes the cleared payload
  first and then destroys. The arena self-test caught this.
- Vanilla clamps a vertex to ±8 m of generated ground (both in `m_levelDelta` and again in
  `ApplyToHeightmap`), so this flattens slopes, not cliffs.
- Paint: `Heightmap.m_paintMaskPaved`/`Dirt`/`Cultivated` all read as "cleared" to
  `ClutterSystem`, which is what stops grass growing back through a levelled floor.

Verified on the local dedicated server (`./server.sh --clean`): 9 compilers written, 7,497
vertices levelled, the payload re-read cold off the ZDO at the height it was given, and
`Ground.Restore` putting the ground back to the seed's own height and removing the compiler.
**Not yet verified with a client in the world** — only a player standing there proves the mesh
moves.

### A level wall top is what removes the gaps, and the unbreakable rule is what pays for it

The ring followed the terrain for a good reason — stone collapses in tall stacks, and two
earlier builds proved it — but a terrain-following top means the walkway and railing on top of
it step along behind it, and every step leaves a wedge of daylight under the next piece. That
was the "weird gaps in the wall" from the game. Collapse is support damage against a few hundred
hit points, and `Fixture` has made these pieces unbreakable since, so the constraint is gone:
`ArenaRing.Raise` now builds to **one height all the way round**, with as many courses per
segment as it needs (246 pieces on the live site, averaging 6 per segment, where terrain-
following used 123), laid downwards from the top so the slack is buried rather than left as a
gap. The walkway and railing are flat rings with it — measured spread 0.00 m. With
`ArenaUnbreakable` off it goes back to following the terrain, because a venue that falls over is
worse than one with a stepped top.

### Things people have to walk up must be ramps, not steps

Separate level plates 0.4 m apart are not a stair: between them there is nothing to walk onto,
so whether a character rides up the next plate's edge is a question about a capsule and a
collider, and in the game the answer was "I have to jump". `ArenaStand.Ramp` tilts the same
boards to the slope and overlaps them along it, which is one continuous surface at about 20° —
a character walks up it like a hill. The piece's own up axis comes from `Footing`, so the
rotation is arithmetic.

### Destroyed ZDOs stay readable for a frame, and it has misled three counts

`ZDOMan.DestroyZDO` only queues the id on `m_destroySendList`; the object stays in
`m_objectsByID` until `SendDestroyed` runs on the next update. Anything that tears fixtures down
and then counts what is standing counts both. It has produced a gallery reported as 334 pieces,
a nine-step stair reported as seventeen, and a walkway reported as three metres out of level in
the frame it was relaid flat. `Fixture.Doomed` asks the queue (reflection — it is private), and
the fixture scans skip anything on it.

### Valheim has no climbing, so "put a ladder on it" does not work

Looked for it after the arena's ladder came back from the game as "not actually climbable":
there is no climb code in `Player` or `Character` at all, and the only `Ladder` component in
`assembly_valheim` is a **lift** — `Ladder.Interact` moves the character to a `m_targetPos`
transform on the prefab. `wood_stepladder` does not carry one, so a wood ladder is climbed
purely by walking up its collider: it works only when it faces the right way, and that facing
is a decision in a Unity scene the server cannot read. Anything the server builds that people
have to get up should be **steps** instead — level tiles, ≤0.4 m apart, which a player walks up
without jumping and which have no orientation to get wrong. `ArenaStand.Stair` does that with
the same `wood_floor` boards as the rest of the venue.

### Terrain: fill hollows, do not level

First version levelled the arena floor flat and paved it, which in the game was a grey disc
with a cliff round the outside you could walk up. `Ground.Fill` is raise-only: it lifts ground
below a floor line and leaves everything above it alone, so the natural shape survives and
there is no step anywhere (at a hollow's edge the filled surface and the ground are the same
height). The floor line is the median ground inside the ring less 1.5 m, never below the water
level. Paint only goes on what was filled.

### Pieces the arena builds are now unbreakable

`Fixture.cs` writes `1e9` into `ZDOVars.s_health` on every piece it places (wall, decks,
boardwalk, gallery, ladders, gate chests). `WearNTear.Awake` reads that field with the prefab's
`m_health` only as a default, and every hit subtracts from it — so there is no indestructible
flag in vanilla, just a number the server owns. `ArenaRing.Repair` re-asserts it at every run
start, which covers pieces built before the rule and anything a player repairs back down with
a hammer.

### Opening the server to the public — done

`src/Verse/PublicServer.cs` (new) and a second transpiler in `src/Verse/PlayerCap.cs`.
`docs/valheim-server-plugins.md` §12 is the full write-up of what the browser reads and from
where; the class doc in `PublicServer.cs` covers the password mechanism in detail.

The three asks, and how each was settled:

1. **Sorts to the top.** `CommunityServerList.GetFilteredList` sorts on
   `m_serverName.CompareTo(...)` and on nothing else, so the name starts with `!`. **But the
   sort happens after a truncation to 200, and `ZSteamMatchmaking.GetServers` fills those 200
   from public lobbies before it looks at dedicated servers at all** — so `!` wins the sort
   among whatever 200 were fetched and does nothing to get you into them. The filter box is
   the reliable route: it is a lowercased `Contains` on the name applied *before* the cap.
   This was the live symptom — the server was joinable from "recent" but absent from the
   community tab with an empty search box.
2. **Neon yellow.** The browser assigns the name straight to a `TMP_Text`, so TextMeshPro
   markup is parsed. `SERVER_NAME` is now
   `"!<color=#FFFF00>VERSE</color> - No Password - Your Own World"` — 60 bytes of Steam's
   63-byte budget (`SetServerName` caps at `k_cbMaxGameServerName`), with the `!` deliberately
   outside the tag so it is still the sort key.
3. **A password that accepts anything.** Vanilla will not run a listed server without a
   password: `FejdStartup.ParseServerArguments` calls `Application.Quit()` if `-public 1` and
   the password is missing, under five characters, or a substring of the world or seed name.
   That check reads the command line before `ZNet.SetServer`, so `-password` on `ExecStart`
   has to be real. The patch then rewrites **only** the comparison in `ZNet.RPC_PeerInfo`, via
   a transpiler that swaps the `String::op_Inequality` operand for `PublicServer.Mismatch`.
   Everything else stays vanilla on purpose: the padlock still shows and the dialog still
   comes up, so the entry looks like every other listed server. Gated on the
   `AcceptAnyPassword` config key, which **defaults to false** — it is `true` in the live
   config only. The client ignores an empty submission, so a player must type *something*.

   **Do not re-derive this the other way round.** The first version also blanked
   `ZNet.m_serverPassword` in a postfix on `SetServer`, which removed the dialog entirely. It
   worked — a vanilla client joined with no prompt at 21:50 — but it also took the padlock off
   the entry, and was reverted at the user's request: looking like a normal listed server was
   worth more than skipping the dialog. The blanking approach is a three-line postfix if it is
   ever wanted again, and `PublicServer.cs`'s class doc records it.

   The padlock is **display-only** and was *not* why the server was missing from the community
   tab — `isPasswordProtected` is read in one place, `ServerListElement.UpdateTextAndIcons`, to
   toggle the row's `Private` icon. Nothing in the list-building path tests it and
   `RequestInternetServerList` is called with zero filters. See point 1 for the actual cause.

Also fixed, because it only becomes visible once the server is listed:
`ZSteamMatchmaking.RegisterServer` hard-codes `SetMaxPlayerCount(10)` where the real cap is
25, so the entry would have read `12 / 10` to anyone choosing a server. Transpiled to
`PlayerCap.Limit()`, matched by the call that follows it rather than by the constant.

Verified on the live server:

- `Verse 0.1.0 startup check: isolation on, party cap 12, player cap 25, password asked for, anything accepted, all hooks resolved.`
  — no `Problem` from either transpiler, and no `(browser still says 10)` note on the cap.
- `Registering lobby` / `Opened Steam server` / `Game server connected`.
- An A2S query on UDP 2457 (the recipe is in §12) returned: name intact at all 60 bytes with
  the colour tag, **max players 25**, **visibility 1** (padlock advertised), type `d`,
  environment `l`.
- **A real vanilla client joined.** On the earlier blanking build: `Got connection` →
  `Got handshake` → `Server: New peer connected` → `Got character ZDOID from adasd2`, with no
  `has wrong password` anywhere, which proved the admission path end to end. The current
  transpiler-only build has **not** had a client through it yet — the dialog is back, so what
  needs confirming is that typing anything into it gets you in.

**Still only checkable from a Valheim client:**

- That an arbitrary password typed into the dialog is accepted (the one open item above).
- Whether the entry ever appears in the community tab. **It does not, even filtered to
  "verse"** — see the section below, which rules the server side out.
- That the browser *renders* the colour tag rather than printing it literally. The field is a
  `TMP_Text` so it should, but it has never been confirmed for this specific field.

**The password has since been removed outright — 2026-10-03.** The blanking approach above is
back, now as its own config key rather than a replacement for the transpiler:

- `NoPassword` (new, defaults to **false**) blanks `ZNet.m_serverPassword` in a postfix on
  `ZNet.SetServer`. No padlock, no dialog. `AcceptAnyPassword` stays and is left on as the
  fallback for a game update that moves the field — see `PublicServer.cs` and §12 of the plugin
  notes for why the two compose rather than conflict.
- **`-password` still has to stay on `ExecStart`.** This was re-confirmed against the assembly,
  not taken on trust: `FejdStartup.ParseServerArguments` validates the *command line* and calls
  `Application.Quit()` before `SetServer` ever runs. The argument stays real; it just stops
  reaching players.
- Verified locally only, on `./server.sh` with the key seeded: the startup check prints
  `password being removed, all hooks resolved` and the postfix then prints `password removed: no
  padlock in the browser and no dialog on connect` — the "had one to clear" wording, so the
  field really was populated when it fired. **Not yet deployed to the live server, and not yet
  had a client through this build.** The mechanism itself was proven against a vanilla client on
  2026-10-02 (the 21:50 join below), so what is unconfirmed is the config gating, not the trick.
- That settles the open cosmetic question that used to be here: with no dialog, **"No Password"
  is accurate again** and is also the shorter string. The live `SERVER_NAME` currently says
  `_<color=#FFFF00>VERSE</color> - Any Password` and wants changing when this deploys. It lives
  in `/etc/valheim.env`, outside the repo. Mind the 54-byte ceiling if crossplay is ever
  re-enabled, and check the join code after any rename.

### Crossplay was tried and REVERTED — the server is on the Steam backend

**Current live state (22:56 UTC): Steam backend, no `-crossplay`.** Verified from outside: A2S on
2457 answers 212 bytes with the name intact, max players 25, visibility 1; and
`ISteamApps/GetServersAtAddress` lists `137.184.234.238:2457`. Joinable at
`play.verseworlds.fun:2456` via Join Game → Add server, any password accepted.

Read the rest of this section before trying `-crossplay` again. It is still the only
configuration where the community tab's search box finds the server
(`PlayFabMatchmaking.ServerSideFiltering` is `true` where Steam's is `false`), it worked twice,
and it brought in a Nintendo Switch player — but it then failed four times running and had to be
backed out.

**The failure: stuck in `State.Creating`, which is silent and total.** `OnSessionUpdated` logs
`registered with join code <n>`, sets `m_retries = 100` and calls `CheckJoinCodeIsUnique()`.
Activation only happens if that search comes back:

```csharp
else if (result.Lobbies.Count == 1 && result.Lobbies[0].Owner.Id == GetEntityKeyForLocalUser().Id)
    ActivateSession();          // UpdateLobby: SearchData["string_key2"] = "True"
else
    OnSessionUpdated(State.RegenerateJoinCode);
```

On the stalled instances **neither callback ever fired** — no `Retry join-code check`, no
`Zero lobbies returned`, no `Created new join code`, no error. And because `string_key2` is only
set to `True` by `ActivateSession`, every discovery path fails at once: the join-code lookup, the
name search and `FindServerByIp` all filter on `string_key2 eq 'True'`. The client reports
"couldn't resolve join code" / "failed to connect".

**There is no timeout on that state.** `m_retries` only decrements when the callback *returns*
zero lobbies, so a dropped response hangs forever while systemd reports `active` and all three
plugins print clean startup checks. This is the worst-shaped failure on a public server: the only
evidence is the absence of the `is active with` line.

What the evidence pointed at, and what was disproven:

| PID | name | join code | activated |
| --- | --- | --- | --- |
| 114235 | `!<color…>` | **781583** | yes |
| 114984 | `␠␠_<color…>` | 555433 | yes |
| 115761 | `\t\t_<color…>` | 555433 | **no** |
| 118863 | `_<color…>` | 555433 | **no** |
| 127498 | `_<color…>` | 555433 | **no** |
| 130384 | `_<color…>` + `-instanceid v2` | 555433 | **no** |

Every instance from 22:38 on was handed the same join code, suggesting a stale lobby from an
earlier run still owns 555433 and defeats the `Count == 1 && Owner.Id == mine` test. **`-instanceid`
does not help** — it appends to the PlayFab custom ID (`{name}_{port}_{deviceUniqueIdentifier}` +
InstanceId), so `v2` produced a genuinely new entity and PlayFab still issued 555433. The join
code is not derived from the entity and there is no server-side lever on it found so far.

**If retrying:** wait for the stale lobbies to age out of PlayFab's index (nothing server-side
speeds this up), put a startup watchdog in place first, then make exactly one change and leave it
alone. The churn of repeated restarts is what wedged it — it was healthy with a player on it at
22:36 and was restarted four more times for unrelated cosmetic reasons.

Historical state from when it was working, 22:23 UTC:

```
Logged in PlayFab user via custom ID
Register PlayFab server "!<color=#FFFF00>VERSE</color> - Any Password" with IP 137.184.234.238:2456
Joined PlayFab Party network with ID "4774966d-…"
Session "!<color=#FFFF00>VERSE</color> - Any Password" registered with join code 781583
Session … is active with 0 player(s)
```

Firehose reports `backend=PlayFab` and `steam rate: off (backend is PlayFab, which has no
pinned rate)`, which is correct and costs nothing — the pinned rate it exists to lift is
Steam-only.

Three things had to be fixed to get here, all written up in
`docs/valheim-server-plugins.md` §12 under "`-crossplay`, and the three things that stop it
working". In short:

1. `apt-get install --no-install-recommends libpulse-mainloop-glib0` — `libparty.so` links
   PulseAudio's glib mainloop and a headless box does not have it. The error is
   `DllNotFoundException: libParty.so`, which is a lie: the file exists as lower-case
   `libparty.so` and the real problem is the missing dependency. **Always
   `ldd /opt/valheim/valheim_server_Data/Plugins/libparty.so | grep "not found"` first.** A
   `libParty.so` symlink was added in that directory and `valheim_server_Data/Plugins` was
   appended to `LD_LIBRARY_PATH` in the unit while chasing the wrong theory; both are harmless
   and were left in place, but neither is what fixed it.
2. **`SERVER_NAME` must be ≤ 54 characters.** The PlayFab account id is
   `PlayFab_` + name + `_<port>_` + 32-char device id = name + 46, against a 100-character
   limit, and exceeding it fails the login and then calls `Application.Quit()`. The name was cut
   from 60 to 44 bytes (`"Your Own World"` dropped) and the tagline changed to "Any Password",
   which is also more honest now the dialog is back. **TMP markup is fine in the name** — only
   the length matters.
3. `src/Verse/AccountId.cs` — the account-key fix below.

Expect **no UDP listener on 2456**: the transport is a Party network over Azure relays, so
`ss -lunp` shows only the query socket on 2457. Health is the `Session … is active` line, not a
socket.

Reverting (already done): drop ` -crossplay` (and any ` -instanceid …`) from `ExecStart`,
`daemon-reload`, restart. The name may go back up to 63 bytes at the same time — the 54-byte
budget is a crossplay-only constraint. It was left at 44 bytes
(`_<color=#FFFF00>VERSE</color> - Any Password`) so that re-enabling crossplay needs no name
change; `"Your Own World"` is the 17 characters that were dropped to fit, if you want them back
while on Steam.

### The account key, and why switching backend nearly destroyed every verse

`verses.json` keys players by **bare Steam ID** (`"76561198033210929"`), which is what
`ZSteamSocket.GetHostName()` returns. `ZPlayFabSocket.GetHostName()` returns
`m_platformPlayerId.ToString()`, and `PlatformUserID.ToString()` is
`GetPlatformPrefix(m_platform) + m_userID` — so the same player arrives as
`"Steam_76561198033210929"`. Under `-crossplay` every returning player would have missed their
registry entry, been treated as new by `Verses.OfOrCreate`, and been handed a fresh empty world
while their real one sat orphaned. All 17 verses, verse 1's migrated progression included, were
one restart from that.

Fixed at the single chokepoint: `Peers.PlatformId` now goes through
`AccountId.Canonical`, which strips **only** Steam's prefix (every other platform keeps its own,
so an Xbox id can never be read as the Steam id of the same digits). The bare form won because
it is what is already on disk, so there was nothing to migrate. `AccountId.cs` is deliberately
Unity-free so `tests/TextTests` compiles it directly — 10 cases, run `./test.sh`.

**Verified against live traffic**, both halves:

```
peer 3733200152 (76561198098799929)             placed in verse 17
peer 1981458557 (Nintendo_10051847508551881508) placed in verse 18
```

The first is a Steam account arriving over a PlayFab socket and being normalised back to its
bare id, landing in the verse it already owned rather than a new one. The second is a Nintendo
Switch player — genuinely new, correctly given a new verse, and keeping the `Nintendo_` prefix,
which is the reason only Steam's prefix is stripped. `grep -oE '"(Steam|PlayFab|Xbox)_[^"]*"'`
over `verses.json` returns nothing, which is the proof the normalisation held. Backup from
before the switch, if ever needed: `verses.json.bak-20261002-221548`.

The verse count is therefore 18, not 17, and that increase is correct. Do not read a rising
count after a backend change as the orphaning bug without checking the leader keys first.

### Why the server cannot be put "at the top" of the community tab — settled

Do not spend time on the server name for this. It was chased a long way and the answer is that
list position is not reachable from the server side at all.

`ZPlayFabLobbySearch.FindLobbyWithPagination` requests 4 pages of 50 (`number_key11 eq
<page>`, `PageSizeRequested = 50`) — that is the 200 — and the server picks its own page with
`GetSearchPage() => UnityEngine.Random.Range(0, 4)`. `FindLobbies` is called with **no
`OrderBy`**, so each bucket returns an arbitrary 50 and appearing at all is a draw of roughly
`200 / total_servers` per refresh. The alphabetical sort in `CommunityServerList.GetFilteredList`
only ranks the 200 that were already drawn.

The giveaway symptom, which was misread twice here: **reliably findable by name, absent from
the unfiltered list.** That is not "registered but sorted badly" — the name filter is a
letter-frequency index (`CharToKeyName`, `number_key15..30 ge <count>`) that narrows the
population enough to fit the 50-per-bucket cap, so it is deterministic where the browse is not.

Three prefixes were tried live and all changed nothing, because none of them addressed the
draw: `!` (60-byte name, pre-crossplay), `␠␠_`, then `\t\t_`. The measured culture-sensitive
prefix order is in `docs/valheim-server-plugins.md` §12 if it is ever relevant — the short
version is that `!` is mid-table, more of a character beats fewer, and zero-width characters
are ignorable and do nothing. The name is now `_<color=#FFFF00>VERSE</color> - Any Password`
(44 bytes): one underscore keeps the sort edge for the refreshes that are won, without the
broken-looking indent a whitespace prefix gives.

**What is actually deterministic for players**: the join code, `play.verseworlds.fun:2456` via
Join Game → Add server, and telling people to type `verse` into the community tab's search.
Crossplay is what made that last one work — on the Steam backend the filter is client-side over
an already-truncated list, so searching the server's own name found nothing.

Note each `SERVER_NAME` change rewrites the PlayFab custom ID, which can issue a new join code.
Check the log after any rename rather than trusting a code written down earlier.

### Ruled out while the server was on the Steam backend

The server is joinable from "recent" and by address, but has never appeared in the community
tab — including with `verse` typed into the search box, which rules out the 200-entry cap.
**Everything reachable from the server side has been checked and is correct.** Do not re-run
these; pick up after them.

- **Steam's master server has the entry.** Keyless, no API key needed:
  ```sh
  curl -s "https://api.steampowered.com/ISteamApps/GetServersAtAddress/v1/?addr=137.184.234.238"
  ```
  returns `addr 137.184.234.238:2457, appid 892970, gamedir valheim, gameport 2456,
  region -1, secure false`. So registration genuinely succeeded.
- **The server answers A2S reliably** — 5/5 challenge+info round trips, 228 bytes each, no
  loss. This matters because `ZSteamMatchmaking.OnServerFailedToRespond` is an **empty
  method**: a server that does not answer the client's own direct ping is dropped silently,
  with nothing logged anywhere.
- **Nothing in the client's ingest or UI path filters it out.** `OnServerResponded` adds every
  server unconditionally. `CommunityServerList.GetFilteredList` only truncates and sorts. The
  version/crossplay checks in `ServerListGui` (~line 964) are in `OnPlayFabJoinCodeSuccess`
  only — the join-by-code path, not the list. `MultiBackendMatchmaking` always constructs
  *both* PlayFab and Steamworks backends, so a client's crossplay setting does not hide a
  Steam-only server from the tab.
- Ports 2456 and 2457 UDP are open in ufw (v4 and v6); `GameServer.Init(0, m_serverPort,
  m_serverPort + 1, ...)` means only those two are used, so 2458 is not needed on 1.0.16.
- No `Steam register server failed` in the journal, so `OnSteamServerRegistered` never
  reported a failure.

**The one asymmetry that makes the symptom ambiguous:** `SteamworksMatchmaking.ServerSideFiltering`
is `false`, so typing in the search box filters only what has *already* arrived from Steam —
it cannot fetch anything new. The refresh delivers thousands of servers and
`m_serverListRevision` only bumps every 100 responses, so filtering before a refresh has
finished can show nothing while the entry is still queued. Any retest must be **clear the
filter → Refresh → wait for the count to stop climbing → then filter**.

Two untried leads, in order of cheapness:

1. **Plain-ASCII name test.** It has never been confirmed that Steam's browser tolerates
   `<color=#FFFF00>` in a server name end to end. Set `SERVER_NAME` to something like
   `!VERSEtest`, restart, and look. If it appears, the markup is the problem and the colour
   has to go (the A2S reply shows the name stored whole, but that is our own socket answering,
   not Steam's browser parsing it).
2. **`-crossplay`.** This switches `ZNet.m_onlineBackend` to PlayFab, so `OpenServer` calls
   `ZPlayFabMatchmaking.RegisterServer` *instead of* the Steam one — it is either/or, not
   both. It registers in the PlayFab list that crossplay clients browse and opens the server
   to Xbox/Game Pass players, at the cost of PlayFab's relay connection path. A real option if
   discoverability matters more than the Steam list.

Meanwhile the reliable route for players is the direct address,
`valheim.jurenka.software:2456`, added via Join Game → Add server / favourites. That path is
proven working.

## Context for a fresh session

The production server is `valheim.jurenka.software`, the same box this work was done against
over SSH. A fresh session has no memory of any of it. The user has already confirmed the
account being used may change — a new session may need SSH access re-confirmed, but the key
has worked throughout.
