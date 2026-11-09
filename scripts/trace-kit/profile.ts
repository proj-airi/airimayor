import { definitionRecordsOf, defineMetric, defineProfile } from '@proj-airi/trace-kit'
import { agentProfile } from '@proj-airi/trace-kit/agent'

const mayorProfile = defineProfile({
  key: 'mayor',
  version: '1',
  metrics: [
    ['model.duration_ms', 'Model duration', 'airimayor.duration_ms', 'ms'],
    ['model.input_tokens', 'Input tokens', 'gen_ai.usage.input_tokens', 'tokens'],
    ['model.output_tokens', 'Output tokens', 'gen_ai.usage.output_tokens', 'tokens'],
    ['model.cache_tokens', 'Cache-read tokens', 'gen_ai.usage.cache_read.input_tokens', 'tokens'],
    ['model.reasoning_tokens', 'Reasoning tokens', 'gen_ai.usage.reasoning.output_tokens', 'tokens'],
  ].map(([key, label, attribute, unit]) =>
    defineMetric({
      key,
      label,
      unit,
      scope: 'session',
      dimensions: ['agent'],
      value: { kind: 'attribute', track: 'agents/{agent}/llm', record: 'span.end', attribute },
    }),
  ),
})

export const profileDefinitions = [
  ...definitionRecordsOf(agentProfile, 0),
  ...definitionRecordsOf(mayorProfile, 0),
].map(({ ts, ...definition }) => definition)
