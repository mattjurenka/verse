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
  you alongside the static build, for whenever the site needs server-side logic (e.g. pulling
  a live player count onto the Connect section) instead of a separate Worker project.

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
