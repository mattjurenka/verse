import { Badge } from "@/components/ui/badge"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"

type Row = { command: string; does: string; note?: string }

const VERSE_COMMANDS: Row[] = [
  { command: "!verse", does: "Shows your verse, who's in it, and how it's set to let people in." },
  { command: "!verse help", does: "Prints this list, in chat." },
  { command: "!verse open", does: "Anyone on the server can join with !verse join <n>." },
  {
    command: "!verse password <word>",
    does: "Anyone who knows the word can join with it.",
  },
  {
    command: "!verse invite <player>",
    does: "Lets one specific, currently-online player in.",
  },
  { command: "!verse private", does: "Closes it back up. Nobody new gets in." },
  {
    command: "!verse spawn",
    does: "Moves your verse's arrival point to where you're standing.",
    note: "leader only",
  },
  {
    command: "!verse join <n> [password]",
    does: "Joins verse <n>, giving up your own.",
    note: "confirm if it's your only one",
  },
  {
    command: "!verse leave",
    does: "Starts a brand new, empty verse that's yours alone.",
  },
]

const WARP_COMMANDS: Row[] = [
  {
    command: "!warp spawn",
    does: "Teleports you straight to your verse's arrival point.",
  },
  {
    command: "!warp bed",
    does: "Teleports you to a bed you've claimed, if you have one.",
    note: "claim a bed first",
  },
]

function Note({ note }: { note?: string }) {
  if (!note) return null
  return (
    <Badge variant="secondary" className="ml-2 align-middle text-[10px]">
      {note}
    </Badge>
  )
}

/**
 * A real <table> reads fine once there's room for a command column that doesn't wrap, but
 * below `sm` there just isn't - so narrow screens get a stacked command/description list
 * instead of a table fighting its own column widths.
 */
function CommandTable({ rows }: { rows: Row[] }) {
  return (
    <>
      <dl className="divide-y divide-border/60 sm:hidden">
        {rows.map((row) => (
          <div key={row.command} className="py-3 first:pt-0 last:pb-0">
            <dt className="font-mono text-sm text-primary">{row.command}</dt>
            <dd className="mt-1 text-sm text-muted-foreground">
              {row.does}
              <Note note={row.note} />
            </dd>
          </div>
        ))}
      </dl>

      <Table className="hidden sm:table">
        <TableHeader>
          <TableRow>
            <TableHead className="w-[1%] whitespace-nowrap">Command</TableHead>
            <TableHead>What it does</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.command}>
              <TableCell className="whitespace-nowrap font-mono text-sm text-primary">
                {row.command}
              </TableCell>
              <TableCell className="text-sm text-muted-foreground">
                {row.does}
                <Note note={row.note} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </>
  )
}

export function Commands() {
  return (
    <section id="commands" className="border-t border-border/60 py-20 sm:py-28">
      <div className="mx-auto max-w-4xl px-6">
        <div className="mx-auto max-w-2xl text-center">
          <h2 className="text-3xl sm:text-4xl">Commands</h2>
          <p className="mt-4 text-muted-foreground">
            Type these straight into in-game chat. No leading slash - that never leaves your own
            client in Valheim, so the server would never hear it.
          </p>
        </div>

        <div className="mt-12 rounded-xl border border-border/60 bg-card/40 p-2 sm:p-6">
          <Tabs defaultValue="verse">
            <TabsList className="grid w-full grid-cols-2 sm:w-auto">
              <TabsTrigger value="verse">!verse</TabsTrigger>
              <TabsTrigger value="warp">!warp</TabsTrigger>
            </TabsList>
            <TabsContent value="verse" className="mt-4">
              <CommandTable rows={VERSE_COMMANDS} />
            </TabsContent>
            <TabsContent value="warp" className="mt-4">
              <p className="mb-4 px-1 text-sm text-muted-foreground">
                Unlike <code className="text-primary">!verse</code>, warping is instant - you're
                already there, it just moves you.
              </p>
              <CommandTable rows={WARP_COMMANDS} />
            </TabsContent>
          </Tabs>
        </div>
      </div>
    </section>
  )
}
