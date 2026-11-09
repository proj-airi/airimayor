import { createApp } from 'vue'
import { createI18n } from 'vue-i18n'
import Viewer from './viewer.vue'
import '@proj-airi/ui/src/main.css'
import 'vue-json-pretty/lib/styles.css'

declare const __TRACE_MESSAGES__: Record<string, unknown>

createApp(Viewer)
  .use(
    createI18n({
      legacy: false,
      locale: 'en',
      messages: { en: { tamagotchi: { settings: { devtools: { pages: { 'io-tracer': __TRACE_MESSAGES__ } } } } } },
    }),
  )
  .mount('#app')
