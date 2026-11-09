import { build } from 'esbuild'
import { readFile, readdir, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import Vue from 'unplugin-vue/esbuild'
import { createGenerator, presetIcons, presetWind3 } from 'unocss'
import { parse } from 'yaml'

const root = dirname(fileURLToPath(import.meta.url))
const worker = await build({
  entryPoints: [join(root, 'viewer-worker.ts')],
  bundle: true,
  write: false,
  minify: true,
  platform: 'browser',
  format: 'iife',
  target: 'es2022',
  tsconfig: join(root, 'tsconfig.json'),
  legalComments: 'inline',
  metafile: true,
})
const result = await build({
  entryPoints: [join(root, 'viewer.ts')],
  bundle: true,
  write: false,
  minify: true,
  platform: 'browser',
  format: 'iife',
  outfile: join(root, 'viewer.js'),
  plugins: [Vue({ isProduction: true, sourceMap: false })],
  target: 'es2022',
  tsconfig: join(root, 'tsconfig.json'),
  legalComments: 'inline',
  metafile: true,
  define: {
    __VIEWER_WORKER__: JSON.stringify(worker.outputFiles[0].text),
    __TRACE_MESSAGES__: JSON.stringify(
      parse(await readFile(join(root, '.upstream/packages/i18n/src/locales/en/tamagotchi/settings.yaml'), 'utf8'))
        .devtools.pages['io-tracer'],
    ),
    'process.env.NODE_ENV': '"production"',
    __VUE_OPTIONS_API__: 'false',
    __VUE_PROD_DEVTOOLS__: 'false',
    __VUE_PROD_HYDRATION_MISMATCH_DETAILS__: 'false',
    __VUE_I18N_LEGACY_API__: 'false',
    __VUE_I18N_FULL_INSTALL__: 'true',
    __INTLIFY_PROD_DEVTOOLS__: 'false',
    __INTLIFY_JIT_COMPILATION__: 'true',
  },
})
const template = await readFile(join(root, 'viewer-template.html'), 'utf8')
const wind = presetWind3()
const uno = await createGenerator({
  presets: [
    wind,
    presetIcons({
      collections: {
        mingcute: () =>
          import('@iconify-json/mingcute/icons.json', { with: { type: 'json' } }).then((module) => module.default),
      },
    }),
  ],
  theme: { colors: { primary: wind.theme.colors.violet } },
})
const sourceFiles = (await readdir(root, { recursive: true })).filter(
  (file) => !file.startsWith('node_modules') && /\.(?:vue|ts)$/.test(file),
)
const utilities = await uno.generate(
  (await Promise.all(sourceFiles.map((file) => readFile(join(root, file), 'utf8')))).join('\n'),
)
const css =
  utilities.css +
  '\n' +
  result.outputFiles
    .filter((file) => file.path.endsWith('.css'))
    .map((file) => file.text)
    .join('\n') +
  '\n' +
  (await readFile(join(root, 'viewer.css'), 'utf8'))
const license = await readFile(join(root, 'LICENSE.upstream'), 'utf8')
const upstream = JSON.parse(await readFile(join(root, 'upstream.json'), 'utf8'))
const script = result.outputFiles.find((file) => file.path.endsWith('.js')).text.replaceAll('</script', '<\\/script')
const attribution = `<!-- AIRI trace and UI packages: https://github.com/${upstream.repository}/tree/${upstream.commit}/packages\n${license}\n-->\n`
const packages = new Set(
  [...Object.values(result.metafile.outputs), ...Object.values(worker.metafile.outputs)]
    .flatMap((output) =>
      Object.entries(output.inputs)
        .filter(([, usage]) => usage.bytesInOutput > 0)
        .map(([path]) => path),
    )
    .map((path) => path.match(/(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)/)?.[1])
    .filter(Boolean),
)
const licenses = await Promise.all(
  [...packages].sort().map(async (name) => {
    const directory = resolve(root, 'node_modules', name)
    const files = (await readdir(directory)).filter((file) => /^(?:licen[sc]e|notice)(?:\.|$)/i.test(file))
    if (name === '@iconify-json/mingcute') {
      const info = JSON.parse(await readFile(join(directory, 'info.json'), 'utf8'))
      return `${name}\n${info.author.name}\n${info.author.url}\n${info.license.url}\n${await readFile(join(root, 'node_modules/comlink/LICENSE'), 'utf8')}`
    }
    if (name === 'vaul-vue')
      return `${name}\nhttps://github.com/unovue/vaul-vue\n${await readFile(join(root, 'LICENSE.vaul-vue'), 'utf8')}`
    if (!files.length) throw new Error(`Missing license text for bundled dependency ${name}`)
    return `${name}\n${(await Promise.all(files.map((file) => readFile(join(directory, file), 'utf8')))).join('\n')}`
  }),
)
const notices = `<!-- Bundled dependency licenses\n${licenses.join('\n\n').replaceAll('-->', '-- >')}\n-->\n`
const html =
  attribution + notices + template.replace('/* VIEWER_STYLE */', () => css).replace('/* VIEWER_SCRIPT */', () => script)
await writeFile(join(root, 'viewer.html'), html)
console.log(`Built offline viewer: ${join(root, 'viewer.html')} (${Buffer.byteLength(html).toLocaleString()} bytes)`)
