# What a Valheim server-side plugin can actually do

Field notes from building [Mark Hall-Patton](../README.md) — a plugin that puts a talking
NPC into a world where **no client has anything installed**.

Everything here was read out of the decompiled game assembly, not out of a wiki. Line
numbers are from `ilspycmd` output for **Valheim 1.0.16** and will drift; the class and
method names are what to grep for.

```sh
nix develop
ilspycmd -p -o /tmp/dc "$VALHEIM_MANAGED/assembly_valheim.dll"      # ~40s, 2.5k files
ilspycmd -p -o /tmp/sp "$VALHEIM_MANAGED/Splatform.dll"             # identity types
ilspycmd -p -o /tmp/ss "$VALHEIM_MANAGED/Splatform.Steam.dll"       # platform behaviour
```

Prefab **names** are not in the assembly — they are asset names. They are recoverable as
strings from the soft-reference manifest, and the display names from `resources.assets`:

```sh
cd "$VALHEIM_DIR/valheim_Data"
strings -n 5 StreamingAssets/SoftRef/manifest_extended | grep -oE "[A-Za-z0-9_]+\.prefab" | sort -u
strings -n 4 resources.assets | grep -oE '"customization_beard[0-9]+","[A-Za-z ]+"' | sort -u
```

---

## 1. The one fact that decides your whole design

**A dedicated server does not instantiate GameObjects where the players are.**

`ZNetScene.CreateObjectsSorted` (ZNetScene.cs:197) only creates objects near
`ZNet.instance.GetReferencePosition()`. Every caller of `SetReferencePosition` is a
player — `Player.SetLocalPlayer`, `Player.LateUpdate`, `Game.FindSpawnPoint`, `Valkyrie`,
`Tracker` — so on a headless server it stays at `Vector3.zero` forever.

`ZoneSystem.Update` (ZoneSystem.cs:1181) *does* generate **ghost zones** around every peer,
but that is terrain and vegetation generation, not `ZNetScene` object creation:

```csharp
bool flag = CreateLocalZones(ZNet.instance.GetReferencePosition());
if (ZNet.instance.IsServer() && !flag) {
    CreateGhostZones(ZNet.instance.GetReferencePosition());
    foreach (ZNetPeer peer in ZNet.instance.GetPeers()) CreateGhostZones(peer.GetRefPos());
}
```

Consequences, and they are not small:

| | On a dedicated server |
| --- | --- |
| Creature AI, physics, damage | Run on the **nearest client**, which owns the ZDO |
| `MonoBehaviour` on a spawned prefab | Does not exist out where players are — you cannot hang behaviour on it |
| `ZNetScene.instance.FindInstance(zdo)` | `null` for anything away from the origin |
| Routed RPCs targeting a ZDO you own | Arrive, find no instance, and are silently dropped (`ZRoutedRpc.HandleRoutedRPC`) |
| Objects near world origin | **Are** instantiated, and the server owns them — a real trap, see §6 |

So a server-side plugin works on **ZDOs and RPCs**, not on GameObjects. Anything you want
players to see has to be expressible as ZDO fields that vanilla client code already reads.

A listen server (someone hosting from their own game) has both a local player *and* the
server role, so both paths exist there. Write code that survives either: gate on
`ZNet.instance.IsServer()`, and never assume `Chat.instance`, `Player.m_localPlayer`,
`ZInput` or a camera exists.

---

## 2. Making a networked object with no prefab instance

`ZNetView.Awake` (ZNetView.cs:~50) is the only place that documents what a ZDO needs.
Replicate it by hand:

```csharp
int hash = "Player".GetStableHashCode();
GameObject prefab = ZNetScene.instance.GetPrefab(hash);       // must exist, for clients' sake
ZDO zdo = ZDOMan.instance.CreateNewZDO(position, hash);
zdo.SetPrefab(hash);                                          // REQUIRED: ZDO.IsValid() is m_prefab != -1
var view = prefab.GetComponent<ZNetView>();
zdo.Type       = view.m_type;                                 // Default / Prioritized / Solid / Terrain
zdo.Distant    = view.m_distant;
zdo.Persistent = false;                                       // false = never written to the world save
zdo.SetRotation(rotation);
```

- `ZDOMan.CreateNewZDO(Vector3, int)` is public; the private overload it calls does **not**
  set the prefab, so `SetPrefab` is on you. Without it `IsValid()` is false and clients
  will not build it.
- `ZDOPool.Create` → `ZDO.Initialize` already registers the sector index
  (`ZDOMan.AddToSector`), so it will be sent to peers in range immediately. Moving it later
  with `ZDO.SetPosition` re-sectors it via `ZDO.SetSector` → `ZDOSectorInvalidated`.
- Copying `m_type` and `m_distant` off the prefab's own `ZNetView` beats guessing: `Type`
  decides sync priority in `ZDOMan.CreateSyncList`.

### Lifetime

`ZDOMan.RemoveOrphanNonPersistentZDOS` destroys any non-persistent ZDO whose owner is not
connected — **and `ZDOMan.IsPeerConnected` returns true for the server's own session ID**.
So a non-persistent server-owned ZDO lives exactly as long as the server process. That is
usually the lifetime you want for a summoned thing: no save-file residue, no orphan.

`ZDOMan.DestroyZDO(zdo)` only queues the destroy **if you own it** — `SetOwner` first.
It broadcasts `DestroyZDO` to everybody including the server itself (target `0L`), which
is what removes it locally too.

### Ownership

`ZDO.GetOwner()`, `SetOwner(long)`, `IsOwner()`, `HasOwner()`; the server's ID is
`ZDOMan.GetSessionID()`, which is also `ZRoutedRpc`'s own ID (`ZNet.cs:385` does
`m_routedRpc.SetUID(ZDOMan.GetSessionID())`).

Two reasons to re-assert ownership on a tick: only the owner may write fields, and
`Character.CustomFixedUpdate` does `SetVisible(zdo.HasOwner())` — an unowned character goes
**invisible** on every client.

All `ZDO.Set(...)` overloads early-out when the value is unchanged (`ZDOExtraData.Set`
returns false → no `IncreaseDataRevision`), so re-asserting a whole block of fields on a
timer is nearly free. `SetRotation` compares exactly, so jittery input still churns
revisions — threshold it yourself.

---

## 3. Rendering a humanoid NPC from ZDO fields only

The `Player` prefab is the only humanoid a vanilla client can already build that has a
configurable face, hair, beard and clothes **and** takes its displayed name from the ZDO.
`Player` *is* in `ZNetScene.m_prefabs` (it must be, or remote players could never be
created). Its component list, confirmed in-game with a prefab dump:

```
Player: Transform, CapsuleCollider, Rigidbody, PlayerController, Player, ZNetView,
        ZSyncTransform, ZSyncAnimation, Talker, VisEquipment, Skills, FootStep
  Visual: Transform, Animator, CharacterAnimEvent, LODGroup, AnimationEffect
```

Every field below is one a client already reads off *other* players, which is why writing
them server-side needs no client code:

| ZDO field | Read by | Effect |
| --- | --- | --- |
| position, rotation | `ZSyncTransform.ClientSync` | Body; **interpolated** (`SyncPosition` lerps), so 10 Hz updates look smooth |
| `438569 + Animator.StringToHash(name)` | `ZSyncAnimation.SyncParameters` | Animator parameters — see below |
| `ZDOVars.s_playerName` (string) | `Player.GetHoverName` | Name over the head, via `EnemyHud` (players are always shown, not hover-only) |
| `s_modelIndex`, `s_skinColor`, `s_hairColor` | `VisEquipment` non-owner path | Body and colours |
| `s_hairItem`, `s_beardItem`, `s_helmetItem`, `s_chestItem`, `s_legItem`, `s_shoulderItem` (+`Variant`/`Quality`), `s_rightItem`, `s_leftItem` | same | Clothes, as prefab-name stable hashes; `0` = empty slot |
| `s_health`, `s_maxHealth` | `Character.GetHealth` | Set both: nothing server-side will ever write them |
| `s_wakeup` (bool) | `Player.SetupAwake` | **Defaults to `true` when absent** — every client plays a second of getting up off the floor unless you write `false` |

### Animator parameters over the wire

`ZSyncAnimation` stores each parameter at `438569 + hash` and non-owners read it back
(`ZSyncAnimation.SyncParameters`, ZSyncAnimation.cs:100). Floats with `ZDO.Set(int, float)`,
**booleans as ints** (`GetInt`, so `value ? 1 : 0`). Hash with the public
`ZSyncAnimation.GetHash(name)` (it is `Animator.StringToHash`).

The names `Character` uses (Character.cs:490+): `forward_speed`, `sideway_speed`,
`turn_speed`, `inWater`, `onGround`, `encumbered`, `flying`, `falling`, `crouching`.
Writing `forward_speed` while you move the position is the difference between a man walking
and a man sliding. `SyncParameters` returns early unless the ZDO has an owner. Writing keys
the animator does not have is harmless. `ZDO.AddSessionHash` (what the real component calls)
only keeps a key out of the world save — unnecessary for a non-persistent ZDO.

### Movement without physics or pathfinding

The server has no colliders out there, so there is nothing to walk against and nothing to
path around. What works well: sample the player's ZDO position every ~0.25s into a
breadcrumb queue and walk from crumb to crumb, taking `y` from the crumb. The NPC then
goes round the rock the player went round and up the stairs the player went up, and the
server never learns either exists. Add a leash distance past which you stop retracing and
just reposition.

---

## 4. Chat: hearing players, and speaking to them

This is the part with the non-obvious gates. Three of them will silently eat your message.

### Outbound: what a client actually does when you type

`Chat.InputText` (Chat.cs:450):

```csharp
text = ((text[0] != '/') ? ("say " + text) : text.Substring(1));
TryRunCommand(text, this);
```

**A leading `/` never leaves the machine.** It is stripped and run through the player's own
`Terminal`, so `/mycommand` is an unknown local command, not a server message. Server-side
chat commands must be a plain word (`!mark`, `!help`).

| Typed | Console command | Transport |
| --- | --- | --- |
| `hello` | `say hello` | `Talker.Say(Normal)` → `ZNetView.InvokeRPC("Say")` on the **speaker's own character** |
| `/s hello` | `s hello` | `Chat.SendText(Shout)` → routed RPC `ChatMessage` |
| `/w hello` | `w hello` | `Talker.Say(Whisper)` → same as Normal |

Both paths go through `Chat.CheckPermissionsAndSendChatMessageRPCsAsync`, which sends **one
copy per recipient**, walking `ZNet.instance.GetPlayerList()` — and on a client that list is
whatever the server last broadcast.

### Gate 1: nobody is talking to the server

With one player online, every copy is addressed to themselves and **nothing is sent at
all**. A dedicated server is not in the player list, so a server-side plugin cannot
overhear chat by default. There is no `Chat` on a dedicated server either, so no handler is
registered for `ChatMessage`.

The fix is to put yourself in the list — see §5 — with a `m_characterID` whose `UserID` is
the server's session ID. Clients then address a copy to the server, and
`ZRoutedRpc.HandleRoutedRPC` sees it. Patch that as a **non-skipping prefix**:

```csharp
[HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
static void Prefix(ZRoutedRpc.RoutedRPCData data) {
    var pkg = new ZPackage(data.m_parameters.GetArray());   // copy: the original is about to be read
    if (data.m_methodHash == "Say".GetStableHashCode()) {
        int type = pkg.ReadInt();
        var who = new UserInfo(); who.Deserialize(ref pkg); // Name, then UserId, both strings
        string text = pkg.ReadString();
        // data.m_targetZDO is the SPEAKER's character - the accurate answer to "where are they"
    } else if (data.m_methodHash == "ChatMessage".GetStableHashCode()) {
        Vector3 pos = pkg.ReadVector3(); int type = pkg.ReadInt(); /* UserInfo, text */
    }
}
```

`ZRpc.Serialize` writes parameters **flat** with no length prefixes or type tags, so read
them in declaration order. `new ZPackage(byte[])` rewinds to 0 and `GetArray()` ignores the
read position, so a copy is safe. `ZRoutedRpc.RouteRPC` is the other hook point — it sees
everything the server *relays*, including messages between two clients, but nothing at all
when only one player is online.

Watch for duplicates on a listen server: the host's own line arrives once for them and once
for you. Dedupe on (sender, text) within a second.

### Gate 2: the receiving client's permission check

`Chat.OnNewChatMessage` runs `RelationsManager.CheckPermissionAsync(sender.UserId,
CommunicateWithUsingText, ...)` and **drops the message unless it is granted**
(RelationsManager.cs:~45):

- `!user.IsValid` → `Error` → dropped. `PlatformUserID.IsValid` is just non-null platform
  and non-null user ID, and `new PlatformUserID("Museum", "1830")` satisfies it.
- Same platform as the client → the real platform provider is asked. `SteamRelations.
  GetUserProfileAsync` parses the ID as a `ulong` and asks Steam; a made-up one can come
  back `InvalidID` → `Error` → dropped.
- **Different platform → `DifferentPlatformsNotAvailable`, which the check treats as
  granted.** Deterministic, no network round trip.

So give a synthetic sender a platform string that is *not* the players' platform
(`Museum_1830`, not `Steam_7656...`). That is the single least obvious load-bearing
decision in this whole plugin.

### Gate 3: the chat-log line needs a player-list entry

`Terminal.AddString(PlatformUserID, string, Talker.Type, bool)` (Terminal.cs:2985):

```csharp
if (!ZNet.TryGetPlayerByPlatformUserID(user, out var playerInfo)) {
    ZLog.LogError(...);
    return;                     // no chat line at all
}
string name = CensorShittyWords.FilterUGC(playerInfo.m_name, ...);
```

The displayed name comes from the **player list**, not from the `UserInfo.Name` you sent.
No entry, no line in the chat window — though `AddInworldText` still draws the floating
text, which is what "bubble but no chat line" means when you see it.

### Speaking

```csharp
ZRoutedRpc.instance.InvokeRoutedRPC(
    peerUid, "ChatMessage", position, (int)Talker.Type.Normal, userInfo, text);
```

- Target each peer individually (`ZNetPeer.m_uid`). Target `0L` means everybody and also
  handles locally. On a listen server, targeting `ZDOMan.GetSessionID()` is how the host's
  own `Chat` gets it (`InvokeRoutedRPC` handles `targetPeerID == m_id` locally).
- `ChatMessage` is **not range-checked by clients** (it is the shout transport), so judge
  distance server-side from `ZNetPeer.m_refPos` or the peer's character ZDO.
- `Talker.Type` changes the rendering: `Shout` is yellow and `ToUpper()` with a `Name:`
  prefix on the floating text, `Whisper` is `ToLowerInvariant()`, `Normal` is plain white.
  `Ping` skips the chat line entirely and draws the word "PING".
- Floating text is keyed by sender ID (`Chat.FindExistingWorldText`), so consecutive lines
  from you replace one bubble instead of stacking. `m_worldTextTTL` is 5s — pace multi-line
  output under that.
- The alternative is `ZNetView.InvokeRPC(peer, "Say", ...)` on an NPC's own ZDO, which
  anchors the bubble to the body and uses vanilla distance rules — but it needs the client
  to have instantiated that object already, and it lands in the same three gates.

---

## 5. The player list, and what it costs

`ZNet.UpdatePlayerList` (ZNet.cs:2454) clears and rebuilds `m_players` from the connected
peers, then `SendPlayerList` broadcasts it — on peer connect, on disconnect, and on a
periodic timer (ZNet.cs:1564). Append to it in a **postfix**; `ZNet.GetPlayerList()` is
public and returns the live list.

```csharp
new ZNet.PlayerInfo {
    m_name = "Mark Hall-Patton",                       // the name chat lines will show
    m_characterID = someZDOID,                         // .UserID is what routes chat to you
    m_userInfo = new ZNet.CrossNetworkUserInfo { m_id = ..., m_displayName = ..., ... },
    m_publicPosition = false,                          // true adds a map pin
}
```

`m_characterID` must not be `ZDOID.None` (clients skip those with a warning). A synthetic
`new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue)` works when you have no real object: the
server hands out IDs from 1 upwards and will never collide with it.

**Your entry also reaches the world save.** `UpdatePlayerHistory` is called right after
`UpdatePlayerList` (and again from `SendHistoricalPlayerList`), walks the same live list, and
copies anything new into `World.m_playerHistory` - which is written to the `.db`. A synthetic
player is therefore remembered by the world as someone who once visited it. Lift it out of
the list for the duration of that call (prefix removes, postfix re-adds) if you care; this
is the reason to append in a postfix rather than keep the entry in the list permanently.

**Everything that reads that list now counts your entry.** `ZNet.GetNrOfPlayers()` is
`m_players.Count`, and its callers are:

| Caller | Effect of a synthetic entry | Fixable server-side? |
| --- | --- | --- |
| `ZNet.RPC_PeerInfo`: `GetNrOfPlayers() >= 10` | Server is full one player early | Yes — postfix `GetNrOfPlayers` |
| `ZNet.UpdateNetTime`: `GetNrOfPlayers() > 0` | World clock keeps running in an empty world | Yes — same postfix |
| `CharacterDrop`: `drop.m_onePerPlayer` | One extra boss trophy per kill | **No** — each client computes drops from its own copy |
| `ConnectPanel` | Shows in the in-game player panel | Cosmetic, arguably wanted |

That last row is the price of admission: there is no way to be audible to a client without
being in the list it also uses for loot maths.

---

## 6. Using the `Player` prefab as an NPC chassis

Fine on clients — a server-owned `Player` is just a remote player to them, and every
owner-only branch is skipped. The trap is the **world origin**, where a dedicated server
*does* instantiate objects and therefore owns a real `Player` component:

| Method | What it does to a server-owned Player |
| --- | --- |
| `Player.FixedUpdate` (Player.cs:808) | `if (m_localPlayer != this) ZNetScene.instance.Destroy(gameObject)` — **it deletes itself**, ZDO and all |
| `Player.Update` (Player.cs:853) | Calls `TakeInput()`; there is no `ZInput` on a headless server |
| `Player.LateUpdate` (Player.cs:1654) | `ZNet.instance.SetReferencePosition(transform.position)` — drags the server's whole loaded area to wherever the NPC is |

Three one-line prefixes, gated on your own ZDO flag **and** `IsOwner()`, fix all three and
never fire on clients. Note what you do *not* have to patch: `Character.CustomFixedUpdate`
(driven from `MonoUpdaters` over `Character.Instances`, not from Unity's `FixedUpdate`) is
what runs motion, ground contact and animation, so skipping `Player.FixedUpdate` costs you
nothing you want. It does run `CheckDeath` when owned, so top the health back up or a
passing greydwarf will kill your NPC and drop a tombstone.

---

## 7. Things that are not what you would assume

- **A dedicated server has a `Chat` instance.** It is not a client-only component, so it
  registers the `ChatMessage` routed handler and will happily process messages addressed to
  the server - they just die at `Player.m_localPlayer == null` inside `OnNewChatMessage`.
  Do not use `Chat.instance != null` to mean "there is a player here". Use
  `ZNet.instance.IsDedicated()` or `Player.m_localPlayer`. (Found the hard way: the first
  server run logged `chat=True` on a headless box.)
- **`EnvMan` statics are half-usable headless.** `EnvMan.FixedUpdate` runs with no server
  gate, so `IsNight()`/`IsDay()` are correct — they come from `ZNet.GetTimeSeconds()`. But
  `IsWet()`, `IsCold()` and `IsFreezing()` depend on `GetCurrentEnvironment()`, which comes
  from `EnvMan.GetBiome()`, which reads **`Utils.GetMainCamera()`**. No camera → empty
  Meadows. Weather read on a dedicated server is not the weather over the player.
- **`Heightmap.FindBiome(point)` returns `Biome.None`** away from the origin: it needs a
  `Heightmap` instance. Use `WorldGenerator.instance.GetBiome(point)` — pure maths, and
  `WorldGenerator.Initialize` does run on the server (ZNet.cs:381).
- **`ZoneSystem.FindFloor` / `GetGroundHeight` want colliders**, so they are useless out
  where players are. Take `y` from a player's ZDO position instead, or from
  `WorldGenerator.GetHeight` if you can live without their terrain edits and buildings.
- **Console commands are client-side.** `Terminal.InitTerminal` and `Chat` live on a
  client; a dedicated server has no interactive console to register into. Chat is the
  only user interface you have.
- **Damage to something you own but have no instance of is discarded.** Clients send
  `Character.Damage` as an RPC to the owner; `HandleRoutedRPC` looks up
  `ZNetScene.FindInstance` and finds nothing. Effectively invulnerable, and effectively an
  aggro sink, since client-side AI still sees it as a valid `Character` of its faction.
- **A character's faction is a prefab field, not a ZDO field**, so you cannot make one
  instance non-hostile from the server.
- **`libParty.so` fails to load on a Linux dedicated server.** `DllNotFoundException:
  libParty.so` in the log is vanilla PlayFab party noise, present with no mods at all.
  Ignore it.
- **`UserInfo` is a class the game mutates** (`CensorShittyWords.Filter(ref Name)`), so hand
  out a fresh instance per send rather than a cached one.

---

## 8. Useful prefab names (1.0.16)

Hashes are `"Name".GetStableHashCode()`; `0` means an empty slot.

- Hair: `HairNone`, `Hair1`–`Hair38`. `Hair5` is "Short", `Hair36` is "Chronicler".
- Beard: `BeardNone`, `Beard1`–`Beard26`. `Beard3` is "Short", `Beard17` "Neat",
  `Beard25` "Trimmed", `Beard22` "Mustache".
- Hats worth knowing: `HelmetStrawHat` (wide brim), `HelmetFishingHat`, `HelmetPointyHat`,
  `HelmetOdin`, `HelmetMidsummerCrown`, `HelmetDverger`, `HelmetHat1`–`HelmetHat10`
  (headscarves and caps), `HelmetSweatBand`.
- Civilian clothes: `ArmorRagsChest/Legs`, `ArmorLeatherChest/Legs`, `ArmorTunic1`–`10`,
  `ArmorDress1`–`10`.
- Capes (shoulder slot, variant 0 quality 1): `CapeLinen`, `CapeDeerHide`, `CapeOdin`,
  `CapeWolf`, `CapeLox`, `CapeFeather`, `CapeAsh`.

The character-creator display names are in `resources.assets` as
`customization_beardNN` / `customization_hairNN`, which is how to map a number to a look
without launching the game.

---

## 9. Datamining the game from inside it

You do not need to unpack asset bundles to get at the game's content: the server already
loaded it. `ObjectDB` and `ZNetScene` are both present and populated on a dedicated server,
and between them they hold everything an NPC might be asked about.

| Source | Gives you |
| --- | --- |
| `ObjectDB.instance.m_items` | Every item prefab. `GetComponent<ItemDrop>().m_itemData.m_shared` has name and description tokens, `m_itemType`, `m_damages` (a `HitData.DamageTypes` with a field per damage type), `m_armor`, `m_blockPower`, `m_food`/`m_foodStamina`/`m_foodEitr`/`m_foodBurnTime`, `m_maxDurability`, `m_weight`, `m_maxStackSize`, `m_maxQuality`, `m_teleportable` |
| `ObjectDB.instance.m_recipes` | Every recipe: `m_item`, `m_amount`, `m_craftingStation`, `m_minStationLevel`, `m_resources` as `Piece.Requirement[]` |
| `ZNetScene.instance.m_prefabs` + `GetComponent<Character>()` | Every creature: `m_name`, `m_health`, `m_faction`, `m_boss`. Add `CharacterDrop.m_drops` for the loot table and `Tameable` for whether it can be tamed |
| `ZNetScene.instance.m_prefabs` + `GetComponent<Piece>()` | Every building piece and its `m_resources` |

Four traps, all found by reading the generated output rather than the code:

- **`SharedData` has defaults for fields the item does not use.** Armour 10, block 10 and
  durability 100 are on *everything*, so a log of wood looks like light armour. Gate armour
  on the item type being worn, block on `Shield`, and durability on `m_useDurability`.
- **Some item prefabs also carry a `Piece`**, and read as a build cost that says a cooked
  boar steak is built from one cooked boar steak. Skip prefabs with an `ItemDrop` when
  walking pieces.
- **`Piece.Requirement.m_upgraderResource`** marks an ingredient that only applies to
  levelling the item up. Include it and every recipe gains a phantom component.
- **Variant prefabs duplicate their originals.** `Troll_sleeping` has the same stats as
  `Troll`. De-duplicate on the text with the prefab name removed, which keeps genuinely
  different variants (the two longships have different nail counts) and drops the clones.

### What the game files do not tell you

Two things worth knowing before you plan an index around them:

- **There is no "which biome is this found in" anywhere in the item or creature data.** It
  is a property of the spawn tables (`SpawnSystemList`) and the zone vegetation lists, not of
  the prefab, and reconstructing it from those is a much bigger job than it sounds. This is
  also the single most common thing anyone asks about the game, so plan for it.
- **`ObjectDB.GetAllBuildPieces()` is the buildable list, not `GetComponent<Piece>()`.** That
  method walks the piece tables of the hammer, hoe and cultivator, which is the definition of
  something a player can construct. Scanning prefabs for the component instead turns up
  legacy pieces nobody can build - including a second, cheaper "Portal" that exists only in
  the files, which is precisely the sort of answer that sends somebody hunting for a recipe
  that is not in the game.

For the first one, an external source is the honest answer. The wiki's infobox **fields**
(`| location = [[Black Forest]]`, `| type =`, `| tameable =`) are a factual database and
reduce to one short line per page; `tools/fetch-wiki.sh` in this repo reads that whitelist
and nothing else, one request per page with a pause between, and records the source and its
CC BY-SA licence in the generated file. There is no need to copy article prose for this: the
numbers are already in the game data and the model writes its own sentences.

Fandom's MediaWiki API has no TextExtracts extension, so `prop=extracts` is unavailable.
`action=parse&prop=wikitext&section=0` gives just the lead section, which is where the
infobox lives, and is the smallest useful request.

### Display names, without Localization

`Localization` is **client-only** - it is not in the dedicated server's assembly at all, so
`m_name` is only ever the raw `$item_sword_iron` token there. The strings themselves *are*
on the server, in `valheim_server_Data/resources.assets`, as CSV rows of
`"token","English","Swedish",...`. English is ASCII and survives `strings`, so the whole
table comes out with two lines of shell - `tools/extract-localization.sh` in this repo does
exactly this:

```sh
strings -n 6 resources.assets \
  | grep -oE '^"[a-zA-Z0-9_]+","[^"]*"' \
  | sed -E 's/^"([a-zA-Z0-9_]+)","(.*)"$/\1\t\2/'
```

That yields ~5,600 token/English pairs including every `*_description`, which is flavour
text the game itself never shows twice. Without the table, splitting the prefab name on
capitals (`SwordIron` -> "Sword Iron") is a surprisingly good substitute for matching
player questions, since both sides reduce to the same word set as "iron sword".

## 10. Running a server to test against

The dedicated server is Steam app **896660**, free and anonymous - no account, no client
install. Its `assembly_valheim.dll` is **not** the client one: 632 decompiled types against
the client's ~2,500, so re-check anything you relied on against the server build before
trusting it. Every API in this document is present in both.

On NixOS the binary is an unpatched Unity ELF. Two ways to run it:

- **nix-ld**, if `NIX_LD` is set: export `NIX_LD_LIBRARY_PATH` with the server's own
  `linux64/` prepended and run the binary directly. Nothing beyond nix-ld's default library
  set is needed - it is a headless build.
- **An FHS environment** (`pkgs.buildFHSEnv`) otherwise. Works, but it is bubblewrap: it can
  fail with `bwrap: Can't make symlink at /bin` when started from an already-sandboxed or
  non-interactive context, which makes it the worse choice for something long-running.

Either way, give doorstop's `LD_PRELOAD` an **absolute** path rather than the bare
`libdoorstop_x64.so` the BepInEx pack uses. The pack relies on `LD_LIBRARY_PATH=./doorstop_libs`,
and the loader that ends up resolving it may not have that - nix-ld rewrites the library path,
and an FHS wrapper changes the working directory.

Proof that a plugin is actually live, in the server's stdout:

```
[Message:   BepInEx] Chainloader startup complete (1 loaded, 0 skipped, 0 failed)
09/30 13:03:51: isModded: True
09/30 13:03:57: Game server connected          <- joinable from here
09/30 13:04:14: Registering lobby
```

`BepInEx/LogOutput.log` is not written until the process exits cleanly, so read stdout while
it runs. Log a startup block of your own: the things worth asserting - is this a server, is
it dedicated, is the prefab you depend on present, did every patch bind - are all knowable in
one place at startup, and each is otherwise a silent failure later.

## 11. Harmony targets used here, with signatures

All private unless noted; parameter names matter for Harmony injection.

| Target | Signature |
| --- | --- |
| `ZRoutedRpc.HandleRoutedRPC` | `void HandleRoutedRPC(RoutedRPCData data)` |
| `ZRoutedRpc.RouteRPC` | `void RouteRPC(RoutedRPCData rpcData)` (relay hook, sees traffic between clients) |
| `ZNet.UpdatePlayerList` | `void UpdatePlayerList()` |
| `ZNet.GetNrOfPlayers` | `public int GetNrOfPlayers()` |
| `ZNet.UpdatePlayerHistory` | `void UpdatePlayerHistory()` |
| `Player.Update` / `FixedUpdate` / `LateUpdate` | `void ...()` — all declared on `Player`, so `[HarmonyPatch(typeof(Player), "Update")]` resolves to `Player`'s and not `Character`'s |
| `Talker.RPC_Say` | `void RPC_Say(long sender, int ctype, UserInfo user, string text)` |
| `Chat.RPC_ChatMessage` | `void RPC_ChatMessage(long sender, Vector3 position, int type, UserInfo userInfo, string text)` |

`Harmony.PatchAll` throws if a target name does not resolve, so a bad name is a startup
failure and not a silent no-op — check `BepInEx/LogOutput.log` for
`Chainloader startup complete`.

---

## 12. Checklist for the next one

1. Decompile first. Every design decision above came from reading a method, and several
   contradict the obvious guess.
2. Decide early whether the thing can be expressed as ZDO fields. If it cannot, a
   server-only plugin is the wrong shape and you need a client mod.
3. Gate everything on `ZNet.instance.IsServer()`, and assume no `Player.m_localPlayer`, no
   `ZInput`, no camera and no colliders. There *is* a `Chat` instance, which is not the
   same as there being anyone to read it (§7).
4. For anything player-visible, find the vanilla code path a client already runs for
   *another player* and feed it. Do not invent a protocol — a client cannot speak yours.
5. Expect the near-origin case to behave like a listen server, because it does.
6. Test the empty-server and one-player-online cases specifically. Most of the gates above
   only bite when there is nobody else to relay through.
7. Run a real dedicated server early. Two of the notes in this document are corrections to
   things that had been reasoned out carefully and were still wrong, and both showed up in
   the first sixty seconds of the first run.
