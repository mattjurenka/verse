# verseworlds.fun

The homepage for the Verse Valheim server - explains what a verse is and documents the
`!verse` / `!warp` chat commands (see `../src/Verse` for the plugin itself). React + Vite +
TypeScript + Tailwind v4 + shadcn/ui, deployed to Cloudflare Pages.

## Developing

Needs Node 22 and pnpm; `nix develop .#site` from the repo root gets you both without
installing anything globally.

```sh
nix develop .#site
pnpm install
pnpm dev
```

- `pnpm build` - type-checks (`tsc -b`, including `functions/`) and builds to `dist/`.
- `pnpm preview` - serves the built `dist/` with plain Vite, no Cloudflare emulation.
- `pnpm preview:cf` - builds, then serves `dist/` through `wrangler pages dev`, so
  `functions/*` actually runs as it would on Cloudflare rather than being skipped.

## Structure

- `src/components/site/` - the page, one file per section (`Hero`, `Concept`, `Commands`,
  `Connect`, `Faq`, nav, footer). `src/components/ui/` is shadcn/ui's own, generated with
  `pnpm dlx shadcn@latest add <component>` - don't hand-edit it, regenerate it instead.
- `functions/` - Cloudflare Pages Functions. Each file is its own Worker route
  (`functions/api/health.ts` -> `GET /api/health`); this is the "worker access" Pages gives
  you alongside the static build, for whenever the site needs server-side logic instead of a
  separate Worker project.

## The join code is fetched, not written down

The crossplay join code shown in the header, the hero, the Connect section and the footer is
**not a constant in the page**. PlayFab issues it when the server registers its session and a
restart can be handed a different one, so a code typed in here would go stale silently — and a
stale join code is a server nobody can reach.

```
the game's journal  ->  valpanel /api/join  ->  this site's /api/join  ->  useJoin()
  (the only place        (../valpanel, on       (functions/api/join.ts,   (src/lib/join.ts)
   that knows)            the game box)          cached at the edge)
```

Each step only passes on what the step before it knows, so there is nothing to keep in sync.
`src/lib/join.ts` holds a remembered code as the first paint and the last resort — update it
when the code changes for good, but nothing breaks if it drifts, which is the point. When
crossplay is off the chain reports no code at all and every component falls back to the
address rather than showing a number that would not work.

**How often does it actually change?** Rarely, but unpredictably - the worst combination for
a number typed into a page. What the journal shows (`journalctl -u valheim | grep 'join code'`
on the game box):

| when | code | |
|---|---|---|
| 2 Oct 22:23 | `781583` | first crossplay session |
| 2 Oct 22:32 | `555433` | changed |
| 2 Oct 22:38 → 22:54 | `555433` | four more restarts, same code |
| 5 Oct 04:44 | `968470` | new, after ~2.5 days with crossplay off |

So a restart does **not** normally mint a new code: five restarts inside half an hour all came
back with `555433`. PlayFab appears to hand the same code back while it still remembers the
session and allocate a fresh one once it does not, so the thing that changes a code is a long
gap, not a bounce. None of that is documented by Iron Gate or promised anywhere, which is the
argument for reading it rather than writing it down: one request makes the question moot.

## Deploying

**Pushing to GitHub does not deploy anything.** The Cloudflare Pages project `verseworlds`
has no git integration - `wrangler pages project list` prints `Git Provider: No` - so every
deployment is a direct upload from a machine that runs:

```sh
pnpm deploy          # pnpm build && wrangler pages deploy dist
```

Needs `wrangler login` once, or `CLOUDFLARE_API_TOKEN` in the environment. The Functions
bundle goes up with it (`✨ Uploading Functions bundle` in the output), so a change under
`functions/` ships only through this command too. That is worth knowing because the failure
is quiet: a missing Function is not a 404, it is the SPA's `index.html` served at
`/api/whatever` with a 200, which looks like a routing bug rather than a build that never
happened. `curl -s https://verseworlds.fun/api/health` is the cheap check - JSON means the
Functions bundle is live, HTML means it is not.

Connecting the repo to Pages (project → Settings → Builds: build command `pnpm build`,
output `dist`, root directory `site`) would make a push enough. Until someone does that,
the deploy is a deliberate act.

## Before this goes live

- Any Discord / social links - none exist yet, so the footer deliberately doesn't invent any.
- `index.html`'s Open Graph tags point at `https://verseworlds.fun/` already; add a real
  `og:image` once there's branding worth sharing in a link preview.
