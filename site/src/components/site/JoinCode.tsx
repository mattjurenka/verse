import { Check, Copy } from "lucide-react"
import { useState } from "react"
import { Button } from "@/components/ui/button"
import { SERVER_ADDRESS, spaced, useJoin } from "@/lib/join"
import { cn } from "@/lib/utils"

/**
 * The join code, which is the one thing a visitor needs to take away from this page.
 *
 * Everything here reads it from `useJoin` rather than from a constant, so the number on the
 * page is the number the server actually has - see `src/lib/join.ts`. When crossplay is off
 * there is no code, and both of these fall back to the address rather than showing a number
 * that would not work.
 */

function useCopy() {
  const [copied, setCopied] = useState(false)

  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 1800)
    } catch {
      // The clipboard API can refuse - no permission, insecure context. The value is on
      // screen either way, which is what matters.
    }
  }

  return { copied, copy }
}

/**
 * Compact, for the site header and the footer.
 *
 * It stands where a "Join the server" button used to, so it wears that button's own colours -
 * primary background, dark text - and reads as a call to action rather than a label. It is
 * still a copy button, because copying the code is the action.
 *
 * With no code there is nothing to copy and the address is too long to sit in a header, so it
 * falls back to the button it replaced.
 */
export function JoinCodeChip({ className }: { className?: string }) {
  const { code } = useJoin()
  const { copied, copy } = useCopy()

  if (!code) {
    return (
      <Button asChild size="sm" className={className}>
        <a href="#connect">Join the server</a>
      </Button>
    )
  }

  return (
    <button
      type="button"
      onClick={() => copy(code)}
      title="Copy the join code"
      aria-label={`Join code ${code}. Click to copy.`}
      className={cn(
        "group flex h-8 shrink-0 items-center gap-2 rounded-lg bg-primary pl-2.5 pr-2",
        "transition-colors hover:bg-primary/80",
        className,
      )}
    >
      <span className="hidden text-[11px] uppercase tracking-wider text-primary-foreground/75 sm:inline">
        Join code
      </span>
      <code className="font-heading text-sm tracking-wider text-primary-foreground tabular-nums">
        {spaced(code)}
      </code>
      {copied ? (
        <Check className="size-3.5 text-primary-foreground" />
      ) : (
        <Copy className="size-3.5 text-primary-foreground/70 group-hover:text-primary-foreground" />
      )}
    </button>
  )
}

/** One line, for under the hero's buttons: the code where the decision to play gets made. */
export function JoinCodeLine() {
  const { code } = useJoin()
  const { copied, copy } = useCopy()

  if (!code) {
    return (
      <p className="text-sm text-muted-foreground">
        Join at <code className="text-foreground">{SERVER_ADDRESS}</code> - no password
      </p>
    )
  }

  return (
    <p className="flex flex-wrap items-center justify-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
      <span>Join code</span>
      <button
        type="button"
        onClick={() => copy(code)}
        aria-label={`Join code ${code}. Click to copy.`}
        className="group inline-flex items-center gap-1.5 rounded px-1 transition-colors hover:bg-primary/10"
      >
        <code className="font-heading text-base tracking-wider text-foreground tabular-nums sm:text-lg">
          {spaced(code)}
        </code>
        {copied ? (
          <Check className="size-3.5 text-primary" />
        ) : (
          <Copy className="size-3.5 opacity-60 group-hover:opacity-100" />
        )}
      </button>
      <span>- no password, any platform</span>
    </p>
  )
}

/** The big one, for the join section: the code as the headline instruction. */
export function JoinCodeCard() {
  const { code, crossplay } = useJoin()
  const { copied, copy } = useCopy()

  if (!code) {
    return (
      <div className="rounded-xl border border-border bg-muted/30 p-6 text-left">
        <p className="text-sm text-muted-foreground">
          {crossplay
            ? "The server is registering its join code - try the address below in the meantime."
            : "Crossplay is off at the moment, so there is no join code. Use the address below."}
        </p>
      </div>
    )
  }

  return (
    <div className="rounded-xl border border-primary/40 bg-primary/5 p-6 text-center">
      <p className="font-heading text-xs uppercase tracking-[0.2em] text-primary">Join code</p>

      <button
        type="button"
        onClick={() => copy(code)}
        aria-label={`Join code ${code}. Click to copy.`}
        className="group mt-3 inline-flex items-center gap-3 rounded-lg px-2 py-1 transition-colors hover:bg-primary/10"
      >
        <span className="font-heading text-4xl tracking-[0.15em] text-foreground tabular-nums sm:text-5xl">
          {spaced(code)}
        </span>
        {copied ? (
          <Check className="size-5 text-primary" />
        ) : (
          <Copy className="size-5 text-muted-foreground group-hover:text-foreground" />
        )}
      </button>

      <p className="mt-3 text-sm text-muted-foreground">
        In Valheim, <strong className="text-foreground">Start Game → Join Game → Join code</strong>
        . Works from PC, Xbox and Switch alike.
      </p>
    </div>
  )
}
