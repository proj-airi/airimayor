import { createApp } from 'vue'
import { createI18n } from 'vue-i18n'
import Viewer from './viewer.vue'
import '@fontsource-variable/comfortaa'
import '@unocss/reset/tailwind.css'
import '@proj-airi/ui/src/main.css'
import 'vue-json-pretty/lib/styles.css'

declare const __TRACE_MESSAGES__: Record<string, unknown>

function reportError(reason: unknown) {
  const output = document.getElementById('runtime-error')!
  output.textContent = `Viewer error: ${reason instanceof Error ? `${reason.name}: ${reason.message}` : String(reason)}`
  output.hidden = false
  console.error(reason)
}

window.addEventListener('error', (event) => reportError(event.error ?? event.message))
window.addEventListener('unhandledrejection', (event) => reportError(event.reason))

const app = createApp(Viewer)
app.config.errorHandler = reportError
app.use(
  createI18n({
    legacy: false,
    locale: 'en',
    messages: { en: { tamagotchi: { settings: { devtools: { pages: { 'io-tracer': __TRACE_MESSAGES__ } } } } } },
  }),
)
app.mount('#app')
