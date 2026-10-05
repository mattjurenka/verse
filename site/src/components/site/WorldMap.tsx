import { Map } from "lucide-react"
import { Button } from "@/components/ui/button"

const SEED = "CUbMpDSA4a"
const MAP_URL = `https://valheimseed.com/?seed=${SEED}`

export function WorldMap() {
  return (
    <section id="map" className="border-t border-border/60 py-20 sm:py-28">
      <div className="mx-auto max-w-2xl px-6 text-center">
        <h2 className="text-3xl sm:text-4xl">The world underneath</h2>
        <p className="mt-4 text-muted-foreground">
          Every verse generates the same thousand-kilometre map, from the same seed -{" "}
          <code className="rounded bg-muted/60 px-1.5 py-0.5 text-foreground">{SEED}</code>.
          Scout biomes, boss altars and dungeons before you ever log in.
        </p>

        <Button asChild size="lg" className="mt-8">
          <a href={MAP_URL} target="_blank" rel="noreferrer">
            <Map className="size-4" />
            View the world map
          </a>
        </Button>
      </div>
    </section>
  )
}
