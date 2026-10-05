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

Which cuts the other way too: **the player list is how a server renames somebody** on a stock
client. Rewriting `PlayerInfo.m_name` in an `UpdatePlayerList` postfix changes the name on chat
lines, in the player panel and on map pins at once, and the chat line is composed as
`"<color=orange>" + name + "</color>: …"` into TMP, so the name can carry its own rich text and
a nested `</color>` returns to orange rather than white. `src/Verse/AdminTag.cs` is that, for an
`[ADMIN]` badge. Rewrite only the broadcast copy — `ZNetPeer.m_playerName` is what
`GetPeerByPlayerName`, your own commands and the server log all resolve — and remember that a
name is unvalidated client input (`FejdStartup` checks a length of three and nothing else) while
chat text is bracket-stripped on arrival and names are not, so anything you render as authority
has to be scrubbed out of every other name first.

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
- **A ZDO's owner is its simulator, not its author.** `ZDOMan.ReleaseNearbyZDOS`
  (ZDOMan.cs:930) calls `SetOwner(uid)` on every *persistent* ZDO in a player's active area
  every two seconds, so ownership follows whoever is standing nearest and churns constantly.
  `GetOwner()` answers "who is simulating this", never "whose is this". If you need to know
  who made something, record it when the object is created — for a client-made object, that
  is the `CreateNewZDO` call inside `ZDOMan.RPC_ZDOData`, which only fires for a ZDOID the
  server has not seen before.
- **ZDO visibility is already per-peer, with one gate.** `ZDOMan.CreateSyncList`
  (ZDOMan.cs:1261) builds a separate list per peer, and all four send paths — near objects,
  distant objects, force-sends, the client change queue — go through
  `ZDOPeer.ShouldSend(ZDO)` (ZDOMan.cs:52), which is otherwise only a revision check. That
  is the lever for anything instancing-shaped: a client cannot act on an object it was never
  sent. Portals come free, because a client matches them among the ZDOs it knows.
- **The ZDO send scheduler services one peer per *frame*.** `ZDOMan.SendZDOToPeers2`
  (ZDOMan.cs:886) starts a round every 50 ms and then does one peer per frame, so each
  player's update period is `50ms + N × frametime` — ~170 ms at 10 players, ~650 ms at 36.
  It is a scheduling artefact, not bandwidth: `SendZDOs` (ZDOMan.cs:1057) already bounds
  itself per peer against the socket's send queue. This is the real reason
  `ZNet.RPC_PeerInfo` caps a server at ten players (ZNet.cs:1038).
- **`ZDO.Set`'s `okForNotOwner` flag is ignored.** The 1.0.16 body (ZDO.cs:394) writes and
  bumps `DataRevision` whatever you pass, so a server can write a field on a ZDO a client
  owns. Fields are per-key, so the owner's own writes do not clear it, and vanilla saves ZDO
  fields with the world — which makes a ZDO field the cheapest durable place to put
  server-side metadata about someone else's object.
- **Clients generate terrain and biomes themselves, from the seed.** `Heightmap.Generate`
  (Heightmap.cs:421) and `HeightmapBuilder` call `WorldGenerator.instance` on the client, and
  biomes are radial from the origin. So you cannot move content to a different coordinate and
  keep the terrain it sat on, and no server patch can put Meadows 8 km out.
- **Teleporting is client-authoritative.** `Player.TeleportTo` (Player.cs:5888) runs on the
  client and RPCs its own ZDO; the server cannot refuse it. Anything you want to be
  unreachable has to be invisible, not far away. The server *can* teleport a player, though:
  `Character.cs:713` registers `RPC_TeleportTo` on the player's own `ZNetView`, so routing it
  by ZDOID works even with no instance on the server.
- **Location icons are pushed per peer, and are what a fresh client spawns on.**
  `ZoneSystem.SendLocationIcons(long peer)` (ZoneSystem.cs:835) targets one peer, and on a
  client `GetLocationIcon` (ZoneSystem.cs:2658) reads only what the server sent. Since
  `Game.FindSpawnPoint` (Game.cs:524) falls back to `GetLocationIcon("StartTemple")`, a
  server can give two players different spawn points with nothing installed on their end.
  Note `SendLocationIcons(0L)` at ZoneSystem.cs:2242 broadcasts, so per-peer work has to
  intercept that too.
- **Private nested types are patchable.** `ZDOMan.ZDOPeer` is private; reach it with
  `AccessTools.Inner(typeof(ZDOMan), "ZDOPeer")` and target its methods via a
  `TargetMethod()`. A patch may take a parameter of an inaccessible type by declaring it
  `object` — Harmony matches it by name.

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

Used by [`Verse`](verse-design.md), which filters what each peer is told about:

| Target | Signature |
| --- | --- |
| `ZDOMan.ZDOPeer.ShouldSend` | `bool ShouldSend(ZDO zdo)` — private nested type; see §7 |
| `ZDOMan.SendZDOs` | `bool SendZDOs(ZDOPeer peer, bool flush)` — take `peer` as `object` |
| `ZDOMan.SendZDOToPeers2` | `void SendZDOToPeers2(float dt)` (the send scheduler) |
| `ZDOMan.AddPeer` / `RemovePeer` | `public void AddPeer(ZNetPeer netPeer)` |
| `ZNet.SendPlayerList` | `void SendPlayerList()` (builds one list for everybody) |
| `ZoneSystem.SendLocationIcons` | `void SendLocationIcons(long peer)` |
| `ZNet.RPC_PeerInfo` | `void RPC_PeerInfo(ZRpc rpc, ZPackage pkg)` — the admission gate: player cap and password both live here |
| `ZNet.SetServer` | `public static void SetServer(bool server, bool openServer, bool publicServer, string serverName, string password, World world)` |
| `ZSteamMatchmaking.RegisterServer` | `public void RegisterServer(string name, bool password, GameVersion gameVersion, string[] modifiers, uint networkVersion, bool publicServer, string worldName, ServerRegistered cb)` |

`Harmony.PatchAll` throws if a target name does not resolve, so a bad name is a startup
failure and not a silent no-op — check `BepInEx/LogOutput.log` for
`Chainloader startup complete`.

---

## 12. Getting listed in the community browser

Four separate pieces of the game decide what a stranger sees in the server list, and three of
them are reachable from a server-side plugin.

**Read this first: on the PlayFab backend, appearing in the list at all is a lottery, and no
property of your server changes the odds.** Everything below about sorting is real but applies
only *after* you have been drawn, so it is the second question, not the first. The browse is
paginated by a bucket the server assigns itself at random:

```csharp
// ZPlayFabMatchmaking
private static int GetSearchPage() => UnityEngine.Random.Range(0, 4);   // stored as number_key11

// ZPlayFabLobbySearch.FindLobbyWithPagination
Filter = m_searchFilters[m_currentFilter] + $" and number_key11 eq {m_currentPage}",
Pagination = new PaginationRequest { PageSizeRequested = 50u }
```

Four buckets, 50 lobbies each: that is where the 200 comes from, and `FindLobbies` is called
with **no `OrderBy`**. Every public server randomly lands in one of four buckets and PlayFab
returns an arbitrary 50 of each, so with a population of N the chance of being in a given
refresh is roughly `200 / N`. There is no name, age, player count or property that promotes
you, because nothing in the request expresses a preference.

The symptom this produces is confusing and worth recognising: **a server that is reliably
findable by name but absent from the unfiltered list**. Same code path, different arithmetic —
the name filter (see `CreateNameSearchFilter` / `CharToKeyName`, a letter-frequency index over
`number_key15..30` with `ge <count>` per character) narrows thousands of lobbies to a handful,
which fits inside the 50-per-bucket cap, so it is deterministic. Unfiltered, the cap bites. Do
not read "found by search" as "registered correctly but sorted badly"; it is the stronger
signal, and it means the browse is not a channel you can rely on at all.

So for discoverability, prefer the things that are deterministic: the **join code**, the direct
`host:port`, and a distinctive word in the name for people to search. Treat list position as a
bonus that occurs on the refreshes you happen to win.

**Once you are in the 200: the list is capped and sorted afterwards, and on the Steam backend
dedicated servers are last in line for those 200.** This part defeats the obvious trick.
`CommunityServerList.GetFilteredList` round-robins across the backends until it has 200
entries and *then* sorts:

```csharp
while (resultOutput.Count < 200) { /* take entry `num` from each backend in turn */ }
resultOutput.Sort((ServerListEntryData a, ServerListEntryData b) =>
    a.m_serverName.CompareTo(b.m_serverName));
```

and the Steam backend's own `ZSteamMatchmaking.GetServers` has already applied a 200 cap of
its own, in an order that puts player-hosted lobbies ahead of dedicated servers:

```csharp
public void GetServers(List<ServerData> allServers)
{
    if (m_friendsFilter) { FilterServers(m_friendServers, allServers); return; }
    FilterServers(m_matchmakingServers, allServers);   // public lobbies first
    FilterServers(m_dedicatedServers, allServers);     // dedicated servers second
}

private void FilterServers(List<ServerData> input, List<ServerData> allServers)
{
    string text = m_nameFilter.ToLowerInvariant();
    foreach (ServerData item in input)
    {
        if (text.Length == 0 || item.m_matchmakingData.m_serverName.ToLowerInvariant().Contains(text))
            allServers.Add(item);
        if (allServers.Count >= 200) break;
    }
}
```

So with an empty search box, 200+ public lobbies mean the dedicated-server list is never
reached at all. **A sort-early name gets you to the top of whatever 200 were fetched; it does
not get you into the 200.** Worth stating plainly because the sort is easy to find, easy to
act on, and on its own does nothing for a server that is being truncated away.

What *does* reliably surface a server is the search box: the filter is a lowercased
`Contains` on the name applied **before** the cap, so any distinctive substring in the name
collapses the list to a handful. Design the name so that searching an obvious word finds it.

The sort is worth winning only for the refreshes where you were drawn. `CompareTo` is
**culture-sensitive**, and that is not the ordinal order — measured against .NET 8's ICU with
the tag held constant, earliest prefix first:

```
  1. TAB            2. 5x SPACE      3. 2x SPACE     4. SPACE
  5. ENSP U+2002    6. NBSP U+00A0   7. __           8. _
  9. -             10. !            11. (no prefix)
      ignorable, i.e. identical to no prefix at all: U+0001, ZWSP U+200B, BOM U+FEFF
```

Three things to take from that. `!` is mid-table, not near the front — it only looks early in
the *ordinal* table, which also puts `_` dead last, so the two orders disagree completely and
only the culture-sensitive one is used. More of the same character sorts earlier than fewer.
And the zero-width characters that look like the clever answer are **ignorable**: they sort
identically to no prefix, so they do nothing.

A corollary worth knowing when reading a list: leading whitespace is invisible in the row, and
a name beginning `<color=…>` displays as the text inside the tag while sorting on the `<`. So
the entry above yours may not start with what it appears to start with. (Our own name sorted
ahead of `!Aardvark` precisely because `!<` beats `!A` — a symbol outranks a letter.)

Put the sort character *outside* any markup tag so it is still the sort key, and keep it mild:
a long whitespace prefix renders as a broken-looking indent, and on a crossplay server it also
ends up inside the PlayFab custom ID (see below), where it is load-bearing for no gain.

**The name is rich text.** `ServerListElement` assigns it straight to a `TMP_Text`:

```csharp
m_serverName.text = CensorShittyWords.FilterUGC(m_serverListEntry.m_serverName, ...);
```

so TextMeshPro markup — `<color=#FFFF00>…</color>`, `<b>`, `<i>` — is parsed rather than
printed. The markup counts against the length budget and against the sort key, so put the
character you are sorting on *before* the opening tag.

**The length limit is Steam's, not Valheim's.** `ZSteamMatchmaking.RegisterServer` passes the
name to `SteamGameServer.SetServerName`, which caps at `k_cbMaxGameServerName` = 64 bytes
including the terminator, so **63 usable bytes** — of which a single `<color=#RRGGBB>…</color>`
pair spends 23. Valheim does not truncate first and gives no warning; the name is silently
cut. `SetMapName` next to it caps at 32 and *will* visibly cut a long name, but Valheim's own
browser never shows the map field. Verify the real thing off the wire rather than from the
log, with an A2S query on `port + 1`:

```sh
printf '\xff\xff\xff\xffTSource Engine Query\x00' \
  | socat -T4 - UDP:host:2457 | od -A d -c            # replies 'A' + 4 challenge bytes
printf '\xff\xff\xff\xffTSource Engine Query\x00\xAA\xBB\xCC\xDD' \
  | socat -T4 - UDP:host:2457 | od -A d -c            # resend with those four bytes
```

The `I` response carries, in order: protocol, **name**, map, folder, game, appid (short),
players, **max players**, bots, server type (`d`), environment (`l`), **visibility** (0 public,
1 password-protected), VAC, version. That one packet confirms the name survived whole, the
advertised cap, and the padlock state.

**`SetMaxPlayerCount` is advertised separately from being enforced.** `RegisterServer` hard-codes
`SteamGameServer.SetMaxPlayerCount(10)`, overwriting the 64 `SteamManager` set at init. It has
no part in admitting anyone — `ZNet.RPC_PeerInfo` is the gate — so raising the real cap without
this one makes the entry read `12 / 10`, worse than full, to everybody choosing a server.

**A listed server must have a password, and that is checked before any plugin can help.**
`FejdStartup.ParseServerArguments`:

```csharp
if (flag && !IsPublicPasswordValid(password, createWorld))   // flag is -public
{
    ZLog.LogError("Error bad password:" + publicPasswordError);
    Application.Quit();
    return false;
}
```

`-public 1` with no `-password`, or one under five characters, or one that is a substring of
the world name or the seed name, **exits the process** — it does not fall back to running
unlisted. Note also that `-public` defaults to *true* when the flag is absent entirely.

Everything after that check reads one static field, `ZNet.m_serverPassword`, in four places:
`OpenServer` turns `!= ""` into the browser padlock (and `OnSteamServerRegistered`'s retry
coroutine recomputes the same flag per re-register), `RPC_ServerHandshake` sends
`!string.IsNullOrEmpty` of it to the client as `needPassword` (which is what raises the
dialog), and `RPC_PeerInfo` compares it against what the client sent. The field is **assigned
in exactly one place** — the last line but two of `SetServer` — which is what makes removing it
cleanly possible rather than a game of whack-a-mole.

That gives two ways to run a server anyone can get into, and the choice between them is about
what the entry looks like, not about whether it works. Both are implemented in
`src/Verse/PublicServer.cs`, behind a config key each:

- **Blank the field** in a postfix on `SetServer` (`NoPassword`) — no padlock, no dialog at
  all. The client side lines up on its own: `RPC_ClientHandshake` takes its `else` branch when
  `needPassword` is false and calls `SendPeerInfo(rpc)` on the default `password = ""`, and
  `SendPeerInfo` writes `string.IsNullOrEmpty(password) ? "" : HashPassword(…)` — a literal
  empty string, not the MD5 of one — so the server's own `"" != ""` admits the peer. Note the
  `FejdStartup.ServerPassword` auto-submit sits *inside* the `if (needPassword)` branch, so not
  even a client holding a remembered password for this server will send one. A postfix rather
  than an argument rewrite because passing `""` through would skip `HashPassword`, hence skip
  the lazy `ServerPasswordSalt()` that still gets sent to clients regardless.
- **Rewrite only the comparison** in `RPC_PeerInfo` (`AcceptAnyPassword`) — padlock shown,
  dialog raised, any input accepted. The client's `OnPasswordEntered` ignores an empty
  submission, so the player must type *something*; it just does not matter what.

The two compose, and that is deliberate: `NoPassword` is sufficient on its own, and leaving
`AcceptAnyPassword` on alongside it means a game update that moves the field degrades to a
dialog that still accepts anything, rather than locking everyone out of a server advertised as
open. The startup check can only report the *intent* — `Awake` runs before `SetServer` — so the
line to confirm in the journal is the postfix's own `password removed: …`.

Note that the padlock is display-only. `isPasswordProtected` is read in exactly one place,
`ServerListElement.UpdateTextAndIcons`, to toggle the row's `Private` icon; nothing in
`GetServers`, `FilterServers` or `GetFilteredList` tests it, and `RequestInternetServerList` is
called with zero filters. A passwordless server is not hidden from the community list — if one
is missing from it, look at the 200-entry cap above first.

### `-crossplay`, and the three things that stop it working

`-crossplay` sets `ZNet.m_onlineBackend = OnlineBackendType.PlayFab`, which makes `OpenServer`
call `ZPlayFabMatchmaking.RegisterServer` *instead of* the Steam one — it is either/or, never
both. The reason to want it is the search box: `PlayFabMatchmaking.ServerSideFiltering` is
`true` and `RefreshPublicServerList` passes the term to
`ZPlayFabMatchmaking.ListServers(m_filterLowerInvariant, …)`, so the backend returns matches.
That is a real query, not the Steam backend's local filter over a truncated list. A crossplay
server also gets a **join code**, which is a better answer to "how do my friends find it" than
any browser.

Four things bite, in the order you will hit them — and the fourth made it unusable here, so read
it before committing to this backend:

1. **`libparty.so` will not load on a headless box.** The symptom is
   `DllNotFoundException: libParty.so` from `PartyCSharpSDK.SDK.PartyInitialize`, and the
   misleading part is the name in the message: the file ships as
   `valheim_server_Data/Plugins/libparty.so` (lower-case p) and is present. The real cause is a
   missing *dependency* — `ldd` reports `libpulse-mainloop-glib.so.0 => not found`, because the
   Party SDK is a voice-chat library that links PulseAudio's glib mainloop. Mono reports a
   failed `dlopen` of a dependency as `DllNotFoundException` on the top-level library, which
   sends you hunting for the wrong file. On Ubuntu:
   `apt-get install --no-install-recommends libpulse-mainloop-glib0`. Always run
   `ldd .../libparty.so | grep "not found"` before believing anything about the filename.

2. **The server name must be 54 characters or less, or the server quits at boot.**
   `ZPlayFabMatchmaking.RegisterServer` builds a PlayFab account out of it:
   ```csharp
   PlayFabManager.SetCustomId(new PlatformUserID(new Platform("PlayFab"),
       $"{name}_{m_serverPort}_{SystemInfo.deviceUniqueIdentifier}" + (InstanceId ?? "")));
   ```
   `PlatformUserID.ToString()` prefixes `PlayFab_`, so the id is 8 + name + 1 + 4 (port) + 1 +
   32 (`deviceUniqueIdentifier`) = **name + 46**, against PlayFab's 100-character `CustomId`
   limit. A 60-character name produces 106 and the login is rejected with
   `/Client/LoginWithCustomID: Invalid input parameters` — after which the code calls
   **`Application.Quit()`**. Nothing in that error mentions the name, the length, or the limit.
   TextMeshPro markup in the name is *fine* here; only the length matters, which is worth
   knowing before you blame the `<color>` tag as we did.

3. **There is no UDP listener on the game port.** Under PlayFab the transport is a Party
   network over Azure relays (`Joined PlayFab Party network with ID …`), so `ss -lunp` shows
   only the Steam query socket on `port + 1`. Do not read a missing 2456 listener as a failure
   the way you would on the Steam backend — read the log instead, which should end with
   `Session "<name>" with join code <n> and IP <ip>:2456 is active with N player(s)`.

4. **Registration can hang in `State.Creating` forever, and it is both silent and total.** The
   lobby is created and `registered with join code <n>` is logged, then `CheckJoinCodeIsUnique()`
   must come back before anything works:

   ```csharp
   else if (result.Lobbies.Count == 1 && result.Lobbies[0].Owner.Id == GetEntityKeyForLocalUser().Id)
       ActivateSession();       // UpdateLobby: SearchData["string_key2"] = "True"
   else
       OnSessionUpdated(State.RegenerateJoinCode);
   ```

   `string_key2` is set to `True` **only** by `ActivateSession`, and the join-code lookup, the
   name search and `FindServerByIp` all filter on `string_key2 eq 'True'` — so an unactivated
   server matches nothing at all, by any route, while looking perfectly healthy. The client says
   "couldn't resolve join code" or "failed to connect".

   There is **no timeout**: `m_retries = 100` only decrements when the callback *returns* zero
   lobbies, so a dropped response wedges the state machine permanently with systemd reporting
   `active`. **The only evidence is the absence of the `is active with …` log line** — make that,
   not process liveness, your health check, and put a watchdog on it.

   Observed here after ~8 restarts in 40 minutes: six consecutive instances were issued the
   *same* join code and the last four never activated, which suggests a stale lobby from an
   earlier run still owning that code and failing the `Count == 1 && Owner.Id == mine` test.
   `-instanceid` is **not** a way out — it appends to the custom ID and does produce a new PlayFab
   entity, but the join code came back identical, so it is not entity-derived. Nothing
   server-side was found that releases a stale code; the practical remedy was to revert to the
   Steam backend and let PlayFab's index age out. Avoid restart churn on this backend.

**A crossplay server disappears from every Steam-side tool.** Measured on the same box, minutes
apart: `ISteamApps/GetServersAtAddress` went from returning the entry to
`"No servers found at that address"`, and A2S on `port + 1` went from a reliable 228-byte reply
to nothing at all, 3 attempts out of 3 — the socket is still bound, it just stops answering,
because the Steam `RegisterServer` path that sets the name, player count and
`SetAdvertiseServerActive` never runs. Two consequences worth planning around:

- The A2S recipe above, and the keyless `GetServersAtAddress` check, are **Steam-backend only**.
  They are the quickest way to read any server's *raw* name byte-for-byte — which matters,
  because leading whitespace and markup in a name are invisible in the browser but are what the
  list sorts on — and neither works against a crossplay server.
- Monitoring that pings the query port to decide whether the server is healthy will report it
  down forever once `-crossplay` is added.

Valheim will show you a dedicated server's address, which is the way in to reading its name:
`ServerJoinDataTypeExtentions.DisplayUnderlyingDataToUser` is
`switch (false, true, false, true)` over `None, SteamUser, PlayFabUser, Dedicated`, and
`ServerListElement.UpdateTextAndIcons` appends `m_joinData.ToString()` to the row's tooltip when
it is true. So hovering a `Dedicated` entry in the browser shows `host:port`.

One consequence for a plugin that keys anything per account: `ZSteamSocket.GetHostName()`
returns a bare Steam ID and `ZPlayFabSocket.GetHostName()` returns
`PlatformUserID.ToString()`, which is `GetPlatformPrefix(platform) + userID` — the same player,
spelled `76561198033210929` on one backend and `Steam_76561198033210929` on the other. Adding
`-crossplay` to a server whose state is keyed on the first form silently orphans all of it. See
`src/Verse/AccountId.cs`, which exists entirely because of this.

---

## 13. Checklist for the next one

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
