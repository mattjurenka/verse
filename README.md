# Mark Hall-Patton

A Valheim **server** plugin that summons Mark Hall-Patton — museum administrator for the
Clark County Museum System, and the man you know from *Pawn Stars* — into your world, so
you can ask him about southern Nevada history in chat while he walks around after you.

Nothing is installed on the players' machines. They connect with a completely unmodified
game, type `!mark` in chat, and an old man in a wide-brimmed hat with a short grey beard
walks over and starts answering questions.

Built against **Valheim 1.0.16** with **BepInEx 5.4.23.5** (BepInExPack_Valheim 5.4.2351).

## Using it

Everything happens in chat, because chat is the only channel a vanilla client will send
to a server unprompted.

| Typed in chat | What happens |
| --- | --- |
| `!mark` | He walks over to you. If he is already out with someone else, he comes to you instead |
| **anything, near him** | He answers it, after a couple of seconds of thinking. Southern Nevada history is what he answers best, but he can also look up anything in this game - recipes, creature health, drops, build costs |
| `!mark when did the dam open?` | Asking by name works from any distance |
| `!mark stay` | He waits where he is |
| `!mark come` | He starts following you again |
| `!mark go` | He heads back to Henderson (despawns) |
| `!mark help` | He explains the above, to you only |
| `!mark lookup <thing>` | Shows what his notes actually hold for that thing, for when you want to see the raw entry |
| `!mark debug` | Reports what the server heard and where the reply went |

There is deliberately **no slash command**. `Chat.InputText` strips a leading `/` and runs
the rest through the player's *own* console, so `/mark` would never reach the server at
all. A plain word does.

He can only be in one place at a time, because there is only one of him. He goes home by
himself when whoever summoned him disconnects.

## Installing

On the **server only** — the dedicated server install, or the machine hosting if you play
on a listen server.

1. Install BepInExPack_Valheim into the server folder (this is the one in `tools/bepinex`).
2. Drop `HallPatton.dll` into `BepInEx/plugins/HallPatton/`.
3. Start the server through `start_server_bepinex.sh` rather than the plain executable.
4. Put your model API key in the config (see [Generated answers](#generated-answers-meta-muse)).

Confirm it loaded:

```sh
grep -i "hall-patton" "$SERVER_DIR/BepInEx/LogOutput.log"
```

Players need to do nothing at all. There is no version check, no config sync, and no
network protocol of this plugin's own — so a client cannot be out of date with it.

## Trying it locally

`./server.sh` runs a local dedicated server with the plugin in it, so you can connect from
the game and see whether any of this works. It installs BepInEx into the server folder and
builds the plugin against the server's own assemblies first, both idempotently.

```sh
./server.sh            # start it, Ctrl-C to stop
./server.sh --logs     # follow the log instead
./server.sh --clean    # throw the test world away and start fresh
```

It needs the dedicated server itself, which is a separate free Steam app - no account:

```sh
NIXPKGS_ALLOW_UNFREE=1 nix run --impure nixpkgs#steamcmd -- \\
  +force_install_dir $PWD/tools/server +login anonymous +app_update 896660 validate +quit
```

Then wait for `Game server connected` (up to a minute the first time, for world
generation) and join from the game: **Join Game -> Join IP**, `127.0.0.1:2456`, password
`museum`. Override any of it: `WORLD=Foo PORT=2456 PASSWORD=secret ./server.sh`.

On NixOS the server binary is a prebuilt Unity ELF, so `server.sh` runs it through nix-ld
if it is available and through the flake's `#fhs` environment otherwise. On any other
distribution it runs directly.

Once in the world, type `!mark`. If nothing happens, type `!mark debug` - the reply is
itself the test, because it can only arrive if the server overheard you and its answer
reached your chat window, which are the two things that fail silently. It also reports
where he is, how many players are in earshot, whether the model key is present, and the
last model failure.

## Hosting it on a real server

The droplet needs no .NET and no Nix. The plugin is a single 96 KB DLL built here and
copied over, so the remote only ever holds the game, BepInEx and that file.

```sh
./deploy.sh root@your-host        # build, upload, restart, print the startup check
./deploy.sh --no-build root@host  # ship what is already built
./deploy.sh --logs root@host      # follow the remote journal
```

It uploads the BepInEx pack on the first run, then the DLL and the two generated indexes
every time. `HallPatton.notes.md` is copied once and never overwritten after that: it is
the operator's file.

### Standing one up from scratch

1. A 4 GB / 2 vCPU box. The server sits at **1.4 GB resident with nobody connected**, so
   2 GB is not enough. Valheim's simulation is largely single-threaded, so clock speed
   matters more than core count.
2. `steamcmd +login anonymous +force_install_dir /opt/valheim +app_update 896660 validate
   +quit`, **run twice**. On a fresh box the first run self-updates and restarts, loses the
   queued arguments, and fails with `Failed to install app '896660' (Missing
   configuration)` - which looks like a permissions or licence problem and is neither. The
   second run works.
3. Packages: `lib32gcc-s1` for steamcmd, plus `libatomic1` and `libpulse0` for the server
   itself, which links pulse even though it is headless.
4. Open **UDP 2456 and 2457**. 2456 is the game, 2457 is the Steam query port, and 2456 is
   bound about a minute after startup, when the log says `Opened Steam server` - not when
   it says `Game server connected`. Do not go looking for a bug in between.
5. A systemd unit with `Restart=always`, running as a non-root user, with the doorstop
   variables set as absolute paths (see below) and the name, world and password in an
   `EnvironmentFile`.

The doorstop variables come from the BepInEx pack's own `start_server_bepinex.sh`, but
systemd does not run that through a shell sitting in the game directory, so they have to be
absolute:

```ini
Environment=DOORSTOP_ENABLED=1
Environment=DOORSTOP_TARGET_ASSEMBLY=/opt/valheim/BepInEx/core/BepInEx.Preloader.dll
Environment=LD_PRELOAD=/opt/valheim/doorstop_libs/libdoorstop_x64.so
Environment=LD_LIBRARY_PATH=/opt/valheim/doorstop_libs:/opt/valheim/linux64
```

Give it `KillSignal=SIGINT` and a generous `TimeoutStopSec`: the world is written on
shutdown, and a SIGKILL half way through that is how saves get lost.

### DNS

Valheim's **Join IP** box takes a hostname, so `valheim.example.com:2456` works. One A
record pointing at the droplet is all it needs.

If the domain is on Cloudflare the record must be **DNS-only - the grey cloud, not the
orange one**. Cloudflare's proxy handles HTTP and HTTPS; a proxied record hands out
Cloudflare's addresses and the UDP game traffic has nowhere to go.

### Before it faces the internet

- **Keep the model API key out of the repo and off the DLL.** Put it in the unit’s
  `EnvironmentFile` as `MODEL_API_KEY=`, or in `BepInEx/config` on the host — never in a
  tracked file.
- **Set the limits.** See the `Limits` section of the config: a per-player cooldown, an
  hourly cap and a daily budget for the whole server. Without them one person holding down
  Enter is a billing event.
- **Back up the save directory.** It is about a megabyte; a nightly `tar` to object storage
  or a provider snapshot is enough.

## Building

Everything is pinned in the flake — no global SDK install needed.

```sh
nix develop      # dotnet 8, ilspycmd, mono, binutils, file, unzip, jq, curl
./build.sh       # builds and copies the DLL into BepInEx/plugins/HallPatton
./test.sh        # command parsing, keyword matching, answer shaping
```

`build.sh` and `test.sh` re-enter the devshell by themselves if you run them from outside.

The devshell exports `VALHEIM_DIR` and `VALHEIM_MANAGED`, which the `.csproj` uses to find
the game and BepInEx assemblies. It builds against a normal game install and deploys into
it; point it at the server install to deploy there instead:

```sh
dotnet build src/HallPatton/HallPatton.csproj -c Release \
  -p:ValheimDir=/path/to/server -p:ValheimManaged=/path/to/server/valheim_server_Data/Managed
```

`ilspycmd` is in the devshell because every game API this plugin touches was read out of
the decompiled `assembly_valheim.dll` first. If you change anything that talks to the
game, do the same — most of the design below exists because of a detail that is only
visible in that source.

## Configuration

Written on first run to `BepInEx/config/com.matthew.hallpatton.cfg`.

**General**

| Key | Default | Meaning |
| --- | --- | --- |
| `CommandWord` | `!mark` | What players type to summon him. No leading `/` |
| `DisplayName` | `Mark Hall-Patton` | Name on his chat lines, over his head, and in the player list |
| `Follow` | `true` | He walks after whoever summoned him |
| `FollowDistance` | `3` | How far behind he trails, in metres |
| `WalkSpeed` | `3.2` | Metres per second along your own path |
| `LeashDistance` | `40` | Further behind than this and he simply turns up beside you |
| `IdleDismissMinutes` | `0` | Send him home after this long unspoken to. `0` = never |

**Diagnostics**

| Key | Default | Meaning |
| --- | --- | --- |
| `Verbose` | `true` | Log every overheard chat message and what was decided about it, every reply and who it went to, and where he is every few seconds. On by default because the vanilla paths this plugin feeds fail silently |

**Talking**

| Key | Default | Meaning |
| --- | --- | --- |
| `Earshot` | `15` | How close you must be for ordinary chat to count as a question |
| `ThinkSeconds` | `2.5` | He says nothing at all for this long after a question, however fast the answer comes back |
| `Acknowledge` | `true` | If the answer is still not ready when `ThinkSeconds` is up, he says "let me think" so you know he heard you. A quick answer is never announced this way |
| `HistoryTurns` | `8` | Remembered exchanges per player; `0` disables memory |
| `MaxWords` | `90` | Answer length ceiling, as asked of the model |
| `LineLength` | `160` | Answers are broken into chat lines this long |
| `MaxLines` | `5` | Most lines one answer may use |
| `LineGapSeconds` | `2.5` | Pause between those lines |

**Limits** - every question is a paid API call, and chat has no natural brake.

| Key | Default | Meaning |
| --- | --- | --- |
| `CooldownSeconds` | `8` | Shortest gap between one player's questions |
| `PerPlayerHourly` | `40` | Most questions one player may ask in an hour; `0` removes the limit |
| `GlobalDaily` | `600` | Most model calls per day across everyone. Past it he still answers, from the built-in lines, so the server degrades rather than going quiet; `0` removes the limit |

**Knowledge** — see [What he knows about the game](#what-he-knows-about-the-game).

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Look things up in the game's own data before answering |
| `MaxEntries` | `6` | How many matching entries ride along with a question |
| `DumpIndex` | `true` | Write everything he can look up to `HallPatton.knowledge.txt` on startup |

**Look** — vanilla prefab names, so no assets ship with this plugin.

| Key | Default | Meaning |
| --- | --- | --- |
| `Model` | `0` | `0` or `1`, the two vanilla body models |
| `SkinColor` | `1.0, 0.88, 0.78` | R, G, B |
| `HairColor` | `0.88, 0.88, 0.86` | R, G, B — near-white |
| `Hair` | `Hair5` | `Hair1`–`Hair38`, or `HairNone`. `Hair5` is the one the character creator calls "Short" |
| `Beard` | `Beard3` | `Beard1`–`Beard26`, or `BeardNone`. `Beard3` is "Short" |
| `Helmet` | `HelmetStrawHat` | The closest thing the game has to his hat |
| `Chest` | `ArmorLeatherChest` | |
| `Legs` | `ArmorLeatherLegs` | |
| `Shoulder` | *(blank)* | Cape slot, e.g. `CapeLinen` |
| `RightHand` / `LeftHand` | *(blank)* | A museum administrator carries no weapon |

Edits to the `Look` section are picked up within five seconds, on a running server, with
nobody reconnecting — useful for settling on a face.

## Generated answers (Meta Muse)

**On by default.** What you say near him goes to Meta's Model API and he answers in his
own words. Greetings, the help text and the "I'd have to check that" lines are local, so
a man standing in your base all evening costs nothing.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Use Muse for answers |
| `Endpoint` | *(blank)* | Blank uses Meta's; any OpenAI-compatible chat-completions URL works |
| `ApiKey` | *(blank)* | Blank falls back to `MODEL_API_KEY`. With neither set, Muse is off and the built-in lines answer |
| `Model` | `muse-spark-1.1` | See the latency note below before changing this |
| `ReasoningEffort` | `low` | **Leave this alone.** See below |
| `ExtraPersona` | *(blank)* | Appended to the personality prompt |
| `MaxTokens` | `1000` | Budget for reasoning *plus* answer — not an answer length limit |
| `Temperature` | `0.7` | Lower than you would give a joke character: he is answering history questions |
| `TimeoutSeconds` | `20` | Give up and use a built-in line after this |

### What he knows about the game

He can answer Valheim questions - recipes, creature health, drops, build costs, which biome
a thing is found in - and the answers are right, because they are not coming from the model.

Ask a model how much iron a longship takes and it will tell you, confidently and wrongly:
item numbers are exactly the kind of fact that does not survive training. So the plugin
reads the game instead. On startup it walks `ObjectDB.instance.m_items` and `m_recipes` for
every item and every recipe, `ZNetScene.m_prefabs` for every creature (`Character`,
`CharacterDrop`), and `ObjectDB.GetAllBuildPieces()` for everything you can actually
construct - **about 1,900 entries on a stock 1.0.16 server**. A question is matched against
that index and the matching entries travel with it as reference records the model is told to
prefer over its own memory.

```
Iron Sword, a one-handed weapon; damage slash 55; durability 200; weight 0.8; upgrades to
  level 4; crafted at Forge level 2 from Wood x2, Iron x20, Leather Scraps x3. Its own
  description reads: "The straight line between life and death runs along the edge of this
  blade."
Troll, creature; health 600; hostile; drops Coins 20-30, Troll Trophy (50% chance),
  Troll Hide 5-5
Portal, a building piece, built from Greydwarf Eye x10, Finewood x20, Surtling Core x2;
  needs Workbench nearby
```

**Nothing internal ever reaches an answer.** The records carry only what a player sees on a
screen: no prefab names, no enum values. Internally `SwordIron` is still what the index
matches your question against - it is a good match for "iron sword" once both are reduced to
words - but it is not in the text the model is given, item kinds are spelled in English
rather than as `TwoHandedWeaponLeft`, and `ForestMonsters` is rendered as "hostile". The
prompt says it too: never use a prefab name, an enum or any code-like token, because an
answer with a file name in it is an answer nobody asked for.

The whole index is written to `BepInEx/config/HallPatton.knowledge.txt` on every start, so
you can read exactly what he has to hand, and `!mark lookup <thing>` shows you in chat what
a given question matches. Nothing matching is a real answer too: he is told to fall back on
what he actually knows and to say when he is unsure, rather than fill the gap.

Three optional files sit next to it in `BepInEx/config`:

- **`HallPatton.localization.tsv`** - the display names, so he says "Iron Sword" rather than
  falling back on a prefab name, and can read an item's own description off it like a museum
  label. `./tools/extract-localization.sh` generates it. Worth doing: `Localization` is
  client-only and missing from the dedicated server entirely, so without this file the index
  has only prefab names to work from.
- **`HallPatton.wiki.tsv`** - see below.
- **`HallPatton.notes.md`** - anything *you* want him to know that no file holds: how your
  server does things, where your base is, house rules. One fact per line, searched alongside
  everything else, never written to by the plugin.

#### The wiki index

The game files say what an item costs but not where anything *is*, and "which biome is this
in" is most of what players actually ask. `./tools/fetch-wiki.sh` fills that in from the
Valheim wiki:

```sh
./tools/fetch-wiki.sh              # ~1,000 pages, one request each, a few minutes
MAX=20 ./tools/fetch-wiki.sh       # just the first 20, to see the shape
```

It reads a whitelist of infobox **fields** - location, biome, type, tameable, faction,
summoning items - and nothing else, giving one short factual line per page:

```
Abomination: faction Undead; found in Swamp
Abandoned Hut: type point of interest; found in Swamp
```

No article prose is copied, because none is needed: the numbers come from the game and the
model writes its own sentences. Every line is tagged `[community wiki]` in the index, ranked
below the game data, and the prompt tells him that tagged records are player-written and may
be out of date, while the untagged ones come from the world itself and win on any number.

The wiki is community-written and licensed CC BY-SA 4.0. The script records the source and
the licence in the file's own header. That file is generated on your machine for your server
- it is not part of this plugin and is not shipped with it - and the plugin makes no network
request of its own; only the script does, one page at a time with a pause between.

Why a lookup rather than giving the model a tool to call: a tool call costs a second round
trip to the API before he can even start answering, and this is a conversation in a chat
window with a two-second budget. Retrieval before the request costs nothing and cannot fail
halfway.

### What the prompt actually asks for

`Persona.cs` carries his real public biography — the museums, the aviation museum inside
McCarran he came to Nevada for in 1993, UC Irvine and Delaware, Henderson, the badges and
fraternal swords, the E Clampus Vitus motto — because a history guide that invents its
history is worse than no guide. On top of that it is told, in order of how much it
matters:

- **Admit ignorance.** "I would want to check that" rather than a manufactured date. A
  guess presented as a record is the one failure mode worth designing against.
- **Be honest about being a game character.** He is a stand-in for a real, living person.
  Asked whether he is really him, he says so and carries on, and he does not hand out
  opinions the real man has not given.
- **Answer the question.** He is standing in a Norse afterlife and may remark on it, but
  a question gets an answer, not a travelogue.

### Two traps worth knowing about

Both of these were found by measurement, not from the docs.

**1. Muse Spark is a reasoning model, and with no `reasoning_effort` it will spend your
entire `max_tokens` budget thinking and return nothing.** Not truncated output — `content`
comes back literally `null` with `finish_reason: length`. It scales its reasoning to fill
whatever budget you hand it:

| `max_tokens` | reasoning tokens used | reply |
| --- | --- | --- |
| 60 | 57 | `null` |
| 200 | 197 | `null` |
| 600 | 597 | `null` |
| 600 + `reasoning_effort: low` | 523 | *(in-character reply)* |

So `reasoning_effort: low` is mandatory, and `MaxTokens` needs headroom for the hidden
reasoning. It is **not** how you keep answers short — `Talking.MaxWords` does that. `none`
is rejected by the API; `minimal` and `medium` both think *more* than `low`, not less.
`MuseClient` detects this exact failure and says so in the log rather than reporting a
vague "empty reply".

**2. `muse-spark-1.3` is too slow to talk to.** Same prompt, same settings:

| Model | Latency (measured) |
| --- | --- |
| `muse-spark-1.1` | 1.7 – 4.5s |
| `muse-spark-1.3` | 10.6 – 16.8s |

`tools/muse-smoketest.sh` is the harness those numbers came from; it reads the key from
`MODEL_API_KEY` and reports latency, token usage and the answer.

### About the API key

You supply your own. Resolution order is the config file’s `ApiKey` → `MODEL_API_KEY`;
nothing is compiled into the DLL, so a build is safe to pass around. With neither set,
Muse stays off and the built-in lines answer. The key is never logged: failures report the
status code, not the response body.

How the request behaves:

- **Never blocks the server.** `UnityWebRequest` inside a coroutine. A stalled request
  cannot hold up the world.
- **Always falls back.** No key, timeout, HTTP error, unparseable body, empty answer — any
  of them and you get a built-in `Dialogue` line. Talking never dead-ends.
- **One request at a time per player**, so somebody hammering Enter cannot fan out into a
  billing event. They get told to wait, once every ten seconds at most.
- **Remembers per player.** The last `HistoryTurns` exchanges with *you* go along, so he
  can follow your conversation without inheriting someone else's.
- **Knows where it is, and what things cost.** Each request carries a short note - who is
  asking, the biome, day or night - plus any reference records the question matched, which
  the model is told to use but never read aloud. Deliberately no weather: see the note on
  `EnvMan` in [docs/valheim-server-plugins.md](docs/valheim-server-plugins.md).
- **Pauses before answering.** `Talking.ThinkSeconds` is a floor on how fast he can speak,
  so an answer that came back in 1.2s still lands like somebody who thought about it, and
  the "let me think" line is only used when the model is genuinely still working.
- **No new assemblies.** `UnityWebRequest` and `Newtonsoft.Json` already ship with
  Valheim, so the plugin folder stays a single DLL. (The official OpenAI/Anthropic-style
  .NET SDKs would each drag a `System.Text.Json` dependency chain into Unity Mono that
  conflicts with the `System.Memory` the game already pins — hence raw HTTP.)

Endpoint used: `POST https://api.meta.ai/v1/chat/completions`, `Authorization: Bearer
<key>`, OpenAI-compatible `messages` array. See <https://dev.meta.ai/docs/>.

## How it works

If you are here to write something similar rather than to run this,
[docs/valheim-server-plugins.md](docs/valheim-server-plugins.md) is the reference: what a
server-only plugin can and cannot reach, the three gates that silently eat a chat message,
how to build a networked object with no prefab instance, and the API surface with line
refs into the decompiled 1.0.16 assembly.

`src/HallPatton/`

- **`Plugin.cs`** — BepInEx entry point, config, Harmony bootstrap, the per-frame tick.
- **`Historian.cs`** — Mark himself: one ZDO the server owns, and the walking.
- **`Identity.cs`** — the player-list entry and the identity his chat lines carry.
- **`Look.cs`** — his face and clothes, written as ZDO fields.
- **`ServerChat.cs`** — speaking, from a server that has no chat of its own.
- **`Conversation.cs`** — what to do with an overheard message; per-player threads.
- **`Patches.cs`** — the four Harmony patches.
- **`Knowledge.cs`** — what he can look up, read out of the running game.
- **`Persona.cs`** — who he is, as a system prompt. Unity-free.
- **`Diagnostics.cs`** — the startup check, the verbose log and `!mark debug`.
- **`MuseClient.cs`** — the model call.
- **`deploy.sh`** - builds here, ships the DLL to a remote server, restarts it.
- **`Command.cs`**, **`Matcher.cs`**, **`ReplyText.cs`**, **`Dialogue.cs`** — the
  Unity-free text layer, which is what `./test.sh` exercises.

### Why he is a ZDO and not a GameObject

The obvious design — spawn a prefab, hang a `MonoBehaviour` on it, drive it from `Update`
— cannot work on a server. `ZNetScene.CreateObjectsSorted` only instantiates ZDOs near
`ZNet.GetReferencePosition()`, and every caller of `SetReferencePosition` is a *player*:
on a dedicated server it stays at the world origin. That is why Valheim creatures are
simulated by the nearest client rather than by the server. Out where the players actually
are, the server holds ZDOs and no GameObjects at all.

So Mark **is** a ZDO. The plugin creates one for the vanilla `Player` prefab and writes
its fields directly:

| Written | Read back by | Giving |
| --- | --- | --- |
| position, rotation | `ZSyncTransform.ClientSync` | a body that moves, interpolated smoothly |
| `438569 + Animator.StringToHash("forward_speed")` | `ZSyncAnimation.SyncParameters` | legs that walk instead of sliding |
| `HairItem`, `BeardItem`, `HelmetItem`, `ChestItem`, `LegItem`, colours, `ModelIndex` | `VisEquipment`'s non-owner path | the hat, the beard, the clothes |
| `playerName` | `Player.GetHoverName` | his name over his head |

Every one of those is a field a client already reads off *other players*, so each client
builds and renders him with stock code. That is the whole reason no client install is
needed. The `Player` prefab specifically, because it is the only humanoid a vanilla client
can already build that has a configurable face, hair, beard and clothes and takes its
displayed name from the ZDO rather than from the prefab.

The ZDO is created non-persistent, so he is never written into the world save — a restart
should not leave him standing in an empty world waiting for a question. Non-persistent
ZDOs are reaped when their owner disconnects, and `ZDOMan.IsPeerConnected` counts the
server's own session as connected, so his lifetime is exactly the server's.

### Hearing

This is the part that looks like a trick and is actually load-bearing.

A client sends what you type only to the players **the server told it about**:
`Chat.CheckPermissionsAndSendChatMessageRPCsAsync` walks `ZNet.GetPlayerList()` and
addresses one copy of the message to each. With one player online and nothing else in that
list, chat never leaves their machine. A server-side plugin cannot overhear a thing.

So `Identity.PlayerEntry()` appends an entry for Mark to the list the server broadcasts,
carrying a character ID that belongs to the server. Clients now address a copy to *us*,
and `ZRoutedRpc.HandleRoutedRPC` — patched — reads it:

- **normal chat and whispers** arrive as a `Say` RPC on the speaker's own character, so
  `data.m_targetZDO` says exactly where they are standing;
- **shouts** arrive as a `ChatMessage` routed RPC carrying a position.

The same entry is what makes his name work. `Chat.AddString` resolves the name on a chat
line by looking the sender's platform ID up in the player list and **drops the line
entirely** if it is not there.

He is in that list whether or not he is currently out, because he has to be in it to hear
the summon in the first place. The costs of that are in [Known wrinkles](#known-wrinkles).

### Speaking

`ServerChat` invokes the vanilla `ChatMessage` routed RPC once per recipient, under Mark's
identity. On each client that lands in `Chat.RPC_ChatMessage` exactly as another player's
message would: a line in the chat log and floating text in the world. Distance is judged
server-side, because `ChatMessage` is what vanilla uses for shouts and clients do not
range-check it.

The identity's platform ID is deliberately **not** `Steam`. A receiving client runs the
sender through `RelationsManager.CheckPermissionAsync`, which asks its own platform for a
user profile; an ID from another platform is refused as `DifferentPlatformsNotAvailable`,
which that check treats as *granted*, and the message goes through. A made-up ID on the
client's own platform would instead be looked up for real and could come back
`InvalidID` — which is silently dropped. Hence `Museum_1830`.

Answers are split at sentence ends into `LineLength` chunks and sent a couple of seconds
apart, because the floating text clips long strings — and because it reads like someone
talking rather than pasting.

### Walking

He does not pathfind, and the server has no colliders out there to pathfind against. He
follows a **breadcrumb trail**: the host's position is sampled every 0.25s, and he walks
from crumb to crumb, taking his height from the ground the player was standing on. He
therefore goes around the rock the player went around, up the stairs the player went up,
and through the door the player used, without the server knowing any of those things
exist. Sprint away, take a boat or use a portal and the leash gives up on the trail and
simply puts him beside you.

### The patches

| Patch | Why |
| --- | --- |
| `ZRoutedRpc.HandleRoutedRPC` (**prefix**, non-skipping) | Overhears chat addressed to the server. A dedicated server registers no handler for `Say` or `ChatMessage`, so the copy would otherwise be dropped. Never skips the original: on a listen server the host's own `Chat` still has to handle its own messages |
| `ZNet.UpdatePlayerList` (postfix) | Adds his entry. A postfix because that method clears and rebuilds the list from the connected peers each time it runs |
| `ZNet.UpdatePlayerHistory` (prefix + postfix) | Lifts him out of the list for the duration of that call. It copies the live player list into `World.m_playerHistory`, which **is** written to the save file, so without this the world remembers him as someone who once visited |
| `ZNet.GetNrOfPlayers` (postfix) | Takes him back out of the count. That count is what makes a server full at ten and what keeps the world clock running, and he has no business deciding either |
| `Player.FixedUpdate` / `Update` / `LateUpdate` (**prefixes**) | Only ever fire for him, and only on the machine that owns him |

That last one is the one non-obvious case. Normally the server has no GameObject for him
at all — but a dedicated server *does* instantiate whatever is near the world origin, so
summon him close to spawn and the server builds him too, and then owns a `Player`. The
game does not expect that: `Player.FixedUpdate` destroys any `Player` it owns that is not
the local player (he would vanish on the frame he appeared), `Player.Update` would read
the keyboard, and `Player.LateUpdate` would drag the server's reference position across
the map. Clients are unaffected either way — they do not own him, so those branches never
run and the patch never fires.

He is set to 500 health and topped back up if it drops, for the same near-origin case: a
`Player` object the server owns could otherwise be killed by a passing greydwarf and drop
a tombstone.

## Known wrinkles

All of these follow from being in the player list, or from having no body on the server.
None of them needs a client-side fix, which is the trade this plugin is making.

- **He is listed as a player.** He shows in the in-game player panel, out or not. The
  server-side count is corrected (see the patch table), so he does not eat a connection
  slot and does not keep the world clock running in an empty world.
- **Clients count him for `m_onePerPlayer` drops.** Each client has its own copy of the
  player list and computes boss drops from it, so a boss kill yields one extra trophy. A
  server-side patch cannot reach that, and it is the only gameplay effect found.
- **Monsters can see him, and he cannot be hurt.** Nothing simulates him, so damage RPCs
  land on a server with no object to apply them to and are discarded. A greydwarf may end
  up beating on him indefinitely, which is free aggro for you.
- **No map pin**, deliberately - a pin nobody can follow is noise.
- **He walks your path, he does not pathfind.** He cannot swim, and he will not find his
  own way round anything you did not walk round yourself. That is what `LeashDistance` is
  for: past it he stops trying and simply turns up beside you.
- **He goes home on a restart.** His object is never written to the world save.

## Verified so far

- Compiles clean against the real 1.0.16 assemblies, no warnings.
- `./test.sh` covers command parsing, the keyword matcher, answer sanitising, line
  splitting, the persona prompt and the built-in answers: 56 cases, all passing.
- Every game API and every field name used here was read out of the decompiled 1.0.16
  assembly, including the ones that shape the design: `ZNetScene.CreateObjectsSorted`,
  `ZSyncAnimation`'s `438569` key offset, `Chat.AddString`'s player-list lookup,
  `RelationsManager.CheckPermissionAsync`'s platform behaviour, `Player.FixedUpdate`'s
  self-destruct, `ZDOMan.RemoveOrphanNonPersistentZDOS`, and `SetupAwake`'s `wakeup` flag
  defaulting to true (which is why that flag is written — otherwise every client plays a
  second of him getting up off the floor).
- Prefab names (`HelmetStrawHat`, `Beard3`, `Hair5`, the armour and cape sets) were taken
  from the game's own asset manifest, not guessed.
- The latency and reasoning-budget tables above are measured, but they were measured
  against the previous build's much shorter prompt. `tools/muse-smoketest.sh` now carries
  a Mark-shaped request instead; it has not been re-run, and the longer persona will push
  prompt tokens up, so re-measure before trusting `MaxTokens` on a busy server.
- **It runs on a real dedicated server.** Valheim 1.0.16, network version 40,
  `isModded: True`, `Chainloader startup complete (1 loaded, 0 skipped, 0 failed)`,
  through to `Game server connected` and a registered lobby on UDP 2456. The APIs were
  re-checked against the *server* build of `assembly_valheim.dll`, which is a different
  and smaller assembly than the client one - 632 types against the client's 2,500 - and
  all of them are present there.
- **All seven Harmony patches resolve at runtime**, reported by the startup check itself:
  `Player.FixedUpdate`, `Player.LateUpdate`, `Player.Update`, `ZNet.GetNrOfPlayers`,
  `ZNet.UpdatePlayerHistory`, `ZNet.UpdatePlayerList`, `ZRoutedRpc.HandleRoutedRPC`.
  `Harmony.PatchAll` throws on a name it cannot find, so this is proof and not a guess.
- **The `Player` prefab is in the server's own `ZNetScene`** - the one hard dependency,
  checked and logged on startup rather than assumed.
- Two things the first server run corrected, both of which had been guesses: a dedicated
  server *does* have a `Chat` instance (so "is there a local player" is the right test for
  a listen server, not "is there a chat"), and `ZNet.UpdatePlayerHistory` was copying his
  player-list entry into the world's saved history until a patch started lifting him out
  of the list for that call.
- **The knowledge index builds on a live server**: 1,939 entries from a stock 1.0.16
  dedicated server (1,519 items, 161 creatures, 538 building pieces) against all 5,593
  English localization strings, with correct recipes spot-checked against the game
  (`Iron Sword ... crafted at Forge level 2 from Wood x2, Iron x20, Leather Scraps x3`).
  Four data bugs found by reading the generated dump rather than trusting the code:
  `SharedData` carries default armour, block and durability values on *everything*, so a
  log of wood claimed 10 armour; some item prefabs also carry a `Piece` and turned up a
  second time as absurd build costs; `m_upgraderResource` ingredients belong to upgrades,
  not to the base recipe; and variants like `Troll_sleeping` duplicated their originals.
- **Still not exercised: a client actually connecting.** Nobody has typed `!mark` yet, so
  the summon, the walking, the animation sync and the chat round-trip are verified by
  construction and by the server-side startup check only. The riskiest single assumption
  is that clients accept a chat line from a sender whose platform they do not recognise;
  the reasoning for it is in [Speaking](#speaking), and if it turns out to be wrong the
  symptom is floating text over his head with no matching line in the chat log. `!mark
  debug` is there to tell those two cases apart.

## Uninstalling

```sh
rm -rf "$SERVER_DIR/BepInEx/plugins/HallPatton"
```

He leaves nothing behind in the world save — the ZDO was never persistent — and players
have nothing to uninstall.

## A note on the real Mark Hall-Patton

This is an unaffiliated fan homage, built from his own published interviews. It is not
endorsed by him, by the Clark County Museum System or by Clark County, and the character
is told to say so when asked. The museum is real, though: 1830 South Boulder Highway,
Henderson, open daily.
