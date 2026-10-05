export function SiteFooter() {
  return (
    <footer className="border-t border-border/60 py-10">
      <div className="mx-auto flex max-w-6xl flex-col items-center justify-between gap-4 px-6 text-center text-sm text-muted-foreground sm:flex-row sm:text-left">
        <p>
          <span className="font-heading tracking-wide text-foreground">VERSE</span> ·
          verseworlds.fun
        </p>
        <p>Not affiliated with Iron Gate AB. Valheim is their trademark, not ours.</p>
        <p className="flex gap-4">
          <a href="/terms" className="hover:text-foreground">Terms</a>
          <a href="/privacy" className="hover:text-foreground">Privacy</a>
        </p>
      </div>
    </footer>
  )
}
