import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion"

const FAQS: { q: string; a: string }[] = [
  {
    q: "Do I need any mods?",
    a: "No. Verse runs entirely on the server - connect with a completely unmodified copy of Valheim.",
  },
  {
    q: "Is this just separate worlds, like separate servers?",
    a: "No - it's one running server and one shared map underneath everyone. What's isolated is who can see, reach, or build on what you've made. Terrain, locations and the landscape itself are the same for every verse.",
  },
  {
    q: "What happens if I join a friend's verse?",
    a: "You give up your own (unless someone's still in it) and move into theirs, with their build, their progression, and their rules on who else can get in. You can always !verse leave to start over in a fresh one.",
  },
  {
    q: "Will someone else's boss kill or trader stock affect my game?",
    a: "No. Keys, boss defeats, trader stock and spawn tables are tracked per verse, so your world's progression is entirely your own party's doing.",
  },
  {
    q: "Can I lock other people out?",
    a: "Yes - every verse starts private. Nobody can see it, reach it, or join it until you !verse open it, set a !verse password, or !verse invite them by name.",
  },
  {
    q: "How do I join from Xbox or Switch?",
    a: "The same way as on PC: Start Game → Join Game → Join code, and type the join code at the top of this page. Crossplay is on and there is no password. The code is read live from the server, so the one shown here is always the one that works - if you have an older one written down, check back here.",
  },
  {
    q: "What does changing verse actually do to my connection?",
    a: "You'll see a short disconnect with a chat message explaining what happened - that's normal. Reconnect to the same server address and you'll land in the right place.",
  },
]

export function Faq() {
  return (
    <section id="faq" className="border-t border-border/60 py-20 sm:py-28">
      <div className="mx-auto max-w-2xl px-6">
        <h2 className="text-center text-3xl sm:text-4xl">Questions</h2>

        <Accordion type="single" collapsible className="mt-10 w-full">
          {FAQS.map(({ q, a }, i) => (
            <AccordionItem key={q} value={`item-${i}`}>
              <AccordionTrigger className="text-left text-base">{q}</AccordionTrigger>
              <AccordionContent className="text-sm leading-relaxed text-muted-foreground">
                {a}
              </AccordionContent>
            </AccordionItem>
          ))}
        </Accordion>
      </div>
    </section>
  )
}
