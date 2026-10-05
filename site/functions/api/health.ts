// A Cloudflare Pages Function: anything under functions/ is deployed as its own Worker,
// routed by file path (functions/api/health.ts -> GET /api/health), alongside the static
// site. This one is just a liveness check and a starting point - a natural place to grow a
// real endpoint later (e.g. proxying the dedicated server's player count into the Connect
// section) without needing a separate Worker project.
export const onRequestGet: PagesFunction = async () => {
  return Response.json({ ok: true, service: "verseworlds.fun" })
}
