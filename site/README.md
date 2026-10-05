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

## Deploying

The repo connects straight to a Cloudflare Pages project (Pages → your project → Settings →
Builds): build command `pnpm build`, output directory `dist`, root directory `site`. Every
push builds and deploys automatically, functions included.

To push a build by hand instead: `pnpm deploy` (runs `wrangler pages deploy dist`; needs
`wrangler login` once, or `CLOUDFLARE_API_TOKEN` in the environment).

## Before this goes live

- Any Discord / social links - none exist yet, so the footer deliberately doesn't invent any.
- `index.html`'s Open Graph tags point at `https://verseworlds.fun/` already; add a real
  `og:image` once there's branding worth sharing in a link preview.
