# The Challenge Arena: ten waves, a loaner kit, and a clip

Design notes for an arena minigame in the `Verse` plugin (`src/Verse`). Read
[verse-design.md](verse-design.md) first — nearly every constraint below is one of its
findings applied to a new feature, and the two documents disagree nowhere on purpose.

Everything marked **confirmed** was read out of this repo or out of the decompiled
**Valheim 1.0.16** dedicated-server assembly; everything marked **inferred** is reasoning
from vanilla behaviour that has not been checked against the assembly yet. Unlike
verse-design.md there are few line refs here, because most of these facts came from the
assembly's metadata rather than from `ilspycmd` output — the class and method names are
still what to grep for.

## What it is meant to do, and what it is not

A player types `!arena`, is teleported to a walled circular arena in an empty corner of
the map, is handed a standard set of gear, and fights ten waves of increasingly unpleasant
creatures. Clearing it or dying ends the run, the result is broadcast to the whole server,
and they are put back where they came from.

**It is clip bait.** The purpose is a twenty-second video a content creator can post, and
every design decision below resolves in favour of *legible on camera* over *fair*. There
is deliberately **no leaderboard** and **no scoring** — an earlier draft had both, and they
were cut because they forced a standardised-gear requirement the architecture cannot
enforce (see *What the server cannot see*). The loaner kit survives that cut for a
different reason: it makes the fight look like a fight instead of a Mistlands player
deleting greydwarves.

## Verdict: buildable, and one fact decides the whole shape

**Container inventories are ZDO fields. Player inventories are not.**

`s_items`, `s_addedDefaultItems` and `s_inUse` are all real fields in the server's own
`assembly_valheim.dll` (**confirmed**), which is how a chest's contents survive a restart
and sync between clients. A player's own inventory is in their character profile on their
machine, which verse-design.md already records as unfixable: *"Valheim keeps inventory and
skills in the player's own profile, so players carry gear and skills between verses and no
server-side code can wipe them."*

So a chest is the **only** inventory a server-only plugin can read and write, and it is
therefore the only way to give a player an item or to take one away. Every rule in this
design that works, works because of that, and every rule that does not work, fails because
the player's own pockets are out of reach.

### The four rules the loaner kit deletes rather than solves

The first draft of this feature asked for an arena where durability does not tick, items
are not consumed, and death costs nothing. All three live in client code or in
world-global rates (`DeathPenalty`, `DeathKeepEquip`, `DurabilityRate`,
`m_skillReductionRate` are all symbols in the assembly — **confirmed** as names, their
exact semantics **inferred**), and verse-design.md has already ruled on the class:
*"World rates stay global, by decision… rates cannot differ per verse without unpicking
that."* What cannot differ per verse certainly cannot differ per 40-metre circle.

A disposable kit makes three of them moot instead:

| Original rule | Why it is no longer needed |
| --- | --- |
| No durability loss | `SwordIron` has 200 durability. A ten-minute run does not dent it. Durability only ever mattered because it was the player's own gear |
| Items are not consumed | The kit holds a fixed number of meads. Spending them is a resource decision, which is better fighting and better footage than infinite chugging |
| Keep your items on death | Their own gear is in the chest. The kit is a loaner. Death drops a tombstone full of borrowed iron |
| No creature drops | Still wanted, still enforceable — see below — but now cosmetic rather than load-bearing |

**The one piece of damage that remains** is the 5% skill loss Valheim applies on death
(`LowerAllSkills`). It is client-side, it is real, and the only lever is the world-wide
death penalty. **Decision: eat it.** A clip-bait minigame is not worth re-rating the whole
server's death rules.

## The loadout chest

Two chests at the arena gate, both created by the server, both tagged to the player's
verse at creation (**never** left untagged — see *Interference*).

**The deposit chest is `piece_chest_private`.** The Personal Chest is in this server's own
prefab index (**confirmed**: `piece_chest_private`, listed in
`tools/server/BepInEx/config/HallPatton.knowledge.txt`). Vanilla gates it on the chest's
creator matching the opener's player id (**inferred** — decompile `Container` before
building on it), and the server can write that id: `Warp.PlayerIdOf` already reads
`ZDOVars.s_playerID` off the peer's character ZDO for exactly this kind of lookup. So the
server stamps the chest with that player's id and nobody else can open it — not even the
other eleven members of their own verse.

**The kit chest** is written by the server: build an `Inventory` from `ObjectDB`, `Save()`
it to a `ZPackage`, and write the result into the chest's `s_items`. `ObjectDB` is present
and populated on the dedicated server — `HallPatton`'s `Knowledge.cs` walks
`ObjectDB.instance.m_items` and `m_recipes` on the live box and gets 1,519 items
(**confirmed**, in HallPatton's own verified-so-far list). Whether a hand-serialised
inventory is accepted by a client opening that chest is **spike A** and is the first thing
to build, because nothing else here matters if it fails.

### The kit

Prefab names from this server's index, so none of these are guesses (**confirmed**).
Quality is the upgrade level, which the inventory format carries per item.

| Slot | Items | Why |
| --- | --- | --- |
| Weapon | `SwordIron` q3, `MaceIron` q3 | two damage types, so skeletons and blobs are both answerable |
| Shield | `ShieldBanded` q2 | 42 block — parry is readable on camera, which a buckler's dodge is not |
| Ranged | `BowHuntsman` q2, `ArrowIron` ×60 | for the fliers in wave 8 |
| Armour | `HelmetIron` q2, `ArmorIronChest` q2, `ArmorIronLegs` q2 | 14 armour each |
| **Food** | `Sausages` ×3, `TurnipStew` ×3, `CarrotSoup` ×3 | **mandatory** |
| Potions | `MeadHealthMedium` ×4, `MeadStaminaMedium` ×4 | |
| Cold | `MeadFrostResist` ×2 | only if the arena is sited in the north — see *Siting* |

**The food line is the one that would have broken the first play test.** An unfed Valheim
player has 25 health. Wave 1 would kill them. `Sausages` (+55 health), `TurnipStew` (+18
health, +55 stamina) and `CarrotSoup` (+15 health, +45 stamina) are the classic iron-age
trio and put them at roughly 113 health with real stamina, which is the number the wave
table below is tuned against. They have to actually eat it, so the arena says so in chat
during the countdown.

Iron tier is deliberate. Bronze dies too fast to film; silver and up trivialises
everything before wave 8.

### Two traps in handing an item out, both found by running it

Neither of these is visible in code review, and the first one produces a chest that opens
cleanly and is empty.

**1. `m_dropPrefab` is null on a prefab that has never been instantiated, and
`Inventory.Save` writes the item's identity from it.** `ItemDrop` fills that field in when an
item is spawned into the world, so a clone of a prefab's own `m_itemData` has no
`m_dropPrefab` at all. Saving fourteen of those produced a perfectly valid package
containing fourteen *nameless* items, every one of which `Inventory.Load` then silently
discarded. Everything around it still passed: the chest had the right prefab, the right
creator, the right verse tag and a non-empty `s_items` string. The only assertion that
noticed was reading the inventory back and counting it.

In the game this would have presented as a chest that opens and is empty, with nothing in
the log — which is the worst-shaped failure this feature could have had, since the natural
conclusion would have been that the write never happened.

**2. Durability is the same problem with a worse symptom.** It is also normally filled in at
spawn time, so a straight clone can arrive at zero, and a kit of gear that is *already
broken* is far harder to notice than a kit that is empty. `Loadout.Build` sets
`m_durability = GetMaxDurability()` and the self-test asserts nothing comes back at zero.

The lesson generalises past this feature: **an `ItemData` cloned off a prefab is not a
spawned item**, and anything handed to a player this way needs the fields `ItemDrop` would
have set.

### What the server cannot see, and the consequences

| | |
| --- | --- |
| **Equipped** items | **Visible.** `s_rightItem`, `s_leftItem`, `s_chestItem`, `s_legItem`, `s_helmetItem`, `s_utilityItem`, `s_shoulderItem`, with `*Quality` on the weapon, shield and cape (**confirmed** as fields). Live-synced, so a swap mid-run is catchable |
| **Backpack** contents | Invisible |
| **Food and potions** | Invisible |
| **Armour upgrade level** | Invisible — there is no `s_chestItemQuality` |
| **Active status effects** | Invisible |

So the arena can require that what a player is *wearing and holding* is the issued kit,
and eject them if that changes. It can never stop someone bringing Mistlands food and four
stacks of `MeadHealthMajor` in their pockets. **That is accepted.** It would have been
fatal to a leaderboard, which is part of why there is not one.

### The kit is a faucet, and that is the feature

Once an item is in a player's inventory it is client-side and gone. The server cannot take
the kit back, and it cannot gate the exit either, because the player's own gear is sitting
in their own chest and is reachable at any time.

So the arena hands out a free upgraded iron kit on every run, repeatably. Rather than
fight that: **"clear the arena, keep the gear" is the reason to run it twice**, and it is a
better one than the leaderboard it replaces. It costs nothing — a verse is a private world,
so a free kit inflates nobody else's economy.

## Siting: a command, a flat spot, and why not a portal

### Not a portal

Vanilla portals are tag-matched **globally with no verse awareness**, which this repo has
already paid for once: `PortalRepair.cs` exists because verse 1's hub had portals wired to
a test verse's spawn, and per `HANDOFF.md` 15 of 16 tags were repaired while *one was left
unresolved because the planner found three position clusters and correctly refused to
guess*. An arena portal with the same tag in all eighteen verses is that failure on
purpose. Portals are also pairs, and two-way, so a portal structurally cannot be the
one-way door this design needs.

**The arena is entered by `!arena` and nothing else.** `Warp.cs` already holds the
primitive: `ZRoutedRpc.InvokeRoutedRPC(peer, character, "RPC_TeleportTo", point,
Quaternion.identity, true)`. The trailing `true` is the distant-teleport flag, which makes
the client run its own fade-and-reload rather than being dropped into unloaded terrain —
correct here, since the arena is kilometres from anywhere. Arriving somewhere never
visited is a large one-time ZDO push, which is exactly the case `Firehose` exists for
(`docs/portal-sync.md`): 601 KB in 4.0 s against vanilla's 9.

### Finding the site instead of hard-coding it

`WorldGenerator`, `GetBiome`, `GetBiomeHeight`, `GetHeight` and `GetDeepNorthHeight` are
all present in the server assembly (**confirmed**), and height is pure math over the seed —
no scene, no heightmap, no client. **So the server can scan for a flat site itself**:
sample a grid of candidate points, keep the one whose height variance over the arena radius
is lowest and which is above sea level and in the wanted biome.

This has to be done rather than hard-coding coordinates, because the site must be valid for
*this world's seed*. A hard-coded point is a cliff face on the next world. Cache the result
in the registry so the scan runs once.

### Deep North: three reasons for, three against

| For | Against |
| --- | --- |
| **Nobody plays there.** The arena is a shared untagged structure (below), so it must not sit where anyone might build | **It is cold.** `SE_Frost` is in the assembly (**confirmed**); an iron kit in the north freezes, so the kit needs `MeadFrostResist` and the run has to fit inside its duration (~10 min in vanilla — **unconfirmed**, check it) |
| **It is outside `Sweep`'s reach.** `Sweep.cs` anchors on the legacy base and the temple and only acts within `SweepPiecesNear`; a site kilometres north is beyond both, which solves the "Sweep eats the arena" problem by geography rather than by a special case | **Snowstorms and darkness ruin footage.** Weather is derived client-side from the one synced world clock and cannot be chosen. A clip-bait feature whose biome is often an unreadable white-out is working against itself |
| It looks dramatic, and `deepNorthMinDistance` / `CreateDeepNorthGap` (**confirmed** symbols) mean it is genuinely remote | **It has its own monsters**, and they are not wave-appropriate: `Gammeltroll` 3000 HP, `Barka` 2200, `JotunWitch` 800, `Unbjorn` 1200 all live there |

**Recommendation: scan the Deep North first, and fall back to a flat Meadows or Plains site
far from origin if the clips come back unwatchable.** Make it a config key rather than a
decision — `ArenaBiome` and `ArenaMinDistance` — because this is a question only footage
can answer.

### Native spawns have to be deleted, and can be

Ambient wildlife is per-verse because `SpawnSystem.UpdateSpawning` requires
`m_nview.IsOwner()` and a local player (verse-design.md's audit table), which means a
player standing in the arena **generates the local biome's wildlife themselves**. In the
Deep North that is a Gammeltroll wandering into wave 1.

The server cannot stop a client spawning them, but it created every legitimate arena
creature and therefore knows every legitimate arena ZDOID. **So: any creature ZDO that
appears inside the arena cylinder and is not one of ours gets destroyed on arrival.** The
hook is `ZDOMan.RPC_ZDOData`, the same place `Authorship` and `Divergence` already work.
The identical rule deletes creature drops (`ItemDrop` ZDOs) and rejects player-built pieces
and `TerrainComp` changes inside the ring.

**One whitelist that must not be got wrong: a `TombStone` is never deleted.** It holds a
player's inventory, and on a public server the first time that rule misfires is the last
time anyone trusts the arena.

## The arena itself

**One shared, untagged structure.** Built once, from ordinary piece ZDOs, at the scanned
site. Static geometry that nobody mutates is the one case verse-design.md's shared world
handles *correctly*, and `Destruction.cs` already masks destruction per verse — so a troll
smashing the wall in verse 7 leaves it standing for everyone else. Building it per verse
would multiply the ZDO count by eighteen against the ~1.2 GB of measured headroom, for no
gain.

**`HideMask` needs a `Reveal` and does not have one.** It exposes `Hide`, `HiddenFrom` and
`HiddenFromAll` and nothing that un-masks (**confirmed** by reading the file). So wall
damage in a verse is permanent for that verse, and the second run in that verse happens in
a broken ring. The arena must clear its own ZDOs from the mask on run start, which means
adding `HideMask.Reveal(ZDO, int verse)` — a small, generally useful addition that nothing
else needs yet.

**Radius: 35–40 m.** Larger than "medium", because of the party cap below. Twelve players
and seventy creatures in a small ring is one melee scrum with nothing readable in it.

## Waves

Creature prefabs and health values are from this server's own index (**confirmed**), so
they are this build's real numbers rather than remembered ones. Tuned against a fed player
in the issued iron kit: a competent solo runner should wall somewhere around wave 7.

| # | Composition | HP | What it is for |
| --- | --- | --- | --- |
| 1 | `Greyling` ×6 | 120 | warmup; learn the ring |
| 2 | `Greydwarf` ×6 | 240 | crowd blocking |
| 3 | `Skeleton` ×5 + `Skeleton_Poison` ×1 | 300 | first debuff |
| 4 | `Greydwarf` ×4 + `Greydwarf_Shaman` ×2 + `Greydwarf_Elite` ×1 | 430 | target priority — kill the healers |
| 5 | `Draugr` ×5 + `Draugr_Elite` ×1 | 700 | staggered trades |
| 6 | `Troll` ×1 + `Greydwarf` ×8 | 920 | **the money shot** — a troll swinging a log into a crowd |
| 7 | `Troll` ×2 + `Greydwarf` ×6 | 1,440 | spacing under pressure |
| 8 | `Hatchling` ×3 + `Deathsquito` ×2 + `Goblin` ×4 | 1,020 | threat rather than HP; the only wave gear cannot trivialise |
| 9 | `Fenring` ×4 + `Ulv` ×4 + `Seeker` ×2 (★2) | 1,800 | fast packs, no breathing room |
| 10 | `GoblinBruteBros_nochest` + `Goblin` ×4 (★2) | 2,800 | capstone |

Two deliberate choices:

- **Wave 6 is the troll, not wave 7.** An earlier draft put the deathsquitos at 6 as a
  skill check. For a leaderboard that was right; for footage it ends runs abruptly and
  off-camera, so the troll moved up and the deathsquitos moved back.
- **Wave 10 uses Hildir's `_nochest` miniboss variants.** `GoblinBruteBros_nochest`
  (2,100), `Fenring_Cultist_Hildir_nochest` (1,850), `GoblinShaman_Hildir_nochest` (1,200)
  and `Skeleton_Hildir_nochest` (600) exist **specifically as arena minibosses with the
  reward chest decoupled** — which is this design's no-loot requirement already solved by
  the game. Rotating which one is wave 10 gives seasons for free.

Spawn each wave on the rim in two or three groups a couple of seconds apart, with ~8 s
between waves. That films better, it avoids a single large ZDO burst to every peer at once,
and it gives `ReleaseNearbyZDOS` — which reassigns ownership every two seconds — time to
hand each creature to a client before the player reaches it.

### The multiplier

More players means more enemies. **Verse size is 12** (`VersePlugin.MaxPartySize`; note
verse-design.md:11 still says a leader "leads a group of up to six", and its scaling
table's "36 (6 full parties)" assumes the same - both are now stale).

The count that matters is **players of that verse inside the arena**, never the server
population. Players in different verses cannot see each other, cannot damage each other's
creatures and do not appear in each other's player lists, so scaling on server population
would hand a solo runner a twelve-player wave because strangers in other verses happen to
be standing on the same coordinates. There is no hub verse yet, so in practice the
multiplier fires only for a real party — which is exactly who it should fire for, and
nothing about the arena changes when the hub arrives.

| Players | Adds × | Wave-10 capstone |
| --- | --- | --- |
| 1 | 1.0 | `GoblinBruteBros_nochest` |
| 2 | 1.7 | same |
| 3 | 2.3 | same |
| 4–5 | 2.8 | + `GoblinShaman_Hildir_nochest` |
| 6–8 | 3.4 | + `Fenring_Cultist_Hildir_nochest` |
| 9–12 | **4.0 (cap)** | all three |

**Sub-linear, because co-op power is super-linear** — a tank plus an archer is worth far
more than two of either. Scale the *adds* by count and the capstone by adding discrete
minibosses; multiplying a miniboss is nonsense. Scale count rather than stars below wave 9,
because stars make things spongy and crowds film better.

**This is the first feature in this project that could tip CPU.** The measured peak is
18.6% of one core at four players, and verse-design.md has consistently found memory binds
before the scheduler. Seventy creatures simulated across twelve clients, with every damage
RPC crossing the patched `ZRoutedRpc.RouteRPC` on the one Unity main thread, is a load
shape nothing here has produced. Cap the multiplier, and measure rather than extrapolating.

## Rules: what is enforced, what is detected, what is dropped

| Rule | Mechanism | Status |
| --- | --- | --- |
| No creature drops | - | **dropped - it cannot be done.** `CharacterDrop.OnDeath` gates on a private `m_dropsEnabled` field set only by `SetDropsEnabled()`, with no ZDO field anywhere in the decision, so a server has no lever. Deleting the item afterwards loses to autopickup, which is instant and local: the player is standing on the corpse. Confirmed in play - the player kept the drops while the log dutifully culled the ones they had not walked over |
| Floor kept clear of uncollected loot | the same deletion, reframed: it cannot stop a pickup, but it does stop the floor filling up | enforced, and worth having for footage |
| No native wildlife | delete creature ZDOs that are not ours, out to `ArenaClearRadius` rather than only inside the wall, plus a sweep at the start and end of every wave for the ones that wander in | enforced |
| No trees, bushes, rocks or ruins around the venue | `ArenaRing.Flatten` out to `ArenaClearRadius` (60 m against a 19.5 m ring), at every wave edge. Verse-tagged objects, graves and containers are never touched at any radius | enforced |
| No building or digging | reject piece and `TerrainComp` ZDOs in the cylinder | enforced |
| No summons or tames | a friendly creature ZDO appearing in the cylinder voids the run | enforced |
| Gear is the issued kit | snapshot the `VisEquipment` fields at the gate; a change voids | enforced for worn and held items only |
| Cannot leave the ring | sample the character ZDO position each tick | **detected, not prevented** — `Player.TeleportTo` is client-authoritative and verse-design.md is explicit that *"the server cannot refuse it"*. Walls stop honest players; leaving voids the run |
| `!warp` is blocked mid-run | `Warp.Handle` checks for an active run | enforced |
| No durability loss, no consumption, items kept on death | — | **dropped.** The loaner kit makes all three moot |
| No skill loss on death | — | **dropped.** Client-side; the only lever is the world-wide death penalty |
| Food and potions brought from home | — | **undetectable.** Accepted |

## Detecting kills, and a hook that will not work

Wave completion needs to know when a creature dies. The tempting answer is the hook
`Keys.cs` already owns — it patches `ZoneSystem.RPC_SetGlobalKey`, `RPC_RemoveGlobalKey`
and `SendGlobalKeys` on the server, and `Character.OnDeath` does route `SetGlobalKey` to
the server on death.

**It will not work.** That call only fires for creatures carrying
`m_defeatSetGlobalKey` — bosses and a handful of specials. A `Draugr` or a `Greydwarf`
dying sets no key at all, so that hook never sees 99% of arena kills.

The cheap answer is already in hand: **the arena created every creature, so it holds every
arena ZDOID.** Either poll that set — it is small and bounded — or use
`ZDOMan.RPC_DestroyZDO`, which `Destruction.cs` already patches. Wave clear is then "my set
is empty", with no new game API at all.

## Interference from the rest of the server

| Source | Effect on a run | Action |
| --- | --- | --- |
| **Untagged arena creatures** | verse-design.md: *"anything the server creates is untagged and therefore shared by every verse"* — two players running the arena in different verses would see each other's draugr at identical coordinates, and cross-verse damage is dropped or `Divergence.Claim`-forked into spike 7's invulnerable-statue bug | **Tag explicitly at creation.** Note `Authorship` deliberately has no inherit-from-owner helper — that was the bug that logged `265350 zdos considered, 265350 hidden` |
| **`Divergence` and chests** | verse-design.md's own known risk: *"Harmless for a tree being felled; needs checking for a chest being looted"*, and the mutation half is *"written but not yet play-tested"* | Tag both chests at creation so they never enter the copy-on-write path at all. A shared untagged chest is one every verse can loot |
| **`Sweep.cs`** | hunts untagged pieces, creatures and loose items near the legacy base and the temple | Solved by siting the arena far from both. If the site ever moves closer, whitelist explicitly |
| **`Sleep.cs`** | the night skip is a server-wide majority vote, so a stranger's bedtime can flip a run from night to day mid-wave | Accepted. It was fatal to a timed leaderboard; it is merely cosmetic without one |
| **Raids (spike 9, not built)** | one global event placed by distance, verse-blind — a Fuling army can land on the arena | Accepted for now; spike 9 fixes it. The remoteness of the site makes it unlikely, since events are placed at player positions |
| **`BossStones.cs`** | seeds stones at the temple on verse creation | No interaction — different place |
| **`ZDOMan.CreateNewZDO`** | its prefab parameter does not set the ZDO's prefab field | Call `zdo.SetPrefab(...)` explicitly, per `HANDOFF.md`. This has already bitten `BossStones` once |

## Commands

| Typed | Effect |
| --- | --- |
| `!arena` | teleports you to the gate; creates your deposit chests and your kit chests. Also puts you in the run if the kit is already on — typing it a second time after dressing is how most people join, so the subcommand is not a dead end to find |
| `!arena join` (`ready`) | puts you in the next run. Refused, naming the slot, while you are wearing anything of your own |
| `!arena start` (`go`) | begins it, with everybody who has joined. Anybody who has joined can call it, and nothing starts on a timer |
| `!arena leave` | before a run starts, drops you out again; mid-run it counts as falling |
| `!arena watch` (`spectate`, `back`) | takes you back to the venue after you have fallen, and only to the outside of it: the bottom of the steps, with the gallery above. It touches no run state, and the ring has no door, so "cannot re-enter" is the wall and the railing rather than a check. Spectators are kept Rested and, where the site freezes, given the kit mead's own frost resistance (`Potion_frostresist`, read off the item rather than named in the plugin) — the default site is the Deep North, where arriving from your bed with nothing is otherwise a death by weather |
| `!arena help` | the above, to you only |

A run in progress does not admit latecomers; they wait for the next one. The result is
broadcast with `GlobalChat`, which already crosses verses.

## Config

| Key | Default | Meaning |
| --- | --- | --- |
| `ArenaEnabled` | `false` | off until play-tested, like every other spike here |
| `ArenaCommandWord` | `!arena` | third command word alongside `!verse` and `!warp` |
| `ArenaBiome` | `DeepNorth` | which biome to scan for a site |
| `ArenaMinDistance` | *(biome default)* | how far from origin to start scanning |
| `ArenaRadius` | `19.5` | metres, and the circle the wall is built on. Was 26 for the first live runs; a quarter smaller keeps a wave in one frame. **Lives in the server's own config file once written, so changing this default does not change a running server — edit `com.matthew.verse.cfg` too** |
| `ArenaDeckPrefab` | `wood_floor` | the floor piece the deck under the gate chests is laid from; empty puts chests back on bare terrain |
| `ArenaLevelGround` | `true` | fill the hollows in the fighting floor, and pave what gets filled, by writing the zone's terrain deltas from the server — see `Ground.cs`. **Raise-only**: ground already above the fill line is left exactly as the seed made it. It levelled the whole venue flat once; that came back from the game as a disc of paving with a cliff round it |
| `ArenaBoardwalk` / `ArenaBoardPrefab` | `true` / `wood_floor` | the ring of boards on the ground around the outside of the wall. Cosmetic, and where a spectator lands |
| `ArenaGallery` | `true` | the walkway on top of the wall, its two courses of iron railing, and the ramp up to it |
| `ArenaRailPrefab` | `iron_wall_2x2` | vanilla's Cage Wall: iron bars, see-through and solid, which is the only reason a gallery over the floor is safe to offer |
| `ArenaUnbreakable` | `true` | write an unreachable health into every fixture and the gate chests. Vanilla has no indestructible flag; health is a field on the ZDO, so this is a number the server owns |
| `ArenaTrim` | `true` | dress the venue: banners and sconces down the inside of the wall, braziers along the gallery. The lights are real `Fireplace`s and fuel is a ZDO field, so the server refills them at boot and at every run start |
| `ArenaBannerPrefab` / `ArenaSconcePrefab` / `ArenaBrazierPrefab` | `piece_banner04` / `piece_walltorch` / `piece_brazierfloor01` | the dressing's pieces; any of them empty fits none of that kind. Which way a wall-mounted piece faces is measured off its own lop-sided box, not assumed |
| `ArenaWaves` | `10` | |
| `ArenaMultiplierCap` | `4.0` | |
| `ArenaKeepKit` | `true` | the faucet, deliberately |

## Status

Built, in `src/Verse`:

- **`ArenaRules.cs`** — the numbers, with no Unity in them: the wave table, the sub-linear
  multiplier and its cap, the capstone escalation, the kit, and the result line. Compiled
  into `tests/TextTests`, so `./test.sh` covers it — 28 cases, including every band edge of
  the multiplier, that a miniboss is never multiplied, and that the kit contains food.
- **`ArenaSite.cs`** — the flat-site scan. Coarse polar pass over `WorldGenerator.GetBiome`
  and `GetHeight` for the right biome above water, then 37 probes over the real footprint of
  the best 64 candidates, lowest height spread wins. The answer is written back into
  `ArenaSitePoint` so it runs once per world and an operator can pin a different one.
- **`Loadout.cs`** — the gate chests. Creates as many `piece_chest_private` as the kit and a
  full inventory need (read off the prefab's own `Container.m_width/m_height`), stamps each
  with the player's id so vanilla's own lock keeps everyone else out, tags them to the verse
  at creation, and serialises an `Inventory` into `s_items`.
- **`ArenaDeck.cs`** — a 2 m-tile wooden floor under each row of gate chests, laid level with
  the highest ground under *the chests* — along the row, half a chest to either side — and
  idempotent per gate. A chest is a building piece with `WearNTear`: one whose footprint hangs
  over uneven ground loses its support and eventually collapses, which on a deposit chest means
  a player's whole inventory. Shared and untagged like the ring, marked `verse.arena.deck` so
  the scenery sweep and the guard leave it alone, and deliberately *not* carrying the arena's
  own `verse.arena` marker, which `CleanupOrphans` deletes on sight. It reports the height of
  its *surface*, which is not where the tile is positioned: see `Footing.cs`.
- **`Ground.cs`** — terrain edits written from the server, with no client involved. Two
  operations: `Level`, which flattens a circle, and `Fill`, which is what the arena uses — it
  raises what is below a floor line and leaves everything at or above it exactly as the seed
  made it. A fill is continuous with the terrain by construction (at a hollow's edge the two
  are the same height) so it leaves no step anywhere, and the shape of the place survives;
  levelling the arena flat instead produced a paved disc with a walkable cliff round it. Both
  are absolute, and a fill clears anything it finds edited inside a wider radius, which is how
  a venue that was levelled by the old scheme gets its plateau taken away again. Every edit a
  hoe has ever made to a zone is one gzipped byte array (`TCData`) on one `_TerrainCompiler`
  ZDO at the zone's centre, holding a height delta per vertex of its 65x65 grid; the server owns
  the object table, so the server can write it, and the client applies it in
  `Heightmap.ApplyModifiers` exactly as it applies a player's digging. Two details decide
  whether it works: the format is matched byte for byte against `TerrainComp.Save`, because
  `Load` abandons an array of the wrong length with one warning in the client's log, and the
  deltas are measured against `HeightmapBuilder`'s own vertex heights — the biome at the zone's
  four *corners*, blended with a smoothstep — and not against `WorldGenerator.GetHeight`, which
  is a different number wherever a zone straddles two biomes. Vanilla clamps a vertex to ±8 m
  of generated ground, so this flattens slopes and not cliffs. `Restore` undoes an edit and
  removes the compiler, so a venue that moves does not leave a plateau behind it.
- **`Fixture.cs`** — the one way a permanent piece gets into the world: prefab field set,
  persistent, marked, `Authorship` suspended, and an unreachable amount of health written into
  `ZDOVars.s_health`. That last one is the answer to "stone is too easy to break": stone was
  already the toughest material this server has, so the fix was not a tougher prefab but a
  number the server owns. It also retires the failure the chest decks were built around — an
  unbreakable deposit chest cannot collapse with somebody's gear inside it.
- **`ArenaApron.cs`** — the ring of boards on the ground outside the wall, laid against the
  wall's own measured outer face rather than at a written-down radius. The only purely cosmetic
  thing the arena builds, and also where `!arena watch` puts somebody down.
- **`ArenaStand.cs`** — the gallery: a plank walkway on top of the wall, an iron cage railing
  along its inner edge, and a flight of steps up the outside. The railing is the piece that
  matters — a walkway over the floor with an open inner edge is a diving board, and a dead
  fighter dropping back into the run is the thing being prevented. The way up was a ladder
  until the game said otherwise: it went in as three stacked `wood_stepladder` and came back
  "not actually climbable", and the reason is that **Valheim has no climbing** — nothing about
  it in `Player` or `Character`, and the only `Ladder` component is a lift that teleports you
  to a target transform, which that piece does not carry. So a wood ladder is climbed purely by
  walking up its collider, which works only when it faces the right way, and which way that is
  lives in a Unity scene the server cannot read. Steps were the second try — the same boards
  laid level 0.4 m apart, inside the half-metre a player is usually said to step over — and
  those came back as *"I have to jump"*: a stack of separate plates is not a stair, because
  between the plates there is nothing to walk onto. So it is a **ramp**: the same boards tilted
  to the slope and overlapped along it into one continuous surface, about 20° and started on the
  high side of the site, which a character walks up the way it walks up a hill. A floor tile's
  own up axis is measured (`Footing`), so the rotation is arithmetic, and the self-test checks
  the slope angle rather than a step height. The railing is **two courses** of cage wall, the
  second offset half a piece around the ring so its bars fall between the first's rather than
  coplanar with them: 2.7 m of iron over the walkway, after one course came back as "you can
  barely jump over them".
- **`ArenaTrim.cs`** — the venue's dressing: banners and sconces down the inside of the wall,
  braziers along the gallery. Cosmetic except for one mechanism — a torch or brazier is a
  `Fireplace`, and a `Fireplace` burns `ZDOVars.s_fuel` down against `s_lastTime`, so an arena
  lit when it was built is an arena in the dark by the evening. The fuel is a field on a ZDO the
  server owns (the same fact the gate chests rest on), so it is simply refilled at boot and at
  every run start, timestamp included — a fire that has been full since this morning otherwise
  burns its whole load catching up the moment somebody loads the zone. Which way a wall-mounted
  piece faces is read off its own box: a banner hangs on one side of its mounting point, so the
  lop-sidedness says which side is the front. That is the third time a prefab's facing has
  mattered here, after a ladder that could not be climbed and a ramp tilted the wrong way.
- **`Footing.cs`** — how far a prefab reaches below and above its own origin, measured off its
  colliders (meshes as a fallback) and cached. A ZDO's position is the prefab's origin, and
  where that sits inside the object is per-prefab: `wood_floor`'s is 0.10 m under its walking
  surface, `piece_chest`'s is its base. Levelling a deck by its origin and then adding a
  hand-tuned 0.3 m of "clearance" to the chests on top of it is what left the gate chests
  floating about 0.2 m over their own floor, with the floor itself in the air — visible only
  from inside the game. Both are now arithmetic: the tile is positioned so its surface lands on
  the ground, and the chest so its base lands on the surface.
- **`Arena.cs`** — the run: gate, ready-up, countdown, waves, server-written creatures tagged
  to the verse, kill detection, bounds and kit checks, the broadcast, and `CleanupOrphans`.
- **`ArenaGuard.cs`** — the intruder rule, as a second postfix on the `CreateNewZDO` target
  `Authorship` already patches.
- **`ArenaSelfTest.cs`** - 74 server-side asserts, in the style of `SelfTest.cs`. Run on a real
  dedicated server; it failed on its first run and found the `m_dropPrefab` bug. It now also
  lays a real deck on real ground 700 m from the site, stands a chest on it and measures the
  gap between the two, because the floating chests were the one arena bug that was invisible
  from the server side until somebody looked at them.
- `HideMask.Reveal`, `Authorship.Suspended`/`Incoming`, three arena counters in `Metrics`, the
  `!arena` command word as a third branch in `Overhear`, and nine `Arena` config keys.
  `MaxPartySize` now defaults to **12**.

`ArenaEnabled` defaults to **false**. The whole thing is off until spikes A and B have been
through a client.

Both plugins compile clean against the client assemblies *and* against the dedicated server's
own smaller `assembly_valheim.dll` (632 types against the client's 2,500), which is the build
that matters and the one the README's `-p:ValheimDir=` recipe produces.

### Six things the implementation settled that the design above did not

Each of these is a decision the code had to make, and three of them are corrections.

1. **The guard cannot classify at creation time, so it defers.** The `CreateNewZDO` postfix
   fires *before* `RPC_ZDOData` deserialises anything into the ZDO, so at that moment the
   prefab field is 0 and the position is the origin — classifying there would read "unknown"
   for everything. Candidates are queued and sorted out on the next tick, when they are fully
   formed. Destroying an object part-way through the deserialisation loop that is still
   reading it would have been asking for trouble anyway.
2. **Arena creatures have to be persistent, and that is why `CleanupOrphans` exists.**
   `ZDOMan.ReleaseNearbyZDOS` only ever reassigns a *persistent* ZDO, and ownership is what
   makes a client run a creature's AI at all — the server holds no GameObjects out there. A
   non-persistent creature would stand inert for ever. The price is that a crash or a deploy
   mid-wave leaves creatures in the world, so they carry a `verse.arena` field and are swept
   on the next boot.
3. **The return teleport goes to the gate, not to spawn.** The original sketch said "teleport
   you back to spawn", which would have left every player's own gear in a chest several
   kilometres away in the Deep North with no way back but another `!arena`. They land at their
   own gate instead, and are told `!warp spawn` goes home.
4. **Native wildlife and summons are culled, not grounds for voiding a run.** The design said
   a friendly creature in the ring ends the run. Deleting it is both gentler and simpler, and
   it falls out of the same rule that removes the Gammeltroll — one mechanism instead of two.
5. **Kit enforcement happens at the gate first.** `!arena join` refuses, naming the slot, if
   anything worn or held is not from the kit. It is also what lets `!arena` itself join you:
   the check cannot pass until your own gear is off, so arriving at the gate can never enrol
   somebody who has not yet dealt with their own things. Being thrown out of wave four over a forgotten
   helmet is a worse experience than being told at the door; the check still runs during the
   fight, for anything swapped in mid-run.
6. **One chest is not enough for either job.** A full player inventory is 32 slots and the kit
   is 14 stacks, and no single vanilla container is reliably big enough for either, so
   `Loadout` places as many as the prefab's real grid size requires (capped at four of each).

## Spikes

Run against a local dedicated server with a real client on 2026-10-02. The headline result:
**a solo player in the loaner kit fell on wave 7 of 10 in 5:50**, which is where the wave
table above says a competent solo runner should wall. The balance has not been touched since.

| # | Question | State |
| --- | --- | --- |
| **A** | Can the server write an `Inventory` into a chest and have a client open it? | **answered, in game.** The chest opens, all 13 stacks are in it, and the player fought seven waves with the kit. It took three bugs to get there - see *Two traps* and the byte-array note below |
| **B** | Does a creature created as a hand-written ZDO work? | **answered, in game.** Greylings, skeletons, draugr, greydwarves and two trolls, all written straight into the object table as bare ZDOs, all simulated and killed by a vanilla client. The ~2 s inert window while `ReleaseNearbyZDOS` hands ownership over was not noticeable in play |
| **C** | Is `piece_chest_private` gated on the creator id? | **answered by decompiling `Container`.** `CheckAccess` is `m_privacy switch { Private => m_piece.GetCreator() == playerID, ... }`, and the creator is the `s_creator` field the server writes - so the lock does work. The default is the public `piece_chest` only because the private chest's 6-slot grid cannot hold a 32-slot deposit, not because the lock fails |
| **D** | Does the flat-site scan find usable ground? | **answered.** Meadows at 0, 40, 2300 - 3.2 m of height across a 26 m ring, 2.3 km out, from 287 in-biome candidates of 13,072 sampled in **12 ms**. Two earlier sites are a lesson in their own right: see *Siting* |
| **E** | `HideMask.Reveal` | **answered.** It restored 120 masked wall pieces at a run start, which is also how the collapse problem below was found. Whether it restores a wall a *troll* breaks is still unconfirmed - nothing had reached wave 6 until the last run |
| **F** | Load: twelve players, wave 10, x4 multiplier | **not built.** More urgent than it was: at one player the guard was culling 100-230 objects per ten seconds |
| **G** | Is it good footage? | **still open**, and still the only question none of this can answer |

### Six bugs the live runs found, and what each one teaches

Worth recording in full, because five of the six are the same shape: **the write succeeds and
the read silently discards.** Nothing throws, nothing logs, and the feature is simply absent.

1. **`s_items` is a `byte[]`, not a base64 string.** `Container.Save` writes
   `ZPackage.GetArray()` and `Load` reads `GetByteArray` and returns early on null. Every chest
   was genuinely empty for two full test sessions. **And the self-test passed 13 of 13 the
   whole time**, because it read the field back with `GetString` - the same wrong way it wrote
   it. A round trip through your own writer proves nothing about a field's contract; read it
   the way the game reads it.
2. **`m_dropPrefab` is null on a prefab that was never instantiated**, and `Inventory.Save`
   writes each item's identity from it. A clone of a prefab's `m_itemData` therefore serialises
   as a nameless item that `Inventory.Load` drops. An `ItemData` cloned off a prefab is not a
   spawned item.
3. **Durability is the same trap with a worse symptom** - also filled in at spawn time, so a
   clone can arrive at zero. A kit of already-broken gear is harder to notice than an empty one.
4. **The two chest sets collided.** The kit call found the deposit chests and wrote the kit
   into the chests the player was meant to empty their pockets into, because 3 m apart is
   inside a 9.6 m search radius. Match on a marker field, not on position.
5. **A ZDO the server creates is owned by the server**, which holds no GameObjects out where
   players are - so a container RPC addressed to its owner arrives nowhere and the chest never
   opens. Ownership is handed to the player's peer at creation and re-asserted on every entry.
6. **The guard only caught objects *created* inside the ring.** A deathsquito that spawned 60 m
   away and flew in was never a candidate, and killed the player during wave one. Hence
   `ArenaGuard.Sweep`, at the start and end of every wave.

### The one that invalidated everything: `DestroyZDO` needs ownership

```csharp
public void DestroyZDO(ZDO zdo)
{
    if (zdo.IsOwner())          // the server almost never is
        m_destroySendList.Add(zdo.m_uid);
}
```

`ReleaseNearbyZDOS` hands every persistent ZDO in a player.s active area to that player every
two seconds, so essentially nothing near the arena is ever the server.s. **Every destroy this
feature made was a silent no-op** - the loot culls, the intruder sweep, the scenery clearing,
`CleanupOrphans`, the end-of-run creature cleanup and the ring teardown. Six of seven
mechanisms were inert while the log read as though they were working.

The fix is `zdo.SetOwner(ZDOMan.GetSessionID())` before the destroy - the same primitive
`Isolation.cs:220` already uses for its reclaim path. The symptoms it explains, in case
anything else in this plugin ever deletes a world object: a sweep that keeps finding the same
objects (`33 of 33 already destroyed once`), an unchanging `cleared 56` every second, 100-230
"culls" per ten seconds at a single player, `tagged` climbing as clients re-sent objects the
server thought it had deleted, leftover creatures surviving every boot, and five rings standing
where one was wanted.

### Stamina: Rested is grantable, and has to be granted

Without it the arena is a stamina-starved slog, and the player cannot fix it - Rested comes
from comfort, comfort comes from a fire and a roof, and a stone ring in a field has neither.

It is reachable for the same structural reason teleports are: `SEMan` registers
`RPC_AddStatusEffect` on the character.s own `ZNetView` and acts on it when the receiver owns
that character, which the player.s client does. verse-design.md noted that path when
explaining why cross-verse buffs get dropped at the relay; here it is the mechanism rather
than the hazard.

And it is worth granting with no fire anywhere: `SE_Rested.m_baseTTL` is a flat 300 s and
`UpdateTTL` only ever extends, so at zero comfort it still runs 240 s. Granted when the
countdown ends and topped up every 60 s so it cannot lapse at wave eight. The self-test
asserts the effect name resolves in `ObjectDB`, because a wrong name fails completely silently.

### The ring took three attempts, and the failures were instructive

| Attempt | Geometry | Result |
| --- | --- | --- |
| 1 | per-segment ground height, sunk 0.3 m | **34 of 120 collapsed** - on a slope, pieces neither met their neighbours nor reached the ground, and stone needs support |
| 2 | one uniform base from the lowest ground, 4-6 rows to clear the highest | **120 of 164 collapsed** - 41 segments x the top three rows, because stone will not self-support an 8 m stack |
| 3 | per-segment ground height, sunk 1.5 m, two rows | current. Short enough for stone to hold, deep enough that nothing floats, ~2.5 m above grade against a ~1.2 m jump |

**The material is chosen at runtime, not written down here.** Stone was the first ring and
"breakable by a troll" was defended as the point; in play it was just a broken venue, so the ring
is now built from the toughest material the server has. Which that is is a `WearNTear.m_health`
comparison across `grausten_wall_4x2`, `blackmarble_2x2` and `stone_wall_4x2` made when the plugin
first has a prefab table, and the choice is in the log with the numbers it was made on - a
constant in the plugin claiming grausten is tougher would be a guess that could silently go stale
between builds of the game. `ArenaWallPrefab` pins it; the geometry comes from a small table of
known piece sizes, because the circle is laid out from the segment width.

**The material is part of "is the ring already standing?"**, alongside the radius and the piece
count. Grausten walls are the same 4x2 as stone, so without that test the swap would have changed
the config and nothing else, for ever: same radius, same count, ring left alone. Changing the
material tears the old ring down on the next boot and rebuilds it, which is one `tore down N wall
piece(s)` line and no lost state - the ring holds nothing.

**The ring is exempt from `Divergence`** - wall damage mutates a shared ZDO, and a fixture
should not fork per verse because a troll scuffed it. Ring damage is shared, and `Repair`
restores it per run.

**A correction worth keeping, because the mistake is instructive.** That exemption was
originally justified by a climbing teardown count - 120 built, 274 standing, then 520, 684,
766 - read as copy-on-write duplicating the ring. It was not. Every "tore down N" line was a
no-op for the same reason every cull was: `DestroyZDO` does nothing unless the server owns the
ZDO. Each boot simply built a new ring and left the previous ones standing, and the arithmetic
fits exactly at every step (684 + 82 = 766). **A number produced by broken code is not
evidence**, and reasoning from one produced a confident diagnosis of the wrong thing.

### Siting: two wrong answers before a right one

Each failure was the same mistake - judging the venue by how it looks rather than by what a
player does there, which is **stand still, unarmed, at a chest.**

- **Deep North** (the first choice): froze the player to death at the gate before they could
  drink the frost mead that was inside the chest. Cold resistance is an item in an inventory
  the server cannot reach.
- **Plains**: warm and flat, but deathsquitos and fulings roam it, and one killed the player
  during wave 1.
- **Meadows**: nothing in it will kill a naked player standing at a chest. The drama is
  supposed to come from the waves, not the postcode.

**Operational warning:** moving the site strands deposits. Changing `ArenaBiome` or
`ArenaRadius` while players have gear in a gate chest leaves it at the old gate with no way
back to it. Drain the chests before moving the arena.

### Scenery has to be cleared repeatedly, and wider than the ring

`Flatten` at boot reported `cleared 0`, which looked like luck and was a bug: the arena is
kilometres from anywhere, so its zone has never been generated and its bushes and rocks do not
exist as objects yet. At run start it caught 56 - and the rest streamed in afterwards, to be
smashed by the wave, which is where the 100-230 culls per ten seconds were coming from (`Wood`,
`Stone`, `Dandelion`, `Pukeberries` - scenery drops, not creature drops). The clear runs
alongside the sweep, at the start and end of every wave.

It also has to reach past the wall. Clearing to the stray tolerance (35.5 m) left the arena
standing in a wood: trees just outside the ring are in every shot, and a wave that smashes one
drops the trunk on the floor. `ArenaClearRadius` is the venue's radius rather than the fight's,
60 m by default against a 19.5 m ring and 29.5 m gates, and `ArenaGuard.Sweep` culls wildlife to
the same circle. Loose items are the one thing still culled only off the ring's own floor -
`Arena.StraySlack`'s comment records what happened the last time a cull radius reached the gate
chests, which is that it ate gear players had set down beside them.

### Both passes moved off the 1 Hz timer, because players felt them

The clear and the sweep are each a pass over `m_objectsByID` - tens of thousands of entries -
and running the pair of them once a second put two full table scans per second on the server's
main thread for the whole of a run. Players reported the arena as laggy, and that is the only
thing in the feature sized to cause it.

They now run at the two edges of a wave instead: in `NextWave` before the creatures are written,
and in `Fight` the moment the last of them is down, plus once more in `Finish`. Both edges are
moments when nobody is swinging at anything, and a wave's own creatures carry `verse.arena` so
neither pass was ever a threat to them. What the change gives up is latency on an intruder that
arrives *mid*-wave - a deathsquito that flies in now lives until the wave is cleared rather than
for up to a second. If that turns out to matter more than the frame time, the fix is not to go
back to 1 Hz but to ask `ZDOMan` for the handful of sectors the ring covers instead of walking
the whole table.

### The Mountain block was five wolf waves

A player died on wave 14 and reported the middle of the run as "a shit ton of wolves, back to
back to back". The table agreed: waves 11-15 were 22, 12, 12, 14 and 6 wolves - **66 wolves
across five waves**, with Ulvs and Fenrings as the only other thing in any of them, and nothing
new introduced after wave 11.

The constraint that shaped the original block is still true: **one faction per wave**, or the wave
fights itself, and the Mountain's roster is small. What it is not is only three creatures. The
block is now

| # | Was | Is | Shape |
| --- | --- | --- | --- |
| 11 | `Wolf` ×22 + `Ulv` ×10 | `Wolf` ×10 + `Ulv` ×16 + `Wolf` ×4 (★1) | a pack with leaders |
| 12 | `Fenring` ×6 + `Wolf` ×12 | `Fenring` ×5 + `Fenring_Cultist` ×4 + `Wolf` ×6 | fire at range |
| 13 | `Fenring` ×6 + `Wolf` ×12 + `Ulv` ×8 | `Hatchling` ×3 + `Fenring` ×4 + `Ulv` ×12 + `Wolf` ×6 (★1) | drakes overhead |
| 14 | `Fenring` ×8 + `Wolf` ×14 + `Ulv` ×10 | `Fenring` ×6 + `Fenring_Cultist` ×4 + `Ulv` ×12 + `Wolf` ×5 (★1) | the warband |
| 15 | boss + `Fenring` ×6 + `Wolf` ×6 | boss + `Fenring` ×4 + `Fenring_Cultist` ×3 + `Wolf` ×6 | his own cultists with him |

37 wolves instead of 66, fifteen of them starred, and **every wave's total health is within about
3% of the wave it replaces** (2,240 / 2,780 / 3,060 / 4,000 / 4,130 against 2,260 / 2,760 /
3,160 / 4,020 / 4,130) - the complaint was that the block was dull, not that it was hard. Stars do
the work that body count used to: a 1-star wolf is two wolves' health in one body, which is how
the count came down without the wave getting easier. The boss wave deliberately has no starred
adds, because a star raises damage as well as health and the boss is already the hard part.

Two notes on the new creatures:

- **Cultists and drakes are `MountainMonsters`**, which is why they are available at all. They
  share the peaks and the frost caves with wolves in the live world without fighting them, and
  `CheckFactions` re-confirms it from `Character.m_faction` on every boot. `CheckFactions` also now
  fails on a creature that is not on the server at all - it used to leave a `?` in a log line and
  spawn a quietly smaller wave.
- **A flyer is the one thing that can stall a wave.** A wave ends when its creatures' ZDOs are
  gone, and nothing retrieves a drake that drifts out of reach over the wall. Three per wave, in
  one wave; wave 18's deathsquitos have had the same exposure since the table was written without
  doing it. If it ever happens the fix is a leash in `Arena.Fight` - a spawned creature outside the
  ring for long enough counts as dead - rather than giving up the only ranged threat the Mountain
  has.

`./test.sh` pins the shape rather than the exact numbers: at least three kinds of creature in every
wave of the block, wolves at most half of any wave's bodies, no more than 40 wolves across the
block, and at least one starred add in it. The Swamp block (6-10) has the same back-to-back shape -
Draugr in four waves running - and has not been touched.

### The ring is rebuilt at the start of every run

`Repair` was never enough on its own. It un-hides the pieces a verse has broken, which fixes the
case it was written for - a troll put a wall through, that verse alone sees the hole, the mask comes
off at the next run start. A piece *destroyed* in the shared world is not masked and does not come
back, and nothing was watching for it: `Ensure` runs once per boot and only counts pieces when it is
already rebuilding for some other reason. So the wall lost a segment here and there and the venue
quietly degraded between restarts.

`ArenaRing.Rebuild` tears the whole circle down and raises it again, and `Arena.Begin` calls it at
the **start of the countdown** rather than at the teleport - the twelve seconds are better spent
streaming a couple of hundred new pieces out to the clients than having the first wave do it. The
build loop and the teardown were pulled out of `Ensure` into `Raise` and `Standing` so both paths
raise the same circle.

Two deliberate differences from `Ensure`:

- **No occupancy check.** `Ensure` refuses to build on ground a verse is using, which is right when
  it is *choosing* where to put a ring. `Rebuild` runs with the old ring already down, so a refusal
  there would leave the arena with no wall at all. Where the ring goes was settled at boot.
- **Skipped while another verse is mid-run.** The ring is one shared structure. Taking the walls out
  from around somebody else's live wave to freshen them for a new run is not a trade worth making,
  so `Arena.Busy` checks for another verse in Countdown, Fighting or Between and logs that it left
  the ring alone.

The masks go with the pieces: `HideMask.Forget` drops a destroyed piece's parsed mask, because the
pieces a rebuild tears down are exactly the ones a troll had masked, and otherwise every run would
leave entries behind for ZDOIDs that no longer exist.

### Open: boot-time destroys do not persist, and I do not know why

**The symptom.** Ring pieces left at abandoned sites survive every restart. Each boot reports
`tore down 328 wall piece(s) built at 5330.7 m` - the same count at the same average distance,
after a clean SIGINT shutdown with the world demonstrably saving (`save number 46`, chunk files
rewritten). The 5,330 m average is itself the clue that unpicked three wrong theories: a ring
of radius 26 cannot average 5 km from its own centre, so those pieces are the Deep North
(9.3 km) and Plains (5 km) rings from when the site moved during testing, not duplicates of the
current one.

**What is not wrong**, each checked by reading the game or this plugin rather than inferred, so
nobody repeats them:

| Candidate | Why it is not that |
| --- | --- |
| `DestroyZDO` needs ownership | real, and fixed - the in-session culls work now |
| Copy-on-write forking the ring | the counts were failed deletes, not forks; both fork paths are exempt anyway |
| The world not saving | it saves: chunk files are rewritten, save number increments |
| `Destruction.cs` masking instead of destroying | its prefix bails for the server's own destroys (`verse == Verses.None` → `return true`) |
| `SendDestroyed` needing a peer | it broadcasts to target 0 unconditionally and the server handles its own routed RPCs |

**Where I would look next:** whether `ZRoutedRpc.RouteRPC` - which `Isolation` patches - delivers
the server's own target-0 `DestroyZDO` broadcast back to the server itself when no peer is
connected, since every failing destroy happens at boot with nobody on the server and every
working one happens with a player present. That asymmetry is the strongest remaining signal and
it has not been tested.

**Why it is not being chased further now.** It is bounded (~250 ZDOs at coordinates nobody
visits, against 1.2 GB of headroom), self-limiting (every boot tears down what it finds), and it
only exists because the site was moved three times in one afternoon - a production server picks
a site once. `!arena purge`, run in-session with a player connected, is the operator remedy and
judges each piece on its own distance rather than on the useless average `Ensure` uses.

**The honest note for whoever picks this up:** this number got three confident wrong
explanations before it got an admission of ignorance. Each was built on arithmetic from a
system that was itself broken. Measure first, and distrust a number produced by code you have
not yet verified.

### What to do next, in order

1. **Turn `ArenaEnabled` on locally** (`./server.sh`) and read the arena self-test and the
   site scan out of the log. That is spikes D and E, and the server-side halves of A and B,
   with nobody in the game.
2. **Join and type `!arena`.** Spikes A, B and C all resolve in the first two minutes: does
   the chest open, does the kit come out, does the deposit chest refuse a second player, does
   a greyling attack.
3. **Then the waves**, solo, for G. Nothing about the balance is worth touching before there
   is footage to look at.

Spike F wants a full party and should wait until the rest is known good.
