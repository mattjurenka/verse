import { useEffect, useState } from "react"

/**
 * How to get onto the server, fetched rather than written down.
 *
 * The crossplay join code is issued by PlayFab when the server registers its session, and a
 * restart can be handed a different one - so a code hard-coded into the page would go stale
 * silently, and a stale join code is a server nobody can reach. `/api/join` reads it from the
 * game box, which derives it from the server's own journal.
 *
 * The remembered code below is what renders before that answer arrives, and what stands if it
 * never does. It is the real code, so the page is useful immediately and on a bad day; when
 * the fetch lands it is replaced by whatever the server actually has. Keep it current when the
 * code changes for good - but nothing breaks if it drifts, which is the point of fetching.
 */
const LAST_KNOWN = "968470"

export const SERVER_NAME = "Verse"
export const SERVER_ADDRESS = "play.verseworlds.fun:2456"

export type Join = {
  code: string | null
  crossplay: boolean
  /** False while showing the remembered code - either still loading, or the server is unreachable. */
  fresh: boolean
}

export function useJoin(): Join {
  const [join, setJoin] = useState<Join>({ code: LAST_KNOWN, crossplay: true, fresh: false })

  useEffect(() => {
    let live = true

    fetch("/api/join", { headers: { accept: "application/json" } })
      .then((r) => (r.ok ? r.json() : null))
      .then((body: Join | null) => {
        if (!live || !body) return
        setJoin({
          code: body.code ?? null,
          crossplay: body.crossplay === true,
          fresh: body.fresh === true,
        })
      })
      .catch(() => {
        // The remembered code stays on the page. Nothing to tell the visitor: it is almost
        // certainly the right one, and "we could not check" is not useful to them.
      })

    return () => {
      live = false
    }
  }, [])

  return join
}

/** Groups a code for reading aloud and typing in: 968470 -> "968 470". */
export function spaced(code: string): string {
  return code.length === 6 ? `${code.slice(0, 3)} ${code.slice(3)}` : code
}
