export const meta = {
  name: 'translate-chunks',
  description: 'Agents (Haiku by default) translate scene-library chunks into Thai via LibraryTool show/submit',
  whenToUse: 'Scene library build: args = { game: "gta6", chunks: [12, 13, ...], model?: "haiku" | "sonnet" } after LibraryTool import; check results with LibraryTool check',
  phases: [{ title: 'Translate', detail: 'one agent per chunk' }],
}

const RESULT = {
  type: 'object',
  properties: {
    chunk: { type: 'string', description: 'four-digit chunk number' },
    ok: { type: 'boolean', description: 'true when the last submit printed OK' },
    note: { type: 'string', description: 'short reason when not ok, else empty' },
  },
  required: ['chunk', 'ok', 'note'],
}

const game = (args && args.game) || 'gta6'
const chunks = (args && args.chunks) || []
const model = (args && args.model) || 'haiku'
if (!chunks.length) throw new Error('args.chunks is empty')

const pad = (n) => String(n).padStart(4, '0')

phase('Translate')
const results = await pipeline(chunks, (n) =>
  agent(
    `Read tools/LibraryTool/translate-agent.md and follow it exactly.\nGAME=${game}\nCHUNK=${pad(n)}`,
    { label: `chunk ${pad(n)}`, phase: 'Translate', model, schema: RESULT },
  ),
)

const done = results.filter(Boolean)
const failed = done.filter((r) => !r.ok)
const lost = chunks.length - done.length
log(`${done.length - failed.length} ok, ${failed.length} failed, ${lost} without a result`)
return { ok: done.filter((r) => r.ok).map((r) => r.chunk), failed, lost }
