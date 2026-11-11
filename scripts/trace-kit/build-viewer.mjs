import { build } from 'esbuild'
import { readFile, readdir, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { presetChromatic } from '@proj-airi/unocss-preset-chromatic'
import MagicString from 'magic-string'
import Vue from 'unplugin-vue/esbuild'
import { createGenerator, presetAttributify, presetIcons, presetWind3, transformerDirectives } from 'unocss'
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
  loader: { '.woff2': 'dataurl' },
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
const uno = await createGenerator({
  presets: [
    presetAttributify(),
    presetWind3(),
    presetIcons({
      collections: {
        mingcute: () =>
          import('@iconify-json/mingcute/icons.json', { with: { type: 'json' } }).then((module) => module.default),
        solar: () =>
          import('@iconify-json/solar/icons.json', { with: { type: 'json' } }).then((module) => module.default),
      },
    }),
    presetChromatic({ baseHue: 220.44, colors: { primary: 0, complementary: 180 } }),
  ],
  transformers: [transformerDirectives()],
  shortcuts: {
    'viewer-button':
      'inline-flex shrink-0 items-center justify-center gap-2 rounded-md px-3 py-2 text-sm font-normal text-neutral-400 hover:bg-neutral-800 hover:text-neutral-100 disabled:cursor-default disabled:opacity-30 focus-visible:outline-2 focus-visible:outline-primary-400/50',
    'viewer-accent-button': 'bg-primary-800/75 text-primary-200 hover:bg-primary-800 hover:text-primary-100',
    'viewer-icon-button': 'viewer-button size-8 p-0',
    'viewer-icon': 'inline-block size-4 shrink-0',
    'viewer-tab':
      'viewer-button rounded-none border-b-2 border-transparent px-1 aria-selected:border-primary-300 aria-selected:text-primary-200',
    'viewer-empty':
      'flex h-full min-h-0 flex-col items-center justify-center gap-3 p-6 text-center text-sm text-neutral-400',
  },
})
const sourceFiles = (await readdir(root, { recursive: true })).filter(
  (file) => !file.startsWith('node_modules') && /\.(?:vue|ts)$/.test(file),
)
const utilities = await uno.generate(
  [template, ...(await Promise.all(sourceFiles.map((file) => readFile(join(root, file), 'utf8'))))].join('\n'),
)
let componentStyles = result.outputFiles
  .filter((file) => file.path.endsWith('.css'))
  .map((file) => file.text)
  .join('\n')
for (const enforce of ['pre', undefined, 'post']) {
  for (const transformer of uno.config.transformers.filter((item) => item.enforce === enforce)) {
    const source = new MagicString(componentStyles)
    await transformer.transform(source, join(root, 'viewer.css'), { uno })
    componentStyles = source.toString()
  }
}
const css = utilities.css + '\n' + componentStyles
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
packages.add('@iconify-json/mingcute')
packages.add('@iconify-json/solar')
const licenses = await Promise.all(
  [...packages].sort().map(async (name) => {
    const directory = resolve(root, 'node_modules', name)
    const files = (await readdir(directory)).filter((file) => /^(?:licen[sc]e|notice)(?:\.|$)/i.test(file))
    if (name === '@iconify-json/mingcute') {
      const info = JSON.parse(await readFile(join(directory, 'info.json'), 'utf8'))
      return `${name}\n${info.author.name}\n${info.author.url}\n${info.license.url}\n${await readFile(join(root, 'node_modules/comlink/LICENSE'), 'utf8')}`
    }
    if (name === '@iconify-json/solar') {
      const info = JSON.parse(await readFile(join(directory, 'info.json'), 'utf8'))
      return `${name}\n${info.author.name}\n${info.author.url}\n${info.license.title}\n${info.license.url}\nIcons rendered as CSS masks.`
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
