import { Commands } from "@/components/site/Commands"
import { Concept } from "@/components/site/Concept"
import { Connect } from "@/components/site/Connect"
import { Faq } from "@/components/site/Faq"
import { Hero } from "@/components/site/Hero"
import { SiteFooter } from "@/components/site/SiteFooter"
import { SiteNav } from "@/components/site/SiteNav"
import { WorldMap } from "@/components/site/WorldMap"

function App() {
  return (
    <div className="min-h-screen bg-background text-foreground">
      <SiteNav />
      <main>
        <Hero />
        <Concept />
        <WorldMap />
        <Commands />
        <Connect />
        <Faq />
      </main>
      <SiteFooter />
    </div>
  )
}

export default App
