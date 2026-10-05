import { Compass, Swords, Users } from "lucide-react"
import { Button } from "@/components/ui/button"

export function Hero() {
  return (
    <section id="top" className="relative overflow-hidden">
      <div
        className="pointer-events-none absolute inset-0 -z-10"
        style={{
          background:
            "radial-gradient(ellipse 60% 50% at 50% -10%, color-mix(in oklch, var(--primary) 18%, transparent), transparent)",
        }}
        aria-hidden="true"
      />

      <div className="mx-auto flex max-w-4xl flex-col items-center gap-6 px-6 pt-20 pb-24 text-center sm:pt-28 sm:pb-32">
        <div className="inline-flex items-center gap-2 rounded-full border border-border bg-card px-4 py-1.5 text-xs text-muted-foreground">
          <span className="relative flex size-2">
            <span className="absolute inline-flex size-full animate-ping rounded-full bg-primary opacity-60" />
            <span className="relative inline-flex size-2 rounded-full bg-primary" />
          </span>
          Dedicated server, always up
        </div>

        <h1 className="max-w-2xl text-4xl leading-tight sm:text-6xl">
          One server. <span className="text-primary">Everyone's own world.</span>
        </h1>

        <p className="max-w-xl text-base leading-relaxed text-muted-foreground sm:text-lg">
          Verse is a Valheim server where the moment you connect, you get a private world of
          your own — a <em>verse</em>. Explore and build alone, invite friends in, or open the
          gate to anyone. Nobody sets foot in your Valheim without your say.
        </p>

        <div className="mt-2 flex flex-col gap-3 sm:flex-row">
          <Button size="lg" asChild>
            <a href="#connect">Join the server</a>
          </Button>
          <Button size="lg" variant="outline" asChild>
            <a href="#concept">How it works</a>
          </Button>
        </div>

        <dl className="mt-10 grid w-full grid-cols-1 gap-4 sm:grid-cols-3">
          {[
            { icon: Users, label: "Your own party", detail: "private by default" },
            { icon: Swords, label: "Your own progression", detail: "no spoiled bosses" },
            { icon: Compass, label: "One shared map", detail: "same world underneath" },
          ].map(({ icon: Icon, label, detail }) => (
            <div
              key={label}
              className="flex flex-col items-center gap-1.5 rounded-lg border border-border/60 bg-card/50 px-4 py-5"
            >
              <Icon className="size-5 text-primary" />
              <dt className="text-sm font-medium text-foreground">{label}</dt>
              <dd className="text-xs text-muted-foreground">{detail}</dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  )
}
