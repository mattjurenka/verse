# Verses: one private world per player, in one server process

Design notes and findings for the `Verse` plugin (`src/Verse`). Everything below was read
out of the decompiled **Valheim 1.0.16** dedicated-server assembly, the same way
[the plugin field notes](valheim-server-plugins.md) were; line numbers are `ilspycmd` output
and will drift, but the class and method names are what to grep for.

## What it is meant to do

Every player who connects gets their own world — a **verse** — and leads a group of up to
six. A verse is private by default. Its leader can open it by invite, by a password given
alongside the verse's id (a small incrementing integer, so it can be said out loud), or to
anyone. Joining someone else's verse gives up your own. No verse can see, reach or affect
another: the only way in is for the leader to let you in.

## Verdict: possible, and the shape is forced by four facts

**1. ZDO visibility is already per-peer, with a single gate.**
`ZDOMan.CreateSyncList` (`ZDOMan.cs:1261`) builds a separate send list per peer, and all
four send paths — near objects, distant objects, force-sends, the client change queue —
funnel through `ZDOPeer.ShouldSend` (`ZDOMan.cs:52`), which today is only a revision check.
That one method is the whole isolation mechanism. A client never learns the other verses
exist.

**2. Teleporting is client-authoritative, so geography cannot be fenced.**
`Player.TeleportTo` (`Player.cs:5888`) runs on the client and RPCs its *own* ZDO. The
server cannot refuse it. This kills the obvious "give each verse a far-apart region and
fence it" design — a modified client flies wherever it likes. Isolation has to be
*visibility*, which is also why it ends up stronger: with every verse at the same
coordinates there is no coordinate to travel to.

**3. Clients generate terrain themselves, from the seed.**
`Heightmap.Generate` (`Heightmap.cs:421`) and `HeightmapBuilder` both call
`WorldGenerator.instance` on the client. Biomes are radial from the world origin, so a verse
placed 8 km out *is* Ashlands and no server-side patch can make it Meadows. Overlapping all
verses at the same coordinates is therefore not a compromise but an advantage: every verse
gets the genuine vanilla world, correct biomes, the real start, the full map.

**4. Per-verse spawn works on a vanilla client.**
`Game.FindSpawnPoint` (`Game.cs:524`) falls back to
`ZoneSystem.GetLocationIcon("StartTemple")`, and on a *client* that reads `m_locationIcons`,
populated only by the server's `SendLocationIcons(long peer)` (`ZoneSystem.cs:835`) — which
already targets one peer. Hand each player their own `StartTemple` position and they spawn
in their own world, with nothing installed on their end.

### Why not a process per verse

A real server process per verse gives perfect isolation, correct worldgen and no engine
fighting, and maps onto vanilla almost 1:1 (verse id → port, verse password → `-password`).
It fails on one fact: **a vanilla client cannot be redirected between servers.** There is no
redirect RPC, so every verse switch is a manual reconnect to a *different* port, and each
process costs ~1–2 GB. It stays the fallback if the remaining spikes go badly, and the
better answer above ~40 concurrent players. See *Scaling*.

## How isolation is built

| Concern | Hook | Why there |
| --- | --- | --- |
| Objects, buildings, creatures, other players | `ZDOMan.ZDOPeer.ShouldSend` (`:52`) | The one gate every send path uses |
| Portals | *nothing* | A client matches portals among ZDOs it knows; it cannot connect to one never sent |
| Chat, damage, all client-to-client traffic | `ZRoutedRpc.RouteRPC` (`ZRoutedRpc.cs:140`) | The relay every such message crosses |
| Player names and positions | `ZNet.SendPlayerList` (`ZNet.cs:2475`) | Vanilla builds one list and sends it to everyone |
| Verse of a connection | `ZDOMan.AddPeer` (`:816`) | The call that first makes a peer eligible for sync |

**The tag.** A verse id is stored in the ZDO's own field set. `ZDO.Set(int, int, bool okForNotOwner)`
(`ZDO.cs:394`) **ignores the flag entirely** in 1.0.16 and writes regardless, so the server
can tag an object a client owns. Fields are per-key, so the owner's own writes never clear
it, and vanilla saves ZDO fields with the world — verse membership survives a restart with
no persistence of ours and no chance of a side table drifting from the world.

**The authorship rule, and the trap it replaced.** A verse is assigned once, where an object
is created: `ZDOMan.RPC_ZDOData` only calls `CreateNewZDO` for a ZDOID the server has never
seen, so a creation inside that call is something that client just built, dropped, spawned or
tamed. Buildings, dropped items, tamed animals, spawned creatures and the player's own
character are all covered with no per-prefab special cases.

The first version of this tagged from the *current owner* instead, and that was wrong in a
way worth recording: **ownership is not authorship.** `ZDOMan.ReleaseNearbyZDOS` does
`SetOwner(uid)` on every persistent ZDO in a player's active area, every two seconds
(`ZDOMan.cs:930`), so ownership tracks whoever is standing nearest and says nothing about who
made the thing. Tagging from it tagged the whole landscape as the first player walked through
it — a test run showed `265350 zdos considered, 265350 hidden` and a second verse whose world
had no trees in it. There is deliberately no "inherit from owner" helper in the code now.

**Untagged means visible to everyone.** Terrain, vegetation and locations are generated by
vanilla code that knows nothing about verses, so they are shared. Confirmed in testing: a
workbench built in verse 2 is correctly invisible in verse 3, but trees chopped down in verse
2 are *also* gone in verse 3. One verse can strip another's landscape, permanently and
cumulatively, and the verse that did not chop gets the hole without the stump or the logs
(those are client-created, so they are tagged and private).

Two distinct leak classes, which need different fixes:

| Leak | Mechanism | Examples |
| --- | --- | --- |
| **Destruction** | The shared ZDO is removed for everyone (`ZDOMan.HandleDestroyedZDO`) | Felled trees, destroyed rocks |
| **Mutation** | A field on the shared ZDO is changed for everyone | `s_picked` on berries and mushrooms (`Pickable.cs:86` — a flag, not a destroy), `s_health` on part-mined ore (`MineRock5.cs:203`), chest inventories, door state |

### Why attacks and area effects cannot cross a verse

Worth writing down, because it is the question everybody asks and the answer is structural
rather than a rule anybody enforced: **if the Queen is swinging at a spot in verse A, standing
on that spot in verse B is safe.** Two independent barriers, either of which would be enough.

**1. The target does not exist where the hit is computed.** Valheim resolves hits on the
*attacker's* client, against instantiated colliders:

| Path | Detection |
| --- | --- |
| Melee and projectiles | the attacker's own client, against collider hits |
| Area effects | `Aoe.cs:454` — `Physics.OverlapSphereNonAlloc` / `OverlapBoxNonAlloc` on the client running the effect |

A client only instantiates ZDOs the server actually sent it, and `ShouldSend` never sends
another verse's. So verse B's player has no collider in the physics scene where verse A's
Queen is swinging. Nothing is blocked; there is simply nothing there to overlap. The same
applies in reverse to a player's own area effects, buffs included: an overlap query cannot
return a collider that was never created. Note that the server never does this work at all —
it holds ZDOs and no `GameObject`s, so it has no physics scene in which the two verses could
ever meet.

**2. The delivery would be dropped anyway.** Both damage and status effects are applied by a
routed RPC addressed to the *target's* `ZNetView`:

- `Character.Damage(HitData)` → `m_nview.InvokeRPC("RPC_Damage", hit)` (`Character.cs:2237`)
- `SEMan` → `m_nview.InvokeRPC("RPC_AddStatusEffect", …)` (`SEMan.cs:151`)

Both cross `ZRoutedRpc.RouteRPC`, where the relay already drops anything whose sender and
recipient are in different verses. So even a path that somehow found a cross-verse target
could not deliver to it.

The corollary is that you cannot *help* someone in another verse either — no buffing a friend
across the boundary, no reviving them, no shared healing. That is the intended shape: to play
together you join the same verse.

**One real artifact, from the same root as spike 7.** A shared spawner — a fire geyser, a
dungeon spawner — is owned by whoever stands nearest, and the creature it spawns is created by
that client, so it is tagged to *that* verse. Verse B can stand at a shared geyser and get no
Surtlings at all because verse A's player holds the spawner. Ownership is not authorship, and
here it decides who gets the monsters.

### Spike 3, reconsidered: copy-on-write beats per-verse generation

The original plan was to generate the world separately per verse. That fixes both classes,
but it multiplies the ZDO count by the number of verses — in a process whose ceiling is
already the send scheduler, and whose memory already holds every verse at once. It makes the
scaling problem worse in proportion to the thing being fixed.

**Copy-on-write is the better shape.** Keep one shared pristine world, and give a verse a
private copy of an object only when that verse actually changes it:

- *Destruction*: do not destroy a shared ZDO. Record that this verse can no longer see it and
  hide it in `ShouldSend`, leaving the original intact for everyone else. Interception point
  is `ZDOMan.RPC_DestroyZDO(long sender, ZPackage)` (`ZDOMan.cs:1002`) — it carries the
  sender, so the verse is known.
- *Mutation*: when changed data for an untagged ZDO arrives from a peer, split it. The hook
  is `ZDOMan.RPC_ZDOData`, already where authorship is assigned.

**Which way round the mutation split goes is the whole trick.** The obvious direction — put
the shared object back as it was and give the changing verse a copy — does not work.
`ZDO.Deserialize` *adds* fields (`ZDOExtraData.Add`) rather than replacing them, so restoring
a snapshot taken before the change leaves the change in place, and nothing public empties a
ZDO's field set.

So the roles are swapped: the object that was just changed is **kept by the verse that
changed it**, and a fresh pristine copy is built from the snapshot for everyone else. The
copy starts empty, so no clearing is needed. Better still, the player who acted keeps the
exact object they interacted with under the ZDOID their client already holds — nothing blinks
for them, and no stale copy is left on their machine to be re-sent and re-forked. Divergence
peels off repeatedly: the first verse to pick a bush takes that bush and leaves a new one, the
second takes that one and leaves a third.


#### Two draugr, one each — and the duplication bug that was hiding here

The intent is worth stating plainly, because it is the test to run: **party A fighting a
draugr to half health must leave party B an untouched one.** Walking into the same crypt later
should look as though A had never been there.

That is what the mutation fork already does, and the *direction* is why. The pristine copy is
built from the snapshot taken before A's first change, so it is the never-touched creature; A
keeps the damaged one under the ZDOID its client already holds. The copy B inherits is
therefore full health, not half, however long A fought it. Divergence peels off repeatedly, so
a third verse gets a fresh one from B's copy in turn.

Checking that claim turned up a real bug in both fork paths. The pristine copy is created
**untagged**, and `ZdoVerse.VisibleTo` sends untagged objects to *everybody* — including the
verse that just forked away from it. So A would have been sent both: the draugr it had fought
to half health, and a second untouched one standing next to it. Two draugr in one verse, which
is the opposite of the point. Both `Fork` and `Claim` now call `HideMask.Hide(pristine, verse)`
on the copy, which is exactly what the mask is for and persists with the world like the tag
does.

So the end state, with the fix: A has a draugr at half health, B has one at full, neither can
see the other's, and each verse sees exactly one.


#### Checking it without a client: the self-test

`Verse.SelfTest` (or `VERSE_SELFTEST=1`) asserts all of the above on an empty server, because
the alternative is two people in one dungeon and the failure modes are invisible until then.

`ISocket` is a public interface and `ZDOMan.AddPeer` takes any `ZNetPeer`, so two peers can be
synthesised in two verses — added to `ZNet`'s peer list as well as `ZDOMan`'s, because that is
where `Divergence` looks up which verse an incoming update came from. `Peers.Pretend` places
them without touching the registry, so no invented accounts are left in `verses.json`. The
damage is then fed through the **real** `ZDOMan.RPC_ZDOData`, hand-built in vanilla's wire
format, which is the only way to exercise the snapshot-and-fork rather than a convenient
restatement of it:

```
--- Verse: self-test (copy-on-write) ---
  PASS  the shared creature starts untagged
  PASS  and is visible to both verses
  PASS  damaging it forked the object in two
  PASS  A keeps the one it fought
  PASS  A's creature is at half health
  PASS  the copy left behind is untouched
  PASS  the copy belongs to nobody yet
  PASS  A is not shown the copy as well
  PASS  A still sees the one it fought
  PASS  B cannot see A's creature
  PASS  B sees the untouched copy
  PASS  B's swing claims it rather than being dropped
  PASS  claiming forked it again
  PASS  B now owns what it hit
  PASS  B's creature is still at full health
  PASS  B is not shown its own leftover copy
  PASS  a third verse would still find an untouched one
  PASS  A and B each see exactly one creature, at their own health
  all 18 checks passed; 4 throwaway object(s) destroyed
```

**The test was checked against the bug it exists for.** With `HideMask.Hide(pristine, _verse)`
commented out of `Fork`, it fails on exactly one line — `A is not shown the copy as well` —
and passes again when it is restored. A test nobody has seen fail is a test nobody should
trust.

Two notes on how it is built, both of which were mistakes first. Objects are counted as
"objects of this prefab the test did not make itself" rather than by a before-and-after total,
because `Damage` needs a throwaway ZDO of the same prefab to produce a serialised field set,
and counting totals made the result depend on dictionary order. And everything it creates is
non-persistent and destroyed in a `finally`, so a failure part-way through still leaves the
world clean.

Three details that are easy to get wrong:

- The replacement is created *inside* `RPC_ZDOData`, where `Authorship` would otherwise tag it
  as the sending player's own work. `Authorship.Suspend()`/`Resume()` brackets the creation, a
  counter rather than a flag so Harmony's patch ordering cannot matter.
- The replacement must be **hidden from the verse that forked away from it**, or that verse is
  sent both copies — untagged means visible to everybody. See above; this was a live bug.
- If the fork path ever throws, forking stops for the session and the leak returns. A shared
  world that is too visible beats one being corrupted object by object, and `Verse.DivergeOnChange`
  turns it off deliberately.

Divergence then scales with *player activity* rather than with verses × explored area, which
is the right axis. Both halves need the same per-verse "cannot see this" mask, so that is the
piece to build first.

**Known risk to test:** after a fork the client still holds the original ZDOID, so it will be
told the original is gone and the copy has arrived. Expect the object to blink and reappear.
Harmless for a tree being felled; needs checking for a chest being looted.

**Identity.** The registry is keyed on the platform user ID, not the peer's `m_uid`, because
a uid is per-connection and the whole design rests on surviving a reconnect.
`ZNet.RPC_PeerInfo` (`ZNet.cs:943`) reads that ID off the socket and verifies it against a
Steam session ticket before accepting the peer, so a client cannot claim someone else's.

## Changing verse: kick and route

A connected client has already cached its old verse's ZDOs, and making it forget them in
place is fiddly. Disconnecting is both simpler and exact, because a client rebuilds its ZDO
state from scratch on connect:

1. `!verse join 7` → validate, write the new verse into the registry, tell them over chat,
   disconnect.
2. They reconnect **to the same address**. `AddPeer` reads their verse from the registry and
   tags the connection before the first `CreateSyncList` runs.
3. `ShouldSend` filters to verse 7; `SendLocationIcons` gives them verse 7's spawn.

Two wrinkles, both confirmed:

- **The disconnect message is canned.** `rpc.Invoke("Error", n)` selects from a vanilla enum
  (3 version, 5 not authenticated, 8 blacklisted, 9 full). "Rejoin for verse 7" cannot be put
  on the disconnect screen without a client mod, hence sending it over chat first.
- **Logout points live on the client.** `FindSpawnPoint` prefers
  `m_playerProfile.GetLogoutPoint()`, which is in the player's own character profile and is
  not ours to clear. Someone joining verse 7 respawns at their *old base's* coordinates
  inside verse 7 — possibly inside terrain or in a stranger's hall. The fix is to teleport
  them once their character ZDO appears: `Character.cs:713` registers `RPC_TeleportTo` on the
  player's own `ZNetView`, so the server can route it by ZDOID even though it holds no
  instance out there.

**Characters are client-side.** Valheim keeps inventory and skills in the player's own
profile, so players carry gear and skills between verses and no server-side code can wipe
them. With open verses that means a fully geared stranger can join a fresh verse. A product
decision, not a bug.

## Scaling

Two ceilings, and the measurements since this was written have swapped which one binds first.

### The scheduler, and who owns it now

`ZDOMan.SendZDOToPeers2` (`:886`) starts a round every 50 ms and then services **exactly one
peer per frame**, so a round costs `50ms + N × frametime`. The original version of this table
assumed 60 fps; the dedicated server actually runs at **~30 fps** — derived from `Firehose`
reporting 15.0 rounds/s off a 50 ms timer, which only happens if the timer is crossed every
second frame. Every figure here is therefore twice what was first written:

| Concurrent players | Round period @30fps | Updates/sec each |
| --- | --- | --- |
| 10 (vanilla's cap) | ~380 ms | ~2.6 |
| 18 (3 full parties) | ~650 ms | ~1.5 |
| 36 (6 full parties) | ~1.25 s | ~0.8 |
| 100 | ~3.4 s | ~0.3 |

More hardware barely helps: the frame rate would have to double to halve this. It is why
`ZNet.cs:1038` hard-codes `GetNrOfPlayers() >= 10` — a trivially patchable constant, but
roughly where the engine was tuned.

Nothing justifies the one-per-frame spread: `SendZDOs` (`:1057`) already bounds itself per
peer by checking the socket send queue and returning early. **Spike 0 is done, and it does not
live here any more.** The `Firehose` plugin in this repo replaces the same method for its own
reasons — portal arrivals were slow because vanilla's 10 KB send window is consumed by
bytes awaiting acknowledgement, so throughput was `window / round-trip` — and two plugins on
`SendZDOToPeers2` would service every peer twice a round. Verse's `SendScheduler.cs` and its
`FairSend` setting were retired; Firehose owns scheduling, reports throughput per player in
bytes, and stands down if anything else patches the method. Verse's own counters say which
scheduler is driving them. See [portal-sync.md](portal-sync.md).

### Memory, which is the real wall on a 4 GB box

Measured on the live server (2026-10-01, 987,000 ZDOs, 4 players):

| | |
| --- | --- |
| Valheim RSS, 0 or 4 players | **2.00 GB** — flat; it tracks the world, not the players |
| Same build, near-empty world | 1.40 GB — so ~600 bytes per ZDO |
| `mem_total` / peak `mem_used` | 4.11 GB / 2.76 GB, swap untouched |
| Busiest single core, 4 players | 18.6% peak |

So one explored world costs ~0.6 GB of ZDOs on top of a ~1.4 GB floor, leaving roughly
**1.2 GB of headroom — one or two more fully-explored verses, or eight-ish lightly-explored
ones** before it swaps. CPU is nowhere near binding. That reverses the original claim at the
top of this section: on this hardware the first wall is RAM, and the cheap fix is a bigger
droplet rather than a rewrite. Spike 5 still has to measure save size and save-pause growth,
but the memory half now has a number.

Other costs that scale with peer count, all on the one Unity main thread:

- `ZoneSystem.Update` runs at 10 Hz and calls `CreateGhostZones` for **every peer**
  (`ZoneSystem.cs:1181`). Ten clustered vanilla players share a region; 36 players in
  separate verses mean 36 regions × 9–25 zones each, all instantiated.
- `ReleaseZDOS` walks every peer every 2 s; `ZDOMan` has ten `foreach (ZDOPeer …)` loops.
- Memory holds every verse's ZDOs for all explored area at once — see above for what that
  costs.

**Honest expectation:** single-process instancing is a 12–18 player architecture, and on a
4 GB box it runs out of memory before it runs out of scheduler. Past roughly 40 players, or
more than a couple of heavily-built verses, process-per-verse wins.
## Status

Built, in `src/Verse`:

- **Spike 1** — `Isolation.cs`. The `ShouldSend` filter, verse-aware RPC relay and per-verse
  player list. `ZdoVerse.cs` is the tag and the visibility rule, `Authorship.cs` assigns a
  verse at creation, `Peers.cs` maps a connection to a verse, `Verses.cs` is the registry.
  **Passed**: a workbench built in verse 2 is correctly absent in verse 3, and a live log
  shows `2250 zdos considered, 2250 hidden` for a player alone in a fresh verse.
- **Copy-on-write** — `HideMask.cs` and `Destruction.cs` for the destruction half: a verse
  felling a shared tree keeps it standing for everyone else and stops being sent it itself.
  **Confirmed in play.** `Divergence.cs` for the mutation half: picking, part-mining and
  looting a shared object hands that object to the verse that touched it and leaves a pristine
  one behind. Written but not yet play-tested.
- **Spike 4** — `Spawns.cs`. A player who changes verse is put down in the new one instead of
  at the logout point they kept from the old one. See below for why this is a teleport and not
  the per-verse location icons originally proposed. A verse's arrival point defaults to the
  world's start temple and the leader moves it with `!verse spawn`, so an invited player lands
  at the base. **Built; the teleport itself needs a play test.**
- **Spike 7** — `Divergence.Claim`, called from the relay. A verse reaching for a shared
  object another verse owns gets its own copy instead of silence, which is what makes shared
  dungeon creatures fightable. The same hook takes back a verse's own object when
  `ReleaseNearbyZDOS` has handed it to a neighbour by position. **Built; needs a play test.**
- **Spike 6** — `Keys.cs`. Progression per verse: a boss kill is attributed to the verse that
  earned it instead of the world, and each peer is sent its own verse's keys unioned with the
  operator's. The split is vanilla's own enum boundary, `gk < GlobalKeys.NonServerOption`.
  `EnsurePlayerEvents` sets the `PlayerEvents` world modifier at startup, which is a
  prerequisite rather than a preference. **Built; covered by the self-test.**
- **Spike 12** — `Migration.cs`. Turns a world that was played before the plugin existed into
  one verse, tagging by `s_creator`/`s_tamed` and seeding members from the world's player
  history. Dry run by default. **Built; exercised locally.**
- **Scattered starts** — `Spawns.Scatter`, off by default (`Verse.ScatterNewVerses`). Every
  verse starts at the world's temple like any other; turning it on begins a new verse a couple
  of kilometres out instead, at an angle derived from its id, which only matters on a migrated
  world where pre-migration destruction cannot be undone.
- **Spike 10** — `Sleep.cs`. The night is skipped when more than `SleepFraction` of the server
  is in bed, instead of vanilla's unanimity, which two populated verses can never reach. The
  rule is in `SleepRule.cs` and covered by `./test.sh`.
- **The command system** — `Commands.cs` (`status`, `help`, `open`, `password`, `invite`,
  `private`, `join`, `leave`, `spawn`), `CommandText.cs` for the parsing, and
  `VerseIdentity.cs` for the player-list entry that makes a lone player's chat reach the
  server at all. Changing verse disconnects the player with an explanation in chat first, and
  they rejoin on the same address — the kick-and-route flow above. `CommandText.cs` is
  compiled into the test project, so the parsing is covered by `./test.sh`. `leave` always
  starts a fresh verse, even for a player already alone in theirs, which makes it the quickest
  way to move an account between verses when testing - no editing the registry by hand.
- **`!warp`** — `Warp.cs`, a second command word (`Verse.WarpCommandWord`) alongside
  `!verse`. Unlike the verse command it acts immediately rather than on reconnect, since the
  player is already spawned: `warp spawn` resolves the same point `Spawns` would put an
  arrival down at, and `warp bed` finds a bed the account has claimed - see `Beds.cs` for how
  that is found from server-visible ZDO fields alone, with no client cooperation.
- Counters every 10 s (`Verse.Metrics`) — `hidden` / `blocked` / `destroyed` / `masked` /
  `forked` / `claimed` / `reclaimed` / `placed`, plus `updates/s per peer` and which plugin is scheduling the sends.
- A startup check. Every hook reaches a private member by name; Harmony throws on an
  unresolvable *patch target*, but the reflective lookups inside the patches would fail
  silently and quietly stop isolating. They are resolved once at boot and reported, loudly.

**Spike 0 was retired, not abandoned.** Send scheduling is the `Firehose` plugin's job now;
see the Scaling section.

### Why per-verse arrival is a teleport, not location icons

The original plan was per-verse `SendLocationIcons`. That cannot work.
`Game.FindSpawnPoint` (`Game.cs:524`) resolves a spawn in three steps and reads all three off
the **client's own profile**:

1. a logout point, if the player has one and did not just die;
2. a custom spawn point — a bed — which needs a real `Bed` object within 5 m or the client
   clears it and falls through;
3. otherwise `ZoneSystem.GetLocationIcon(m_StartLocation)`, from the icon list the server sent.

Only step 3 is the server's to influence, and since every verse sits at the same coordinates,
per-verse icons would change nothing today — they only start to matter if verses ever get
their own seeds. Step 2 already behaves correctly by accident: a bed in another verse is
hidden by the ZDO filter, so `FindBedNearby` fails and the client gives up on it (at the cost
of the player having to re-sleep). Step 1 is what actually bites, and it is pure client state
no server-side plugin can read or write.

So the server lets the client spawn wherever it likes and then **moves** it. `Character`
registers `RPC_TeleportTo` on the player's own `ZNetView` (`Character.cs:713`) and acts on it
only if the receiver owns that character — which the client does — so a routed RPC addressed
to the character's ZDOID is enough, and the server needs no `GameObject` for the player. The
move is sent as a *distant* teleport so the client runs its own fade-and-reload rather than
being dropped into terrain it has not loaded. A move is only warranted when the verse changed,
which the server knows exactly: every verse change goes through the kick-and-rejoin, so that
account is noted and the next character it spawns is moved once.

**Not built yet:**

- Raising the 10-player cap at `ZNet.cs:1038`. (The `GetNrOfPlayers` patch in
  `VerseIdentity.cs` is unrelated — it subtracts Verse's own player-list entry so the speaker
  does not eat a slot.)
- Invites only resolve a name for a player who is online, since the registry is keyed on the
  account id and a name is only tied to one while they are connected.
- Per-verse world generation. Every verse is the same map at the same coordinates.
## Shared world state that is not isolated yet

Two parties fighting their own copy of the same boss is the case that exposes what is left,
and it is worth working through because most of it already functions.

**The boss itself is already per-verse, by construction.**
`OfferingBowl.InitiateSpawnBoss` calls `m_nview.InvokeRPC("RPC_SpawnBoss", point, …)` with no
target, so it runs on **whoever owns the altar's ZDO**, and `RPC_SpawnBoss` early-returns
unless `m_nview.IsOwner()`. That owner is always a client. The client instantiates the boss,
so the boss's ZDO is *created by that client*, so `Authorship` tags it with that client's
verse and `ShouldSend` hides it from every other verse. Two Queens stand at the same
coordinates, invisible to each other, each simulated by its own party. They cannot collide:
the server holds ZDOs and no `GameObject`s, so there is no server-side physics to interfere.
Loot, trophies and the fight itself all follow the same path. Untested, but the mechanism is
the one already proven for player-built objects.

### Leak 1: global keys are world-wide (spike 6)

`Character.OnDeath` calls `ZoneSystem.instance.SetGlobalKey(m_defeatSetGlobalKey)`
(`Character.cs:2986`), which routes to the server, lands in a single key set, and is then
broadcast to **every peer in every verse** by `SendGlobalKeys(0L)` (`ZoneSystem.cs:3128`).
So:

- Verse A killing the Queen sets `defeated_queen` for verse B as well. Their boss is already
  beaten as far as the world is concerned, and `Vegvisir`, `Trader` stock and the spawn tables
  all read those keys.
- `RandEventSystem` gates raids on them (`m_requiredGlobalKeys`, checked at `:526`–`:535`), so
  one party's progress changes another party's nights.
- `GlobalKeys.activeBosses` is a shared *numeric* key: `BaseAI` increments it when a boss is
  alerted (`BaseAI.cs:1571`) and `Character` decrements it on death (`Character.cs:2995`). Two
  verses fighting bosses at once corrupt each other's counter — and that counter is what
  suppresses random events during a boss fight.

This is wrong by construction rather than merely unproven, which makes it the largest
outstanding item.

**The shape of the fix.** Keys become per-verse, sent as a union with the genuinely global
ones:

| Hook | Why |
| --- | --- |
| `ZoneSystem.RPC_SetGlobalKey` (server) | attribute the key to `Peers.VerseOf(sender)` instead of the world |
| `ZoneSystem.RPC_RemoveGlobalKey` (server) | the same, so a removal does not clear it for everybody |
| `ZoneSystem.SendGlobalKeys(long peer)` (`:720`) | already per peer — send that peer's verse set plus the world set |

Three implementation notes that are easy to get wrong:

1. The state is **three parallel structures**, not one: `m_globalKeys` (a `HashSet<string>`
   whose entries may be `key=value`), `m_globalKeysEnums` (`HashSet<GlobalKeys>`) and
   `m_globalKeysValues` (`Dictionary<string,string>`), all at `ZoneSystem.cs:559`–`563`. A
   per-verse swap has to replace all three coherently or `GetGlobalKey(GlobalKeys)` and
   `GetGlobalKey(key, out value)` will disagree.
2. Keys carry **values**, so a per-verse store keeps `activeBosses=2`, not just a name.
3. `Game.UpdateWorldRates(m_globalKeys, m_globalKeysValues)` (`:797`) derives world rates from
   the key set and is process-global. Per-verse *rates* are therefore not in scope here; see
   the open question below.

**Where the keys should live: the registry, not a ZDO tag.** Three reasons, and the third is
the one that decides it:

- There is no object to tag. Global keys are not objects, so a ZDO carrier would have to be
  invented, and ZDOs are sector-scoped while keys are world-scoped. It would also put
  progression state inside the very thing the isolation filter is deciding about.
- The registry is already the home of verse state, already persisted beside the world save,
  already loaded before the first peer connects, and already keyed by something that survives
  the reconnect a verse change is built on. A dozen `defeated_*` strings and a numeric or two
  per verse is nothing next to what it already holds.
- **Vanilla's own set should stay, and stay global.** Server option keys and world modifiers
  (`ServerOptionsGUI`, and everything `UpdateWorldRates` reads) are *operator* settings, and
  they genuinely do apply to every verse. So the split is not an imposition on the code, it
  falls out of it: vanilla's `m_globalKeys` keeps the world-wide keys, `Record.Keys` holds the
  per-verse progression, and each peer is sent the union. Keeping them in one place would mean
  inventing a way to tell the two kinds apart.

### Leak 2: interaction without mutation (spike 7)

The altar is world-generated, so it is untagged and shared — and ownership of a shared ZDO
goes to whoever stands nearest, reassigned every two seconds by `ReleaseNearbyZDOS`.
*Ownership is not authorship.*

So when verse A's player makes the offering while verse B's player happens to own that altar,
`RPC_SpawnBoss` is routed cross-verse and the relay in `Isolation.cs` **drops it**. Nothing
spawns, and the player sees no error at all — just a dead altar.

`Divergence` does not help: it forks a shared object when its *data* changes, and an RPC that
only triggers behaviour never writes a field. The same hole applies to `BossStone` (claiming a
boss power) and to anything else driven by RPC-to-owner rather than by a ZDO write, which is a
common shape in this codebase.

**The shape of the fix** belongs in the relay itself. `ZRoutedRpc.RouteRPC` is already the one
place that sees the sender's verse, the target ZDOID and its owner. When a verse's player
addresses a shared ZDO owned by another verse, fork that ZDO to the sender's verse and hand
them ownership *before* relaying, instead of dropping. That turns a silent failure into "each
verse gets its own altar the first time it touches one", which is the same rule copy-on-write
already applies to picking a berry.

### Leak 3: raids are one event, server-wide (spike 9)

There is **one** event timer and **one** event slot. `UpdateRandomEvent` rolls every
`m_eventIntervalMin × 60 × Game.m_eventRate` seconds (a minute, base), `m_randomEvent` is a
single field, and when one is chosen the server broadcasts
`InvokeRoutedRPC(0L, "SetEvent", name, time, pos)` — target 0, everyone. Each client then
decides whether it is in the event purely by distance:
`Utils.DistanceXZ(position, re.m_pos) < re.m_eventRange` (96 m by default). No verse check, no
eligibility check.

Three consequences:

1. **Frequency dilution.** Two parties can never be raided at once, and the event is placed at
   one position. With N parties each sees roughly 1/N of the raids they would get alone, and a
   raid in progress holds the slot for its duration. More parties, quieter nights.
2. **A co-located verse is dragged in.** Every verse is the same map and the start temple is
   the same spot, so two parties building in the same region is likely rather than
   theoretical. The broadcast reaches both, and the distance check does not care which verse
   anybody is in. The *monsters* are per-verse — spawns come from the zone-owning client, so
   `Authorship` tags them — so the second party fights its own real copy of a raid it never
   earned.
3. **The difficulty follows the wrong party.** Eligibility comes from `HaveGlobalKeys` reading
   the single world key set, so a fresh group can draw a Fuling army because somebody else
   killed Yagluth.

#### `GlobalKeys.PlayerEvents` is a prerequisite, not a nice-to-have

Set that key and `HaveGlobalKeys` short-circuits to "any individually eligible player", while
`GetValidEventPoints` only places an event at the position of a player whose own
`possibleEvents` contains it. That list comes from each client's `m_serverSyncedPlayerData`,
computed client-side by `PlayerIsReadyForEvent` from known items and player keys — **per-player
progression, not world keys**. Under verses, per-player is per-party. It is an operator /
world-modifier key, so it belongs exactly where world rates already live: global, set once.

**Spike 6 breaks raids without it.** Nearly every raid lists some `defeated_*` in
`m_requiredGlobalKeys`. Move those keys out of vanilla's set into the registry and, with
`PlayerEvents` off, every raid fails its check and raids stop entirely — silently. With it on,
gating no longer consults the world set, and spike 6 is safe. Spike 9 needs it for the same
reason: without it, every verse's selection returns the same answer and you get per-verse
*timing* with identical, progression-blind raids.

#### Making events purely per-verse

Two details in the code make this much cheaper than it looks. **Vanilla already clones**:
`SetRandomEvent` does `m_randomEvent = ev.Clone()` (`:383`), so the live event is a per-instance
copy with its own `m_time` and `m_pos` — hold one clone per verse exactly as vanilla holds one.
And **there is already a heartbeat**: `UpdateRandomEvent` ends with a 2-second `m_sendTimer`
→ `SendCurrentRandomEvent()`, so the current event is re-sent continuously and late joiners
need no special handling.

The seam is a single Harmony prefix on the private `UpdateRandomEvent(float dt)`, server-only.
That method contains the whole engine — the roll, the standalone-interval events and the send.
Everything downstream in `FixedUpdate` goes inert by itself, because `m_randomEvent` is only
ever set from inside the method being skipped: keep per-verse clones, never touch the vanilla
field, and the rest has nothing to do. `UpdateForcedEvents` keeps working untouched. That is a
far smaller blast radius than the rest of this plugin.

State is one entry per verse — clone, time, roll timer, standalone timers — and per verse per
tick:

| Step | How |
| --- | --- |
| Roll | own timer against `m_eventIntervalMin × 60 × Game.m_eventRate`, then `m_eventChance / Game.m_eventRate` |
| Select | load the static `s_playerEventDatas` with **only that verse's** peers, then call vanilla's own `GetPossibleRandomEvents()` and pick at random as `StartRandomEvent` does; restore the static afterwards |
| Start | `ev.Clone()`, set `m_pos`, `OnStart()` |
| Advance | `time += dt` unless `m_pauseIfNoPlayerInArea` and no peer *of that verse* is within `m_eventRange`; end at `m_duration`, `OnStop()`, send the clear |
| Send | replace the `0L` broadcast with a per-peer send of that verse's `(name, time, pos)` |
| Standalone events | the same, with per-verse timers — which also stops vanilla mutating `m_time` on the shared templates, which it does today at `:152` |

The selection step is the one worth dwelling on: `GetPossibleRandomEvents()` takes no
arguments and reads that static, so swapping the list buys **all** of vanilla's gating —
biome, `CheckBase`, global keys and the `PlayerEvents` path — for about twelve lines of
verse-filtered `RefreshPlayerEventData`. The only other reimplementations are a verse-filtered
`IsAnyPlayerInEventArea` (six lines) and the roll itself.

Clients need no changes at all. Each receives one event, believes it is the world's, and
spawns the monsters itself through its own zone ownership, so `Authorship` tags them without
any extra work.

**Cost:** one file of roughly 200 lines, one prefix, three small reimplementations, and
reflection for `s_playerEventDatas`, `GetPossibleRandomEvents`, `m_events`,
`m_eventIntervalMin` and `m_eventChance` — all resolvable at boot and reportable by the
startup check. A day to two, with a two-verse play test. **Still global afterwards:**
`Game.m_eventRate` and the world rates (accepted), `activeBosses` damping (rides on spike 6),
and console-forced events unless `UpdateForcedEvents` is replaced too.

## Audit: every channel that can cross a verse

Done by walking the decompiled assembly rather than by imagination, and the method matters
more than the list, because it is repeatable after a game update. Three questions, each one a
grep:

1. **Who creates the ZDO?** `Authorship` only tags inside `ZDOMan.RPC_ZDOData`, so anything
   the *server* creates is untagged and therefore shared by every verse. This is the biggest
   source of surprises.
2. **What does the server broadcast?** `grep "InvokeRoutedRPC(0L"`. The relay deliberately
   lets the server's own sends through, so every one of those reaches all verses.
3. **What does the server evaluate across all players?** `grep "GetAllCharacterZDOS"`. Any
   server-side caller is verse-blind by construction — that one call returns every character
   on the server.

### Who creates what

| Content | Created by | Shared between verses? |
| --- | --- | --- |
| Terrain, vegetation, locations, **dungeons and the creatures placed inside them** | the server, in `SpawnMode.Full` (`ZoneSystem.cs:1279`) | **yes** |
| Ambient wildlife | a client — `SpawnSystem.UpdateSpawning` needs `m_nview.IsOwner()` *and* a local player (`:160`) | no |
| Anything a player builds, drops, tames or spawns | that client | no |
| A boss from an altar | the altar's owning client, via `RPC_SpawnBoss` | no |
| The player's own character | that client | no |

The second row is the good news and the first is the bad: **ambient wildlife is per-verse, but
every creature that came with a dungeon or a location is shared.** That had not been noticed
before this audit, and it has a consequence worse than visibility — see spike 7 below.

### The channels

| Channel | Mechanism | Status |
| --- | --- | --- |
| Objects, buildings, creatures, players | `ZDOPeer.ShouldSend` | **handled** |
| Chat, damage, client-to-client traffic | `ZRoutedRpc.RouteRPC` | **handled** |
| Player names and positions | `ZNet.SendPlayerList` | **handled** |
| Destroying a shared object | `ZDOMan.DestroyZDO` broadcast | **handled** — `Destruction.cs`, masked not destroyed |
| Changing a field on a shared object | `RPC_ZDOData` | **built** — `Divergence.cs`, untested |
| Boss defeats, progression, `activeBosses` | one global key set, broadcast to all | spike 6 |
| RPC to the owner of a shared object — altars, boss stones, doors, **and damage** | `ZNetView.InvokeRPC` → `InvokeRoutedRPC(m_zdo.GetOwner(), …)` (`ZNetView.cs:333`) | spike 7 |
| Raids and random events | one timer, one slot, `InvokeRoutedRPC(0L, "SetEvent", …)` | spike 9 |
| **Sleeping and the night skip** | `EverybodyIsTryingToSleep` over `GetAllCharacterZDOS` | **spike 10** |
| **Mark Hall-Patton** | server-created ZDO, untagged; chat recipients chosen by distance only | **spike 11** |
| Mid-game location placement | `PersistentEventSystem` (`:115`) rejects a spot near *any* player | verse-blind, low impact |
| World time, weather, day count | one world clock; `EnvMan` derives from it | **shared by design** |
| Difficulty and world rates | `Game.UpdateWorldRates` from the key set | **shared by decision** |
| Connection slots, save pauses, memory | one process, one world file | shared resource — spike 5, and the cap at `ZNet.cs:1038` |
| Boss spawn / death / alert announcements | `MessageHud.MessageAll` from `BaseAI` (`:234`, `:713`, `:1577`) | **already contained** — those run on the owning *client*, so the relay fans them to that verse only |
| World-save warnings | `MessageAll` from the server (`Game.cs:687`, `ZNet.cs:1449`) | shared, and correctly so — the save does stop everybody |
| Dream cinematics | `CinematicsManager` on sleep | rides with spike 10 |
| Rider lookup | `Sadle` (`:373`) scans all characters | benign — it matches by `UserID`, it is a lookup and not an effect |
| `ZNetScene.SpawnObject` | broadcasts a spawn to everyone | **no callers in 1.0.16** — dead code, left alone |

### Spike 10: sleeping is server-wide, in both directions

`Game.UpdateSleeping` runs on the server every 2 s, and the gate is:

```csharp
private bool EverybodyIsTryingToSleep()
{
    List<ZDO> allCharacterZDOS = ZNet.instance.GetAllCharacterZDOS();
    if (allCharacterZDOS.Count == 0) return false;
    foreach (ZDO item in allCharacterZDOS)
        if (!item.GetBool(ZDOVars.s_inBed)) return false;
    return true;
}
```

**Every character on the server** must be in a bed. So with two verses populated, a party that
all climb into beds get nothing, because somebody in another verse is standing up — and the
more verses there are, the less likely sleeping ever works again. That is the failure mode
players report on day one, and it is the reverse of the usual leak: not one verse affecting
another, but one verse *preventing* another from doing anything.

And if everybody does happen to be in bed, `EnvMan.instance.SkipToMorning()` plus the
`SleepStart` broadcast apply to all of them.

**It cannot be made properly per-verse**, because time is one world clock: `SkipToMorning`
moves `EnvMan`, every client derives day, night and weather from the same synced time, and
per-verse time would mean per-verse weather and day count. So the choice is between:

- **(a) Leave it.** Nobody sleeps once a second verse is active. Unacceptable.
- **(b) Count only the sleeper's verse,** and accept that the skip is global. One small patch
  on `EverybodyIsTryingToSleep`, filtering `GetAllCharacterZDOS` by `Peers.VerseOf`. Beds work
  again; one party's bedtime still moves everybody's clock.
- **(c) Per-verse time.** Large, and it would fork weather and the day counter too.

**(b) is the recommendation**, as a deliberate concession in the same bucket as world rates.
Note the griefing angle it leaves: a party can repeatedly skip nights for the whole server, and
since raids happen at night, that cuts other parties' raids short. Worth a cooldown if anybody
abuses it — vanilla already has a 10-second one (`m_lastSleepTime`).


**Built, as (b) with a vote instead of a verse filter.** You asked for a majority rather than
"the sleeper's verse only", and that is better: it keeps one rule for the whole server, so
nobody has to reason about which verse's bedtime wins. `Sleep.cs` replaces
`EverybodyIsTryingToSleep` with a count over the same `GetAllCharacterZDOS` list and a
threshold. `Verse.SleepFraction` is read as *strictly more than* that share, so the default
0.5 is a genuine majority and not a tie; 1.0 restores vanilla, and 0 lets any single sleeper
decide — but never an empty room. The arithmetic lives in `SleepRule.cs`, which is Unity-free
precisely so `./test.sh` can cover the three edges that are easy to get wrong: a tie is not a
majority, "everybody" cannot be written as "more than 1.0", and nobody asleep must never skip
a night. Thirteen cases, all passing.

### Spike 7, extended: damage to shared creatures

The RPC-to-owner problem is worse than the altar case that first exposed it, because
`ZNetView.InvokeRPC(string, …)` addresses **the ZDO's owner** (`ZNetView.cs:333`) and damage
goes the same way. Put that together with "dungeon creatures are server-created and therefore
shared" and:

- A shared draugr in a crypt is sent to *every* verse, and owned by whichever single peer is
  nearest — `ReleaseNearbyZDOS` only reassigns a persistent ZDO when the current owner's
  active area no longer covers it, so the first verse to arrive keeps it while it stays.
- Verse B can see that draugr and swing at it. The damage RPC is addressed to verse A's peer,
  the relay drops it as cross-verse, and **nothing happens**. An invulnerable monster.
- Its AI runs on A's client, which cannot see B's players at all, so it never attacks them
  either. For B it is an inert, undamageable statue standing in the dungeon.

The fix is the same one spike 7 already proposes — fork the shared ZDO to the sender's verse
and hand over ownership instead of dropping the RPC — and applied to a creature it is exactly
copy-on-write: B gets its own draugr, A keeps theirs. One wrinkle to decide: the fork copies
the ZDO *as it stands*, so if A had already fought it down to a third of its health, B's copy
starts there too. Cloning from the prefab's full health instead would be more generous and
less surprising, at the cost of no longer being a straight ZDO copy.


**Built.** `Divergence.Claim` does the interaction fork, called from the relay in
`Isolation.cs` on the path that used to drop the message. It takes a snapshot of the object
exactly as it stands, builds the pristine copy for everyone else under `Authorship.Suspend()`,
tags the original to the acting verse, hands that verse ownership and re-addresses the RPC to
the actor so it is delivered rather than lost. The copy is hidden from the acting verse
(`HideMask.Hide`), without which that verse would be sent both it and the original — see the
two-draugr note under spike 3. `Verse.ClaimOnInteraction` turns it off for
comparison, and the 10-second metrics line counts `claimed`.

**And a second bug found while wiring it up, fixed at the same hook.** An object can be a
verse's *own* and still be owned by a peer in another one, because `ReleaseNearbyZDOS` hands
ownership to whoever is standing nearest, by position, every two seconds, and knows nothing
about verses (`ZDOMan.cs:930`). Since every verse sits at the same coordinates, a player's own
chest can end up owned by somebody who cannot even see it — and then refuse to open, because
the interaction RPC is addressed to that owner and dropped as cross-verse. The relay now
checks for this first: if the target already belongs to the sender's verse, it simply takes
ownership back and delivers. Counted separately as `reclaimed`, because it is a different
fault from a claim and the two would be confusing in one number.

**Still to settle in a play test:** `Character.RPC_Damage` returns early unless the receiver
owns the character, and the acting client does not learn it owns the object until the next ZDO
sync, so the first swing after a claim may be swallowed and the second land. That is the
behaviour to watch for — one wasted swing is acceptable, a permanently unresponsive creature
is not.

### Spike 11: Mark Hall-Patton crosses verses

The other plugin in this repo is itself a leak, and this is the kind of thing to check for any
plugin added later:

- `Historian.cs:95` creates his ZDO with `ZDOMan.instance.CreateNewZDO` **on the server**, so
  it is untagged and every verse sees him. He follows the player who summoned him, so other
  verses watch an old man in a wide-brimmed hat walking after nobody.
- `ServerChat` picks his recipients by distance over all peers, so a player standing near him
  in another verse hears his answers.

Both are small fixes in `HallPatton`: tag his ZDO with the summoner's verse when `Verse` is
loaded, and filter the recipient list the same way. They are only wrong when the two plugins
run together, which is not the case on the live server today.

## Migrating a world that was played first

Install this on a world people have already lived in and every object in it is untagged,
which means "shared by every verse" — somebody's base visible to strangers, and forked out
from under them the first time anyone changed anything. `Migration.cs` turns that world into
one verse, once, behind `Migration.MigrateLegacyInto` (0 = off; set it to the verse id the old
world should become).

**It does not guess what players made, because vanilla already records it.** Every
hammer-placed object stores its builder in its own ZDO — `Piece.SetCreator` writes
`ZDOVars.s_creator` (`Piece.cs:434`) — and tamed animals carry `s_tamed`. Those two fields are
the whole discriminator, and they are vanilla's own record of authorship, which is the
principle `Authorship` already works on. Guessing from prefabs would have been wrong in a
specific and ugly way: village ruins, stone circles and dungeon walls are `Piece`s too, placed
by locations rather than by people. They have no creator, so they stay shared — which is what a
new verse needs in order to have a world at all.

**Nor does it guess who played.** `ZNet.World.m_playerHistory` is persisted with the world
metadata (`World.cs:235`), and each entry's `PlatformUserID.m_userID` is *exactly* the string
the registry is keyed by — the same bare id `ZSteamSocket.GetHostName` returns. Those accounts
become the verse's members, so they connect straight into their own world with nothing to type.
Everyone else keeps the behaviour that was already there: an account the registry has never
seen gets a fresh private verse.

**The progression moves with them.** The keys earned so far are lifted out of the world set
into that verse's record, so new verses do not inherit somebody else's bosses. This is the one
moment that split is free; after new verses exist, there is no clean line to draw.

| | |
| --- | --- |
| Tagged to the legacy verse | anything with a creator, plus tamed animals |
| Left shared | terrain, vegetation, locations, dungeons, loose dropped items, terrain levelling |
| Members | every account in the world's player history |
| Progression keys | moved from the world set into the verse |

**Dry run first, and it is the default.** `Migration.MigrateDryRun` surveys and reports
without writing, because the tag goes into the ZDO field set and therefore into the world save
— so the only rollback is restoring the world. Read the report, check the member list is who
you expect, back the world up, then set it to false. It refuses to run twice (the `Legacy`
flag on the record is the marker) and refuses to run with anybody connected.

```
--- Verse: migration DRY RUN (nothing will be written) ---
  objects in world : 18758
  player-built     : 0   (ZDOs with a creator - everything a hammer placed)
  tamed animals    : 0
  left shared      : 18717   (terrain, vegetation, locations, dungeons - what new verses need)
  already in a verse: 41   (left alone)
  members          : 1
      <name> (7656119...)
  progression keys : 0
```

### What a new verse inherits, and what it does not

Worth being plain about, because it was the first question asked of this design.

| Mechanic | Before spike 6 | After |
| --- | --- | --- |
| Boss defeats | all of the old party's | its own only |
| Creature spawn tables (`SpawnSystem.cs:230`) | post-progression monsters in starter biomes | its own progression |
| Raids | endgame raids on a first base | its own progression |
| Trader stock, boss map markers | fully unlocked | its own progression |
| World modifiers, difficulty, world level | shared | shared, deliberately |
| Time of day, weather, day count | shared | shared, one world clock |
| Player skills, gear, recipes, map | carried in | carried in — client-side, unfixable |

Bosses can be re-fought: `CanSpawnBoss` only looks for a valid spot, with no key check
anywhere in the path, so a new verse summons Eikthyr again and earns its own kill.

**And the limit that remains.** Destruction that happened *before* the plugin existed is gone
rather than hidden — trees felled, ore mined out, world chests emptied. A new verse inherits
that stripped ground unless `Verse.ScatterNewVerses` is turned on, which starts each new verse
a couple of kilometres out instead, at an angle derived from its id so it is stable across
restarts and no two verses share a spot. Off by default, so every verse starts at the same
temple; the damage is local to where the old party played, and a ten-kilometre map has plenty
of elsewhere for the rare world where it is worth turning on.

## Remaining spikes

| # | Question | State |
| --- | --- | --- |
| 0 | Does fair send scheduling lift the ceiling, and by how much? | **retired** - it is `Firehose`'s now, and measured there: 601 KB in 4.0 s against 9 s vanilla |
| 1 | Does the ZDO filter actually hide a verse? | **passed** - player-built objects isolate correctly |
| 2 | Kick-and-route: does a reconnect land you in the right verse? | built, needs a play test |
| 3 | Copy-on-write: destruction **passed**; mutation and the interaction claim now **pass the self-test** on a synthetic two-verse dungeon | built, still wants a real play test |
| 4 | Per-verse arrival | **built** (`Spawns.cs`) - the teleport needs a play test |
| 5 | Scaling: RSS, save size, save pause, `CreateSyncList` cost at 20+ verses | memory half **measured** (~600 B/ZDO, ~1.2 GB headroom); save size and save pause still open |
| 6 | Per-verse global keys - so one party beating a boss does not beat it for everyone | **built** (`Keys.cs`) - covered by the self-test, needs a play test |
| 7 | Interaction without mutation - fork a shared object on a cross-verse RPC instead of dropping it | **built** (`Divergence.Claim`, called from the relay) - needs a play test |
| 9 | Per-verse raids - own timer, own event, own targets | **not built**; needs `PlayerEvents` set, ~1-2 days |
| 10 | Sleeping - count only the sleeper's verse so beds work at all | **built** (`Sleep.cs`) - a majority vote across the server, rule covered by `./test.sh` |
| 11 | Plugin interop - stop Mark being visible and audible in every verse | **not built**; only wrong when Verse and HallPatton run together |
| 12 | Migrating a world played before the plugin existed | **built** (`Migration.cs`) - dry run exercised locally, not yet run on the live world |

There is no spike 8. It was the cheap version of 9 — leave the one shared event alone and just
send it to a single verse's peers — and 9 subsumes it, so it was dropped rather than renumbered.

**Priority is not the same as size.** Spike 10 is the one players would hit first and it is the
smallest: today, once two verses have anybody in them, nobody on the server can sleep. Spike 6
is the largest *wrong* thing, because a verse killing a boss rewrites every other verse's
world. Spike 7 now covers two breakages rather than one — dead altars and invulnerable dungeon
creatures — which makes it bigger than it looked. Spikes 2, 3 and 4 are built and need somebody
in the game; two accounts, two verses and one dungeon with an altar in it settles 2, 3, 6, 7
and 10 between them.

The dependency order where one exists is **set `GlobalKeys.PlayerEvents` → spike 6 → spike 9**,
because spike 6 silently kills raids without that key and spike 9 is pointless without it.
Spike 5's remaining half is still the only question that could change the architecture, and the
memory number already points at "buy a bigger box" rather than "rewrite as a process per
verse".

## Open questions

- **Tagging churn.** Every `ZDO.Set` bumps `DataRevision`, which marks the ZDO for resend.
  Tagging happens once per object, but it is worth watching that it does not cause a
  resend storm when a large base first comes into view.
- **Save size and ZDO count** grow with verses × explored area, in one world file. Chunked
  saves (`ZDOMan.SaveData.m_objectsByChunk`) help; spike 5 has to put a number on it.
- **An unplaced peer** (no platform id) is currently left in `Verses.None` and sees only
  untagged content. That is a visible failure rather than a silent leak, which is the right
  way round, but it should become an explicit refusal to connect.
- **Deleting a verse is irreversible.** Give it a two-step confirm with the id typed back,
  and tombstone the ZDOs rather than destroying them.
- **World rates stay global, by decision.** `Game.UpdateWorldRates(m_globalKeys,
  m_globalKeysValues)` (`ZoneSystem.cs:797`) derives difficulty and world-modifier rates from
  the key set into process-global state, so rates cannot differ per verse without unpicking
  that. They will not: combat difficulty, resource rates and the rest are server settings, and
  every verse getting the same ones is the intended behaviour. This is why spike 6 keeps
  vanilla's key set for operator keys and only moves progression into the registry. Per-verse
  *raids* are no longer an open question either — see spike 9.
- **A boss fought in two verses at once is two boss fights on one main thread.** Each is
  simulated by its own owning client, so the AI cost is theirs, but the server still relays
  both sets of damage RPCs and holds both sets of ZDOs. Nothing says that is a problem; it has
  simply never been measured, and spike 5 is the place for it.
