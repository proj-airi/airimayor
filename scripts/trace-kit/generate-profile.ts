import { writeFile } from 'node:fs/promises'
import { definitionRecordsOf } from '@proj-airi/trace-kit'
import { agentProfile } from '@proj-airi/trace-kit/agent'

const definitions = definitionRecordsOf(agentProfile, 0).map(({ ts, ...definition }) => definition)
await writeFile(new URL('../../Agent/TraceProfile.json', import.meta.url), `${JSON.stringify(definitions, null, 2)}\n`)
console.log(`Generated ${definitions.length} native agent profile definitions`)
