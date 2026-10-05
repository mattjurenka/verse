// GET /api/join - how to get onto the server right now.
//
// The crossplay join code is not ours to choose: PlayFab issues it when the server registers
// its session, and a restart can be handed a different one. So it must not be typed into the
// page. It is read from the admin panel on the game box, which derives it from the server's own
// journal - the only place that knows what code the running server actually got.
//
// Why a Function rather than fetching the panel from the browser: the page keeps one origin,
// the answer is cached at Cloudflare's edge instead of hitting a small droplet once per
// visitor, and the box being down becomes a stale-but-correct code instead of a blank space.
const PANEL = "https://valheim.jurenka.software/api/join"

// What to say when the panel cannot be reached. A join code is stable for as long as the
// session is, so a remembered one is very probably still right - and the honest thing is to
// serve it while saying where it came from, which is what `fresh` is for.
const LAST_KNOWN = "968470"

// Long enough that a burst of visitors is one request to the droplet, short enough that a new
// code is on the page within the minute.
const EDGE_SECONDS = 45

type Join = {
  code: string | null
  crossplay: boolean
  /** False when this is the remembered code rather than one just read off the server. */
  fresh: boolean
  address: string
}

export const onRequestGet: PagesFunction = async (context) => {
  const cache = caches.default
  const key = new Request(new URL(context.request.url).origin + "/api/join", { method: "GET" })

  const hit = await cache.match(key)
  if (hit) return hit

  let body: Join = {
    code: LAST_KNOWN,
    crossplay: true,
    fresh: false,
    address: "play.verseworlds.fun:2456",
  }

  try {
    const answer = await fetch(PANEL, {
      headers: { accept: "application/json" },
      signal: AbortSignal.timeout(4000),
    })

    if (answer.ok) {
      const live = (await answer.json()) as {
        code?: string | null
        crossplay?: boolean
      }

      // A code of null is a real answer, not a failure: it means crossplay is off, or that the
      // session registered but never activated - which is PlayFab's silent failure and exactly
      // the thing a page must not paper over with a code that will not work.
      body = { ...body, code: live.code ?? null, crossplay: live.crossplay === true, fresh: true }
    }
  } catch {
    // Left as the remembered code. A droplet that is briefly unreachable is not a reason to
    // stop telling people how to connect.
  }

  const response = Response.json(body, {
    headers: {
      "Cache-Control": `public, max-age=${EDGE_SECONDS}`,
      "Access-Control-Allow-Origin": "*",
    },
  })

  context.waitUntil(cache.put(key, response.clone()))
  return response
}
