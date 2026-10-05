# Why portal arrivals take ten seconds, and the three constants that cause it

Design notes and findings for the `Firehose` plugin (`src/Firehose`). Everything below was
read out of the decompiled **Valheim 1.0.16** dedicated-server assembly, the same way
[the plugin field notes](valheim-server-plugins.md) were; line numbers are `ilspycmd` output
and will drift, but the class and method names are what to grep for.

## The symptom, measured

Profiling a 4-player DigitalOcean server with a 987,000-object world, two portal trips:

| Trip | Data sent | Time | Rate |
| --- | --- | --- | --- |
| 20:43:28–20:43:37 | 589 KB | 9 s | ~64 KB/s |
| 20:45:30–20:45:36 | 476 KB | 7 s | ~64 KB/s |

A busy destination is 0.5–0.6 MB of world data, and the server shipped it at a **flat
~64 KB/s** both times. Ruled out by the same profiling: CPU (busiest core never above 17%),
memory (1.4 GB free, swap unused), the uplink (135 KB/s total for 4 players), and the
`HallPatton` plugin. The flatness is the tell — a resource ceiling is noisy, a constant is
not.

## Verdict: three constants, stacked

### 1. A 10 KB send window that counts bytes already on the wire

`ZDOMan.SendZDOs` (`ZDOMan.cs:1057`):

```csharp
private bool SendZDOs(ZDOPeer peer, bool flush)
{
    int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
    if (!flush && sendQueueSize > 10240) return false;
    int num = 10240 - sendQueueSize;
    if (num < 2048) return false;
    ...fills a package with up to num bytes of ZDOs...
}
```

The interesting half is what `GetSendQueueSize` returns. For the Steam backend
(`ZSteamSocket.cs:347`):

```csharp
num += pStatus.m_cbPendingReliable + pStatus.m_cbPendingUnreliable + pStatus.m_cbSentUnackedReliable;
```

`m_cbSentUnackedReliable` is bytes **already sent and awaiting acknowledgement**. So 10240
is not a queue limit, it is a *send window* in the TCP sense, and it is consumed by the
round trip. Throughput is therefore `window / RTT` no matter what the link can do:

| Ping | Ceiling from the window alone |
| --- | --- |
| 60 ms | 170 KB/s |
| 100 ms | 102 KB/s |
| 160 ms | **64 KB/s** |
| 250 ms | 41 KB/s |

The measured 64 KB/s puts that server at about a 160 ms round trip, and the flat line is
simply a window with no scaling. This is the dominant term.

Worth noting: under crossplay the sockets are `ZPlayFabSocket`, whose `GetSendQueueSize`
(`ZPlayFabSocket.cs:737`) returns `m_inFlightQueue.Bytes * 0.25f` — a quarter of in-flight
bytes, i.e. an effective window four times larger. Steam players get the worse deal.

### 2. The window is refreshed one peer per frame

`ZDOMan.SendZDOToPeers2` (`ZDOMan.cs:886`) waits 50 ms, then services exactly one peer per
frame:

```csharp
m_sendTimer += dt;
if (m_nextSendPeer < 0) { if (m_sendTimer > 0.05f) { m_nextSendPeer = 0; m_sendTimer = 0f; } return; }
if (m_nextSendPeer < m_peers.Count) SendZDOs(m_peers[m_nextSendPeer], flush: false);
m_nextSendPeer++;
if (m_nextSendPeer >= m_peers.Count) m_nextSendPeer = -1;
```

A round costs `50 ms + N frames`, so the real ceiling is `window / max(RTT, round period)`:

| Players | Round period @30fps | Ceiling at 10 KB |
| --- | --- | --- |
| 4 | ~180 ms | 57 KB/s |
| 10 | ~380 ms | 27 KB/s |
| 20 | ~720 ms | 14 KB/s |

At 4 players the round period and the round trip are about equal, so this is roughly a
co-equal cause; past 10 players it becomes the dominant one. Nothing justifies spreading
the calls over frames — `SendZDOs` already bounds itself per peer by checking the socket and
returning early, so servicing a peer with nothing owed costs a sync-list build and no bytes.

### 3. Steam's own send rate is pinned shut

`ZSteamSocket.RegisterGlobalCallbacks` (`ZSteamSocket.cs:72`) sets **both** ends of Steam's
rate estimate from one pinned integer:

```csharp
GCHandle gCHandle3 = GCHandle.Alloc(153600, GCHandleType.Pinned);
SetConfigValue(k_ESteamNetworkingConfig_SendRateMin, ... gCHandle3 ...);
SetConfigValue(k_ESteamNetworkingConfig_SendRateMax, ... gCHandle3 ...);
```

Min == max == 150 KB/s means Steam's bandwidth estimation has nothing to estimate, and no
window change can get a player above 150 KB/s. This is the ceiling that would bind *after*
fixing the first two.

## What the plugin does

One switch per ceiling, so each can be measured on the same build.

| Setting | Default | Vanilla | Ceiling it lifts |
| --- | --- | --- | --- |
| `Send.WindowBytes` | 65536 | 10240 | 1 — the window itself |
| `Send.ServiceEveryPeer` | true | one peer/frame | 2 — refresh period, now constant in N |
| `Send.RoundSeconds` | 0.05 | 0.05 | 2 — how often a round happens |
| `Steam.SendRateMin` | 153600 | 153600 | 3 — floor left where vanilla had it |
| `Steam.SendRateMax` | 1048576 | 153600 | 3 — ceiling, so Steam can probe again |

Expected per player, at that server's 160 ms ping: **~400 KB/s** against vanilla's 64 KB/s,
making a 589 KB portal arrival about 1.5 s of transfer instead of 9 s. The window becomes
the binding term again at that point (`65536 / 0.16`), which is why raising
`Steam.SendRateMax` without also raising `WindowBytes` buys nothing beyond this.

### How it is implemented

- **The window** is a Harmony transpiler on `ZDOMan.SendZDOs` that rewrites both `10240`
  constants into a call to `SendWindow.Bytes()`. The instructions are edited in place rather
  than replaced, because the second constant is the branch target of both early returns
  above it and a fresh `CodeInstruction` would drop those labels. The 2048 floor is left
  alone: it only says "do not bother with a package smaller than this".
- **The scheduler** is a prefix on `SendZDOToPeers2` that services every peer on a timer and
  skips the original. Skipping leaves vanilla's `m_nextSendPeer` parked at `-1` forever;
  nothing else in 1.0.16 reads it, which was checked rather than assumed.
- **The Steam rate** is not patched. The config values are simply set again — globally,
  which every later connection inherits, and then per connection for peers already up, so
  the ordering cannot matter. Patching `RegisterGlobalCallbacks` was rejected twice over:
  it is small enough to worry about Mono inlining it into its callers, and both values share
  one pinned integer, so a transpiler could not tell min from max.

## The trade-off, stated plainly

A window is also a queue, and a queue is latency. A player with a full 64 KB window waits
`window / sendrate` for anything behind it — up to ~430 ms at Steam's 150 KB/s. That only
happens while that much data is genuinely backed up for them, which is the loading screen,
not normal play; but a player sprinting into dense unexplored terrain can hit it too, and
their own position updates ride the same reliable channel. 64 KB is the conservative
default for that reason. `ZPlayFabSocket` tolerating four times vanilla's window is some
evidence the game is relaxed about it.

Shortening `Send.RoundSeconds` is the latency-free way to buy throughput — the ceiling is
`window / max(RTT, round)`, so a shorter round lets a smaller window reach the same rate —
but it costs a sync-list build and sort per player per round, and `CreateSyncList`
(`ZDOMan.cs:1261`) sorts every candidate ZDO in the peer's simulation distance by
distance-and-staleness. That sort is the CPU this plugin can spend, and 17% of one core was
not using it.

Raising `Steam.SendRateMax` is the one change outside the game's own design. It is still
strictly more adaptive than vanilla: a fixed rate cannot back off on a bad link and a range
can. The player has to be able to receive it, though — 1 MB/s asks for 8 Mbit/s down during
a burst.

## How to tell whether it worked

### Measuring it with no client: the self-test

`Diagnostics.SelfTest` (or `FIREHOSE_SELFTEST=1` in the environment) answers the question the
startup check cannot — whether a wider window actually puts more bytes on the wire — without
waiting for a player to walk through a portal.

`ISocket` is a public interface and `ZDOMan.AddPeer` takes any `ZNetPeer`, so the smallest
thing the send path cannot tell from a player is a socket that swallows what it is handed and
reports whatever queue depth we choose. Fill a patch of world with throwaway objects, register
that peer, and one send round can be measured three ways. From the local test server, 4000
objects at the default 64 KB window:

```
--- Firehose: self-test ---
  objects       : 4000 throwaway, non-persistent, within 30 m of the fake peer
  window 10240, queue empty  :   10.1 KB sent   (vanilla's budget)
  window 65536, queue empty :   64.0 KB sent   (6.3x vanilla's)
  window 65536, queue 20480  :   44.1 KB sent   (vanilla sends nothing at all with this much outstanding)
  verdict       : budget constant live, gate constant live
firehose self-test: all 4000 throwaway objects are gone from the world
```

Each round runs against a peer that has never been sent anything, so all three start from the
same state. The three cases are chosen to separate the two patched constants:

- **10240 / empty** is the baseline, and 10.1 KB confirms the harness measures what vanilla
  would have done.
- **65536 / empty** is the budget constant — `num = window - outstanding` — and 6.3× is it
  working.
- **65536 / queue 20480** is the gate constant — `if (!flush && outstanding > window) return
  false`. Vanilla gives up at 10240, so its answer here is zero bytes; 44.1 KB is 65536 −
  20480 to within a rounding error. This is the case that matters in flight, because
  mid-transfer the queue is never empty.

It is off by default and refuses to run with anybody connected: the objects it makes have no
prefab behind them, which a real client has no business receiving. They are non-persistent, so
they cannot reach the world save, and they are destroyed afterwards — `DestroyZDO` only
*queues* the removal, so the ids are checked again a second later and the count is reported.
A warning there means restart before anybody joins.

**What it does not cover.** The scheduler (ceiling 2) is proven to bind and run, but its effect
is a round period that stops growing with the player count, and one fake peer cannot show
that. Steam's send rate (ceiling 3) was accepted by `SetConfigValue` but its effect needs a
real connection. Both of those want a client.

### In play: the burst lines

`Diagnostics.Report` (on by default) logs a line every ten seconds and a summary of every
burst, a burst being a player receiving faster than walking around requires. From the live
server, 2026-10-01 22:01 UTC — a portal arrival on the 987,000-object world, minutes after
the plugin went on:

```
firehose: 1 peer(s), window 64K, 15.0 rounds/s | <player> 12 KB/s out (peak 114, queue 0.1K)
firehose: <player> received 601 KB in 4.0s (150 KB/s average, 172 KB/s peak)
```

Against the profiled baseline of **589 KB in 9 s at a flat ~64 KB/s**, that is 601 KB in
**4.0 s** — 2.3× the throughput, on the same world and the same players. A second burst
minutes later read 309 KB in 3.0 s.

That second line is the number the whole plugin exists to move: it is the portal arrival,
timed. Throughput comes from `ISocket.GetAndResetStats`, which counts every byte handed to
that socket and which **nothing in Valheim 1.0.16 calls** — it is on the interface and
otherwise dead, so reading and resetting it takes nothing away from the game.

**Reading those numbers.** The 172 KB/s peak is the proof that ceiling 3 came off: vanilla
pins Steam to 153600 B/s and nothing can exceed it, so anything above 150 KB/s can only
happen with `SendRateMax` raised. The *average* landing on 150 KB/s is the next thing to
fix — that is exactly `SendRateMin`, which is where Steam's estimator starts before it
probes upward, and a 4-second transfer gives it very little time to climb. The window is
not the binding term at these rates: 65536 bytes over a 160 ms round trip is a ~400 KB/s
ceiling, well above what came out. So the knob worth turning next is the **floor**, not
`WindowBytes` — with the caveat that a floor is the one setting that can hurt somebody on a
weak link, because it tells Steam to push at least that fast whether or not the link can
take it.

**15.0 rounds/s, not 20.** `RoundSeconds` is 0.05, but the timer is advanced by `dt` once a
frame, so on a server running ~33 ms frames it fires every second frame: 66 ms, not 50. It
does not cost anything here, because the round period only binds when it exceeds the round
trip and 66 ms is well under 160 ms. `RoundSeconds` would have to drop to ~0.03 to actually
get 30 rounds/s.

### At startup: what bound

The startup check reports what actually bound, including one thing that cannot be known
until a player connects:

```
--- Firehose: startup check ---
  role          : server=True dedicated=True backend=Steamworks
  send window   : 65536 bytes, both constants patched (vanilla 10240)
  rounds        : every peer each 50 ms (vanilla: one peer per frame, so 50 ms + N frames)
  patched body  : verified, the patched body JITs
  steam rate    : min 153600 max 1048576 B/s (vanilla pins both at 153600)
  patched       : ZDOMan.SendZDOs, ZDOMan.SendZDOToPeers2
  ceiling       : ~400 KB/s per player at a 160 ms ping, against ~63 KB/s vanilla
```

`patched body` is the transpiler's own smoke test. Malformed IL — a dropped label, an
unbalanced stack — throws `InvalidProgramException` when Mono first JITs the method, which
without this would be the moment the first player connects and nothing arrives. Calling
`SendZDOs(null, false)` makes that happen at startup instead: vanilla dereferences the peer
in its first three instructions, before touching any state, so a `NullReferenceException`
is the result that means "well-formed".

A `transpiler found 1 of 2` or a short `patched` list is what a game update looks like.

## Known limits

- **Not the whole portal wait.** This fixes the server's half — the transfer. The client
  still has to instantiate what it received, and the profiling saw the client then take over
  the area and push ~95 KB/s back up. That upload is paced by the *client's* own copy of all
  three constants, and no server-side plugin can reach it.
- **Steam backend only** for ceiling 3. Under crossplay (`ZPlayFabSocket`) the plugin stands
  down on the rate and says so in the startup check; ceilings 1 and 2 still apply.
- **Collides with `src/Verse`,** whose spike 0 replaces the same scheduler. If both plugins
  are loaded, `Firehose` detects it, stands down on scheduling and leaves it to
  `Verse.FairSend`, which does the same thing. `deploy.sh` ships `HallPatton` and `Firehose`
  and deliberately not `Verse`.
- **Measured, 2026-10-01.** Live on the 987k-object server: 601 KB in 4.0 s at 150 KB/s
  average and 172 KB/s peak, against a profiled 589 KB in 9 s at ~64 KB/s. 2.3× throughput,
  and the peak above 150 KB/s proves the Steam rate cap came off. Ceilings 1 and 3 are
  settled; the scheduler's effect still needs more than one player on to show. The next term
  is `Steam.SendRateMin`, not the window — see the burst-line reading above.
