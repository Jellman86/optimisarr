// Capture the shipped Linux view with fabricated state, never a paired worker.
import { chromium, expect } from '@playwright/test'
import { spawn, execFileSync } from 'node:child_process'
import { readFile, writeFile } from 'node:fs/promises'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { applicationVersion, artwork, when } from './docs-fixtures.mjs'

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const images = resolve(web, '../docs/images')
const origin = 'http://127.0.0.1:4219'
const variants = [
  { name: 'dark', width: 1440, height: 1000, theme: 'dark' },
  { name: 'light', width: 1440, height: 1000, theme: 'light' },
  { name: 'phone', width: 390, height: 844, theme: 'light' },
]
const audio = {
  jobId: 42, title: 'Audio Study · Generated fixture', kind: 'Audio', encoder: 'libopus',
  stage: 'Encoding', encodedSeconds: 31, sourceBytes: 18e6, outputExtension: 'opus',
  hardwareDecoder: null, previewRevision: 1, hasArtwork: false,
  startedAt: '2026-09-17T11:58:00Z',
  sourceMedia: { videoCodec: null, width: null, height: null, durationSeconds: 100,
    audioCodecs: 'flac', pixelFormat: null },
}
const video = {
  ...audio, title: 'Lumen Coast · Documentation clip', kind: 'Video', encoder: 'libx265',
  encodedSeconds: 751, sourceBytes: 8e9, outputExtension: 'mkv', hasArtwork: true,
  startedAt: '2026-09-17T11:50:00Z',
  sourceMedia: { videoCodec: 'h264', width: 1920, height: 1080, durationSeconds: 1200,
    audioCodecs: 'aac', pixelFormat: 'yuv420p' },
}
const base = {
  name: 'Studio Linux', state: 'Working', serverAddress: 'https://optimisarr.example',
  scratchPath: '/work', storage: { kind: 'Disk', freeBytes: 400e9, totalBytes: 1e12 },
  concurrency: 1, capabilities: { videoEncoders: ['libx265'],
    audioEncoders: ['aac', 'libopus', 'libmp3lame'], hardwareDecoders: [], vmaf: 'Cpu' },
  recent: [], version: applicationVersion, brandStyle: 'precession', update: null,
  pairing: { required: false, configuredServer: null, inProgress: false, problem: null },
  metrics: { cpuPercent: 18, gpuPercent: null, gpuEngine: null, sampledAt: when },
}
const states = {
  audio: { ...base, jobs: [audio] },
  encoding: { ...base, jobs: [video] },
  idle: { ...base, state: 'Connected', jobs: [], metrics: { ...base.metrics, cpuPercent: 2 },
    recent: [{ jobId: 41, title: 'Paper Satellites · Documentation clip', delivered: true,
      finishedAt: '2026-09-17T11:56:00Z' }] },
  pairing: { ...base, state: 'Unpaired', jobs: [], serverAddress: null,
    pairing: { ...base.pairing, required: true } },
}

export async function captureLinuxSidecar(spectrumPath, selectedStates = Object.keys(states)) {
  if (!spectrumPath) throw Error('Pass a generated JPEG spectrum path; see docs/images/README.md')
  const spectrum = await readFile(spectrumPath)
  if (spectrum.length > 8192) throw Error('Preview exceeds the worker thumbnail limit')
  const server = spawn(process.execPath, ['node_modules/vite/bin/vite.js', '--host',
    '127.0.0.1', '--port', '4219', '--strictPort'], { cwd: web, stdio: 'pipe' })
  let browser
  const captured = []
  const problems = []
  try {
    await new Promise((ready, reject) => {
      let output = ''
      const timer = setTimeout(() => reject(Error('Owned server did not start')), 15000)
      const failed = error => { clearTimeout(timer); reject(error) }
      server.once('error', failed)
      server.once('exit', () => failed(Error('Owned server exited')))
      server.stdout.on('data', data => {
        output += data
        if (output.includes(origin)) { clearTimeout(timer); ready() }
      })
    })
    browser = await chromium.launch()
    for (const state of selectedStates) {
      if (!states[state]) throw Error(`Unknown fixture state: ${state}`)
      for (const variant of variants) {
        const context = await browser.newContext({ viewport: { width: variant.width, height: variant.height },
          colorScheme: variant.theme, reducedMotion: 'reduce', locale: 'en-GB', timezoneId: 'UTC' })
        const page = await context.newPage()
        await page.clock.install({ time: new Date(when) })
        await page.clock.pauseAt(new Date(when))
        page.on('pageerror', error => problems.push(error.message))
        await page.route('**/*', route => {
          const request = route.request(), url = new URL(request.url())
          if (url.origin !== origin || request.method() !== 'GET') {
            problems.push(`Unexpected request: ${request.method()} ${url.pathname}`)
            return route.abort()
          }
          if (url.pathname === '/api/sidecar/status') return route.fulfill({ json: states[state] })
          if (url.pathname === '/api/sidecar/jobs/42/preview') return state === 'audio'
            ? route.fulfill({ contentType: 'image/jpeg', body: spectrum })
            : route.fulfill({ contentType: 'image/svg+xml', body: artwork(1, true) })
          if (url.pathname === '/api/sidecar/jobs/42/artwork')
            return route.fulfill({ contentType: 'image/svg+xml', body: artwork(1) })
          if (url.pathname.startsWith('/api/')) {
            problems.push(`Unexpected API: ${url.pathname}`)
            return route.abort()
          }
          return route.continue()
        })
        await page.goto(`${origin}/sidecar.html`)
        await expect(page.getByRole('region', { name: 'Worker state' })).toBeVisible()
        if (state === 'pairing') await expect(page.getByRole('heading', { name: 'Pair Studio Linux' })).toBeVisible()
        else if (state === 'idle') await expect(page.getByText('Ready for work', { exact: true })).toBeVisible()
        else await expect(page.getByRole('img', { name: state === 'audio'
          ? `Source audio spectrogram of ${audio.title}` : `Latest frame of ${video.title}` })).toBeVisible()
        await page.evaluate(async () => {
          await document.fonts.ready
          await Promise.all([...document.images].map(image => image.decode()))
        })
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth))
          throw Error(`Horizontal overflow: ${state}/${variant.name}`)
        const filename = `optimisarr-sidecar-linux-${state}-${variant.name}.png`
        await page.screenshot({ path: resolve(images, filename), fullPage: true })
        captured.push(filename)
        await context.close()
      }
    }
    if (problems.length) throw Error(problems.join('\n'))
    if (selectedStates.length === Object.keys(states).length) await writeFile(
      resolve(images, 'linux-sidecar-screenshot-manifest.json'), JSON.stringify({
        applicationVersion, uiRevision: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: web, encoding: 'utf8' }).trim(),
        source: 'web/src/Sidecar.svelte', fixtureClock: when, fabricated: true,
        variants, images: captured,
      }, null, 2) + '\n')
    console.log(`Captured ${captured.length} Linux sidecar screenshots`)
  } finally {
    await browser?.close()
    server.kill('SIGTERM')
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  await captureLinuxSidecar(process.argv[2])
