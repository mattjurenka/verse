// TikTok requires the OAuth redirect_uri to be on a domain we control and have verified -
// Composio's own shared callback domain doesn't satisfy that for TikTok specifically. So
// TikTok redirects here first, and this just forwards the browser on to Composio's real
// callback with the same query string (code, state, etc.) intact.
const COMPOSIO_CALLBACK = "https://backend.composio.dev/api/v3/toolkits/auth/callback"

export const onRequestGet: PagesFunction = async ({ request }) => {
  const incoming = new URL(request.url)
  const target = new URL(COMPOSIO_CALLBACK)
  target.search = incoming.search
  return Response.redirect(target.toString(), 302)
}
