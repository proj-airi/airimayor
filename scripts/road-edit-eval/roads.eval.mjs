import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { caseOf, defineScore, describeTask } from 'vieval'
import { cases, promptFor } from './cases.mjs'
import { applyChange, documentFor, mapSvg, network } from './streets.mjs'

const scores = Object.fromEntries(['exact', 'target', 'topology', 'preservation'].map(name =>
  [name, defineScore(name, { label: name, ...(name === 'exact' ? { rank: 1 } : {}) })]))

const replay = process.env.ROAD_REPLAY
  ? (await readFile(process.env.ROAD_REPLAY, 'utf8')).trim().split(/\r?\n/).map(line => JSON.parse(line))
  : null

async function ask(model, prompt) {
  const directory = await mkdtemp(join(tmpdir(), 'airimayor-road-eval-'))
  try {
    const executable = process.platform === 'win32' ? 'codex.exe' : 'codex'
    const child = spawn(executable, [
      'exec', '--ephemeral', '--ignore-user-config', '--skip-git-repo-check', '-s', 'read-only',
      '-c', 'model_reasoning_effort="low"', '-m', model, '--json', '-',
    ], { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], timeout: 120000 })
    let output = ''
    let diagnostics = ''
    child.stdout.setEncoding('utf8').on('data', chunk => { output += chunk })
    child.stderr.setEncoding('utf8').on('data', chunk => { diagnostics += chunk })
    const finished = new Promise((resolve, reject) => {
      child.on('error', reject)
      child.on('close', resolve)
      child.stdin.on('error', reject)
    })
    child.stdin.end(prompt)
    const status = await finished
    if (status !== 0) {
      const category = /auth|unauthorized/i.test(diagnostics) ? 'authentication' : 'execution'
      throw new Error(`Codex ${category} failure (exit ${status})`)
    }
    const events = output.trim().split(/\r?\n/).map(line => JSON.parse(line))
    const items = events.filter(event => event.type === 'item.completed').map(event => event.item)
    if (items.some(item => !['reasoning', 'agent_message'].includes(item.type)))
      throw new Error('External tool use invalidates this text-only trial')
    const answer = items.findLast(item => item.type === 'agent_message')?.text
    if (typeof answer !== 'string') throw new Error('Codex returned no final message')
    return { answer, usage: events.findLast(event => event.type === 'turn.completed')?.usage }
  } finally {
    await rm(directory, { recursive: true, force: true })
  }
}

async function runEdit(instruction, doc, model, representation, operation, saved) {
  const prompt = promptFor(instruction, representation, operation, doc)
  const digest = createHash('sha256').update(prompt).digest('hex')
  if (replay && saved?.digest !== digest) throw new Error('Missing answer or changed prompt in replay')
  const { answer, usage } = saved ?? await ask(model, prompt)
  const trial = { prompt, digest, answer, usage }
  try {
    const result = applyChange(doc, JSON.parse(answer), operation)
    return { trial, doc: result, city: network(result) }
  } catch (error) {
    trial.invalid = error.message
    return { trial }
  }
}

describeTask('district-edits', () => {
  for (const scenario of cases) {
    caseOf(scenario.name, async context => {
      const { representation, operation, repeat } = context.task.matrix.run
      const model = process.env.ROAD_MODEL || 'gpt-6.2-sol'
      const saved = replay?.find(row => row.scenario === scenario.name && row.model === model
        && row.operation === operation && row.representation === representation && String(row.repeat) === String(repeat))
      let step = await runEdit(scenario.instruction, documentFor(representation), model, representation, operation, saved?.steps[0])
      const steps = [step.trial]
      const invalid = { target: false, topology: false, preservation: false }
      const verdict = step.city ? scenario.grade(step.city) : { ...invalid }
      step.trial.verdict = { ...verdict }
      if (scenario.followup && Object.values(verdict).every(Boolean)) {
        const initial = step.city
        step = await runEdit(scenario.followup, step.doc, model, representation, operation, saved?.steps[1])
        steps.push(step.trial)
        Object.assign(verdict, step.city ? scenario.gradeFollowup(initial, step.city) : invalid)
        step.trial.verdict = { ...verdict }
      }
      const exact = Object.values(verdict).every(Boolean)
      for (const [key, value] of Object.entries({ exact, ...verdict }))
        context.recordScore(scores[key], Number(value))
      await context.recordArtifact('trial.json', {
        body: JSON.stringify({ model, replayed: Boolean(replay), representation, operation, repeat, scenario: scenario.name, steps, verdict, exact }, null, 2),
        contentType: 'application/json', purpose: 'road-edit.trial',
      })
      if (step.city) await context.recordArtifact('district.svg', {
        body: mapSvg(step.city, scenario.followup ? (steps.length === 2 ? 25 : 30) : 0),
        contentType: 'image/svg+xml', purpose: 'road-edit.map',
      })
    }, { timeout: 250000 })
  }
}, { scores: Object.values(scores) })
