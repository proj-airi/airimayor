import { defineConfig } from 'vieval'

const model = process.env.ROAD_MODEL || 'gpt-6.2-sol'

export default defineConfig({
  projects: [{
    name: 'road-editing',
    root: '.',
    include: ['roads.eval.mjs'],
    inferenceExecutors: [{ id: 'codex' }],
    models: [{ id: `codex:${model}`, model, inferenceExecutor: 'codex', inferenceExecutorId: 'codex' }],
    runMatrix: { extend: {
      representation: ['coordinates', 'references'],
      operation: ['rebuild', 'edit'],
      repeat: [1, 2],
    } },
  }],
})
