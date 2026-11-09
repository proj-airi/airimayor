import { writeFile } from 'node:fs/promises'
import { profileDefinitions } from './profile'

const definitions = profileDefinitions
await writeFile(new URL('../../Agent/TraceProfile.json', import.meta.url), `${JSON.stringify(definitions, null, 2)}\n`)
console.log(`Generated ${definitions.length} native agent profile definitions`)
