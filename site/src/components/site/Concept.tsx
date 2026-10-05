import {
  Flame,
  Globe2,
  KeyRound,
  Lock,
  MessageCircle,
  Users,
} from "lucide-react"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"

const FEATURES = [
  {
    icon: Lock,
    title: "Private by default",
    body: "The first time you connect, you become the leader of your own verse. Nobody can see it, reach it, or build in it until you let them in.",
  },
  {
    icon: Users,
    title: "Party up your way",
    body: "Invite specific friends, hand out a password, or throw the gate open to anyone on the server. Join someone else's verse any time - you can always leave to a fresh one.",
  },
  {
    icon: KeyRound,
    title: "Your own progression",
    body: "Boss kills, keys, trader stock, and spawn tables are tracked per verse. One party beating Eikthyr doesn't skip the fight - or the danger - for anyone else.",
  },
  {
    icon: Globe2,
    title: "One world underneath",
    body: "Every verse explores the same thousand-kilometre map and the same landmarks. Isolation is about who can reach your claim, not a separate copy of Valheim per person.",
  },
  {
    icon: MessageCircle,
    title: "Shared chat",
    body: "Different world, same server - everyone can still talk to each other, whatever verse they're playing in and however far apart they'd be standing.",
  },
  {
    icon: Flame,
    title: "A real dedicated server",
    body: "Always on, no one's gaming PC keeping the world alive. Log off, come back next week, your verse is exactly how you left it.",
  },
]

export function Concept() {
  return (
    <section id="concept" className="border-t border-border/60 py-20 sm:py-28">
      <div className="mx-auto max-w-6xl px-6">
        <div className="mx-auto max-w-2xl text-center">
          <h2 className="text-3xl sm:text-4xl">What's a verse?</h2>
          <p className="mt-4 text-muted-foreground">
            Normally a Valheim server means one shared world - whoever logs in first claims the
            good building spots, and the second you fight Yagluth somebody's hunt for Eikthyr
            just got a lot less dramatic. Verse gives every player, or every party, a world
            that's theirs alone, on the exact same server.
          </p>
        </div>

        <div className="mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {FEATURES.map(({ icon: Icon, title, body }) => (
            <Card key={title} className="border-border/60 bg-card/50">
              <CardHeader>
                <div className="mb-2 flex size-9 items-center justify-center rounded-md bg-primary/15">
                  <Icon className="size-4.5 text-primary" />
                </div>
                <CardTitle className="text-base">{title}</CardTitle>
              </CardHeader>
              <CardContent>
                <CardDescription className="text-sm leading-relaxed">{body}</CardDescription>
              </CardContent>
            </Card>
          ))}
        </div>
      </div>
    </section>
  )
}
