import { Menu } from "lucide-react"
import { Button } from "@/components/ui/button"
import {
  Sheet,
  SheetClose,
  SheetContent,
  SheetTrigger,
} from "@/components/ui/sheet"
import { JoinCodeChip } from "@/components/site/JoinCode"

const LINKS = [
  { href: "#concept", label: "What's a Verse" },
  { href: "#map", label: "Map" },
  { href: "#commands", label: "Commands" },
  { href: "#connect", label: "Connect" },
  { href: "#faq", label: "FAQ" },
]

function Wordmark() {
  return (
    <a href="#top" className="flex items-center gap-2 shrink-0">
      <svg viewBox="0 0 32 32" className="h-7 w-7" aria-hidden="true">
        <rect width="32" height="32" rx="6" className="fill-card" />
        <path
          d="M7 8 L16 24 L25 8"
          fill="none"
          stroke="currentColor"
          className="text-primary"
          strokeWidth="3"
          strokeLinecap="square"
        />
        <path
          d="M16 24 L16 8"
          fill="none"
          stroke="currentColor"
          className="text-primary"
          strokeWidth="2"
          strokeLinecap="square"
          opacity="0.55"
        />
      </svg>
      <span className="font-heading text-lg tracking-widest">VERSE</span>
    </a>
  )
}

export function SiteNav() {
  return (
    <header className="sticky top-0 z-50 border-b border-border/60 bg-background/80 backdrop-blur-md">
      <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-6 py-4 sm:gap-4">
        <Wordmark />

        <nav className="hidden items-center gap-8 md:flex">
          {LINKS.map((link) => (
            <a
              key={link.href}
              href={link.href}
              className="text-sm text-muted-foreground transition-colors hover:text-foreground"
            >
              {link.label}
            </a>
          ))}
        </nav>

        {/* The join code stands where "Join the server" used to: it is the same call to
            action with the scroll taken out, so it gets that button's weight and colour.
            Unlike the button it is here at every width - on a phone the code is the whole
            reason somebody opened the page, and it should not be behind the menu. */}
        <div className="flex min-w-0 items-center gap-2">
          <JoinCodeChip />

          <Sheet>
            <SheetTrigger asChild>
              <Button variant="ghost" size="icon" className="md:hidden" aria-label="Open menu">
                <Menu className="size-5" />
              </Button>
            </SheetTrigger>
            <SheetContent side="right" className="w-64">
              <nav className="mt-10 flex flex-col gap-6 px-6">
                {LINKS.map((link) => (
                  <SheetClose asChild key={link.href}>
                    <a href={link.href} className="text-base text-foreground">
                      {link.label}
                    </a>
                  </SheetClose>
                ))}
                <SheetClose asChild>
                  <Button asChild className="mt-2">
                    <a href="#connect">Join the server</a>
                  </Button>
                </SheetClose>
              </nav>
            </SheetContent>
          </Sheet>
        </div>
      </div>
    </header>
  )
}
