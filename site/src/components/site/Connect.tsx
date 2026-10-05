import { Check, Copy } from "lucide-react"
import { useState } from "react"
import { Button } from "@/components/ui/button"
import { Card, CardContent } from "@/components/ui/card"
import { JoinCodeCard } from "@/components/site/JoinCode"
import { SERVER_ADDRESS, SERVER_NAME, useJoin } from "@/lib/join"

function CopyAddress() {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(SERVER_ADDRESS)
      setCopied(true)
      setTimeout(() => setCopied(false), 1800)
    } catch {
      // Clipboard API can refuse (no permission, insecure context); the address is
      // still right there in the UI for a manual copy.
    }
  }

  return (
    <div className="flex items-center gap-2 rounded-lg border border-border bg-muted/40 py-2 pl-4 pr-2">
      <code className="flex-1 text-sm text-foreground sm:text-base">{SERVER_ADDRESS}</code>
      <Button size="icon" variant="ghost" onClick={copy} aria-label="Copy server address">
        {copied ? <Check className="size-4 text-primary" /> : <Copy className="size-4" />}
      </Button>
    </div>
  )
}

export function Connect() {
  const { code } = useJoin()

  return (
    <section id="connect" className="border-t border-border/60 py-20 sm:py-28">
      <div className="mx-auto max-w-3xl px-6 text-center">
        <h2 className="text-3xl sm:text-4xl">Join {SERVER_NAME}</h2>
        <p className="mt-4 text-muted-foreground">
          Same unmodified Valheim client - nothing to install. Connect, and you'll land in your
          own verse automatically. No password.
        </p>

        {/* The code first and biggest: it is the way in that works from every platform, and
            the only one a console player has. */}
        <div className="mt-10">
          <JoinCodeCard />
        </div>

        <div className="mt-6 grid gap-6 text-left sm:grid-cols-2">
          <Card className="border-border/60 bg-card/50">
            <CardContent className="pt-6">
              <h3 className="mb-2 font-heading text-sm tracking-wider text-primary">
                Server browser
              </h3>
              <p className="text-sm text-muted-foreground">
                In Valheim, open <strong className="text-foreground">Start Game → Servers</strong>{" "}
                and search for <strong className="text-foreground">{SERVER_NAME}</strong>. No
                password needed.
              </p>
            </CardContent>
          </Card>
          <Card className="border-border/60 bg-card/50">
            <CardContent className="pt-6">
              <h3 className="mb-2 font-heading text-sm tracking-wider text-primary">
                By address
              </h3>
              <p className="mb-3 text-sm text-muted-foreground">
                {code
                  ? "On PC you can also use the server list's \"Join IP\" tab:"
                  : "Use the server list's \"Join IP\" tab:"}
              </p>
              <CopyAddress />
            </CardContent>
          </Card>
        </div>
      </div>
    </section>
  )
}
