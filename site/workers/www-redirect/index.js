// Routed at www.verseworlds.fun/* (see wrangler.jsonc) - the apex is the canonical host, this
// just sends www traffic there with the path and query string intact.
export default {
  async fetch(request) {
    const url = new URL(request.url)
    url.hostname = "verseworlds.fun"
    return Response.redirect(url.toString(), 301)
  },
}
