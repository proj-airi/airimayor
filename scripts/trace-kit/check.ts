import type { TraceRecord } from '@proj-airi/trace-kit'
import type { Recording } from './viewer-model'
import { readFile, readdir, realpath, stat } from 'node:fs/promises'
import { basename, isAbsolute, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { isObject, loadDirectory, payload } from './viewer-model'
import { judgeWrite, type Verdict } from './write-evidence'

export class Report {
  failures = 0
  verdicts: Record<Verdict, number> = { confirmed: 0, diverged: 0, undetermined: 0 }

  constructor(readonly print: (message: string) => void = console.log) {}

  fail(where: string, message: string): void {
    this.failures++
    this.print(`FAIL  ${where}: ${message}`)
  }

  verdict(where: string, verdict: Verdict, message: string): void {
    this.verdicts[verdict]++
    if (verdict === 'diverged') this.failures++
    this.print(`${verdict.padEnd(12)}  ${where}: ${message}`)
  }
}

function checkCityResult(
  recording: Recording,
  record: Extract<TraceRecord, { type: 'instant' }>,
  report: Report,
  where: string,
): void {
  const data = payload(recording, record)
  if (typeof data.tool !== 'string' || typeof data.success !== 'boolean') {
    report.fail(where, 'city.tool.result needs a JSON asset with tool and success')
    return
  }
  const label = `${where} ${data.tool} ${record.ids?.toolCall ?? record.spanId ?? ''}`
  const anchors = isObject(data.timeline) ? data.timeline : {}
  const frames = ['submitted', 'applied', 'completed']
    .filter((name) => anchors[name] !== undefined)
    .map((name) => (isObject(anchors[name]) ? anchors[name].frame : undefined))
  if (
    !frames.every((frame): frame is number => typeof frame === 'number' && Number.isSafeInteger(frame)) ||
    frames.some((frame, index) => index > 0 && frame < frames[index - 1])
  )
    report.fail(label, `anchors out of order or invalid: ${frames.join(', ')}`)
  if (
    data.files !== undefined &&
    (!Array.isArray(data.files) ||
      data.files.some(
        (file) =>
          !isObject(file) || typeof file.name !== 'string' || !isObject(file.asset) || !('$asset' in file.asset),
      ))
  )
    report.fail(label, 'files must contain names and native asset references')
  const judgment = judgeWrite(data.tool, data)
  if (judgment.invalid) report.fail(label, judgment.message)
  else if (judgment.verdict) report.verdict(label, judgment.verdict, judgment.message)
}

export async function checkRecord(directory: string, report: Report, label = basename(directory)): Promise<void> {
  report.print(`\n== ${label}`)
  let recording: Recording
  try {
    const root = await realpath(directory)
    recording = await loadDirectory({
      name: label,
      async file(path) {
        try {
          const actual = await realpath(join(root, path))
          const inside = relative(root, actual)
          if (isAbsolute(inside) || inside === '..' || inside.startsWith(`..${sep}`))
            throw new Error('Asset resolves outside the recording')
          const info = await stat(actual)
          if (!info.isFile()) throw new Error('Recording entry is not a file')
          return { path, size: info.size, read: () => readFile(actual) }
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code === 'ENOENT') return
          throw error
        }
      },
    })
  } catch (error) {
    report.fail(label, (error as Error).message)
    return
  }
  const where = (index: number) => `${label}/record#${index + 1}`
  for (const issue of recording.issues)
    report.fail(issue.index === undefined ? label : where(issue.index), issue.message)
  recording.records.forEach((record, index) => {
    if (record.type !== 'instant') return
    const data = payload(recording, record)
    if (record.name === 'city.tool.result') checkCityResult(recording, record, report, where(index))
    if (record.name === 'recording.gap') report.print(`note  ${where(index)}: known gap ${data.message ?? ''}`)
    if (record.name === 'session.fact' && data.key === 'session' && isObject(data.value)) {
      const facts = data.value
      const details = ['head', 'city', 'provider', 'model', 'protocol']
        .filter((key) => facts[key] !== null && facts[key] !== undefined)
        .map((key) => `${key} ${facts[key]}`)
        .join('; ')
      report.print(`session ${record.ids?.conversation ?? record.epoch}; ${details}`)
    }
  })
  const view = recording.full
  const timeline = view.timelineView()
  const modelSpans = [...timeline.spans.values()].filter((span) => span.track.endsWith('/llm'))
  const toolSpans = [...timeline.spans.values()].filter((span) => span.track.endsWith('/tools'))
  const usage = (key: string) => {
    const values = modelSpans.map((span) => span.attrs[key]).filter((value) => typeof value === 'number')
    return values.length ? values.reduce((sum, value) => sum + value, 0) : 'unavailable'
  }
  report.print(
    `trace: ${view.reader.indexes.byKey.size} records, ${timeline.spans.size} spans, ${modelSpans.length} model calls, ${toolSpans.length} tools; tokens: input ${usage('gen_ai.usage.input_tokens')}, output ${usage('gen_ai.usage.output_tokens')}`,
  )
}

async function newestRecord(): Promise<string | undefined> {
  const userData =
    process.env.CSII_USERDATAPATH ??
    join(
      process.env.USERPROFILE ?? process.env.HOME ?? '',
      'AppData',
      'LocalLow',
      'Colossal Order',
      'Cities Skylines II',
    )
  const root = join(userData, 'ModsData', 'airimayor', 'sessions')
  const entries = await readdir(root, { withFileTypes: true }).catch((error) => {
    if (error.code === 'ENOENT') return []
    throw error
  })
  for (const entry of entries.filter((entry) => entry.isDirectory()).sort((a, b) => b.name.localeCompare(a.name))) {
    const directory = join(root, entry.name)
    if (await stat(join(directory, 'records.jsonl')).catch(() => null)) return directory
  }
}

async function main(): Promise<number> {
  const directory = process.argv[2] ? resolve(process.argv[2]) : await newestRecord()
  if (!directory) {
    console.log('No session record found. Pass a record directory or set CSII_USERDATAPATH.')
    return 2
  }
  const report = new Report()
  await checkRecord(directory, report)
  report.print(
    `\nwrites: ${Object.entries(report.verdicts)
      .map(([verdict, count]) => `${count} ${verdict}`)
      .join(', ')}; failures: ${report.failures}`,
  )
  return report.failures ? 1 : 0
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(
    (code) => {
      process.exitCode = code
    },
    (error) => {
      console.error(`FAIL  ${(error as Error).message}`)
      process.exitCode = 1
    },
  )
}
