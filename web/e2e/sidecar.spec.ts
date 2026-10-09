import { test, expect, type Page } from '@playwright/test'

const status = {
  name: 'Quark', state: 'Connected', serverAddress: 'https://optimisarr.example',
  scratchPath: '/work', storage: { kind: 'RAM', freeBytes: 6 * 1024 ** 3, totalBytes: 8 * 1024 ** 3 },
  concurrency: 1, capabilities: { videoEncoders: ['hevc_qsv', 'av1_qsv'], hardwareDecoders: ['qsv'], vmaf: 'Cpu' },
  jobs: [], recent: [], version: '0.2.16', brandStyle: 'precession', update: null,
  pairing: { required: false, configuredServer: null, inProgress: false, problem: null },
  metrics: { cpuPercent: 37.4, gpuPercent: 62.1, gpuEngine: 'Video', sampledAt: new Date().toISOString() },
}

const job = {
  jobId: 42, title: 'Test clip', encoder: 'hevc_qsv', stage: 'Encoding', encodedSeconds: 12, sourceBytes: 123456789,
  outputExtension: 'mkv', hardwareDecoder: 'qsv', previewRevision: 0, hasArtwork: false, startedAt: new Date().toISOString(),
  sourceMedia: { videoCodec: 'h264', width: 1920, height: 1080, durationSeconds: 100, audioCodecs: 'aac', pixelFormat: 'yuv420p' },
}

async function serve(page: Page, body: object) {
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: body }))
}

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
}

test('sidecar shows focused RAM and GPU status without server navigation', async ({ page }) => {
  await serve(page, status)
  await page.goto('/sidecar.html')
  await expect(page.getByText('Linux sidecar', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'In memory' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Optimisarr/ }).first()).toHaveAttribute('href', status.serverAddress + '/')
  await expect(page.getByRole('navigation')).toHaveCount(0)
  await expect(page.getByText('Ready for work')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Worker state' })).toContainText('37%')
  await expect(page.getByRole('region', { name: 'Worker state' })).toContainText('62%')
  await page.setViewportSize({ width: 375, height: 812 })
  await noHorizontalScroll(page)
})

test('sidecar shows a job with its stage, progress and decode path', async ({ page }) => {
  await serve(page, { ...status, state: 'Working', jobs: [job] })
  await page.goto('/sidecar.html')
  const card = page.getByRole('article', { name: 'Test clip' })
  await expect(card.getByText('Encoding here')).toBeVisible()
  await expect(card.getByText('12% encoded')).toBeVisible()
  await expect(card.getByText('1920 × 1080')).toBeVisible()
  await expect(card.getByText('GPU decode · qsv')).toBeVisible()
  await expect(card.locator('[aria-current="step"]')).toHaveText('Encode')
  await expect(page.getByRole('region', { name: 'Worker state' })).toContainText('Working')
})

test('a running job keeps the page from reading as idle', async ({ page }) => {
  // A snapshot can say Connected while a job is already listed; the job is the truth.
  await serve(page, { ...status, state: 'Connected', jobs: [job] })
  await page.goto('/sidecar.html')
  await expect(page.getByRole('region', { name: 'Worker state' })).toContainText('Working')
})

test('the page uses the mark the server chose', async ({ page }) => {
  await serve(page, { ...status, brandStyle: 'stellar' })
  await page.goto('/sidecar.html')
  await expect(page.locator('header canvas')).toHaveAttribute('data-brand-style', 'stellar')
  await expect(page.locator('link[rel~="icon"]').first()).toHaveAttribute('href', /\/brand\/stellar\/favicon-/)
})

test('an update from the server is offered with its release notes', async ({ page }) => {
  await serve(page, { ...status, update: { version: '0.2.17', releaseUrl: 'https://github.com/Jellman86/optimisarr/releases/tag/v0.2.17' } })
  await page.goto('/sidecar.html')
  await expect(page.getByText(/Optimisarr 0\.2\.17 is available/)).toBeVisible()
  await expect(page.getByRole('link', { name: 'Release notes' })).toHaveAttribute('href', /releases\/tag\/v0\.2\.17$/)
})

test('failed polling hides stale healthy state and offers retry', async ({ page }) => {
  await page.route('**/api/sidecar/status', route => route.fulfill({ status: 503 }))
  await page.goto('/sidecar.html')
  await expect(page.getByRole('alert')).toContainText('Cannot reach this sidecar')
  await serve(page, status)
  await page.getByRole('button', { name: 'Retry' }).click()
  await expect(page.getByText('Ready for work')).toBeVisible()
})

test('unavailable load readings stay explicit on a narrow screen', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await serve(page, { ...status, metrics: null })
  await page.goto('/sidecar.html')
  const strip = page.getByRole('region', { name: 'Worker state' })
  await expect(strip).toContainText('CPU')
  await expect(strip).toContainText('—')
  await noHorizontalScroll(page)
})

const unpaired = { ...status, state: 'Unpaired', serverAddress: null, pairing: { required: true, configuredServer: null, inProgress: false, problem: null } }

test('an unpaired worker pairs from its own page', async ({ page }) => {
  let paired = false
  let sent: { server: string | null; code: string } | null = null
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: paired ? status : unpaired }))
  await page.route('**/api/sidecar/pair', async route => {
    sent = route.request().postDataJSON()
    paired = true
    await route.fulfill({ json: { paired: true } })
  })
  await page.goto('/sidecar.html')
  await expect(page.getByRole('heading', { name: 'Pair Quark' })).toBeVisible()
  const pair = page.getByRole('button', { name: 'Pair this worker' })
  await expect(pair).toBeDisabled()
  await page.getByLabel('Server address').fill('https://optimisarr.example')
  await page.getByLabel('Pairing code').pressSequentially('12345678')
  await expect(page.getByLabel('Pairing code')).toHaveValue('1234 5678')
  await pair.click()
  await expect(page.getByText('Ready for work')).toBeVisible()
  expect(sent).toEqual({ server: 'https://optimisarr.example', code: '1234 5678' })
})

test('a refused code is explained where it was typed', async ({ page }) => {
  await serve(page, unpaired)
  await page.route('**/api/sidecar/pair', route => route.fulfill({ status: 400, json: { error: 'That pairing code was not accepted.' } }))
  await page.goto('/sidecar.html')
  await page.getByLabel('Server address').fill('https://optimisarr.example')
  await page.getByLabel('Pairing code').fill('00000000')
  await page.getByRole('button', { name: 'Pair this worker' }).click()
  await expect(page.getByRole('alert')).toHaveText('That pairing code was not accepted.')
  await expect(page.getByLabel('Pairing code')).toHaveAttribute('aria-invalid', 'true')
})

test('a server set by the container cannot be edited on the page', async ({ page }) => {
  await serve(page, { ...unpaired, pairing: { ...unpaired.pairing, configuredServer: 'https://configured.example/' } })
  await page.goto('/sidecar.html')
  await expect(page.getByLabel('Server address')).toHaveValue('https://configured.example/')
  await expect(page.getByLabel('Server address')).toHaveAttribute('readonly', '')
  await page.setViewportSize({ width: 375, height: 812 })
  await noHorizontalScroll(page)
})


test('audio work shows a source spectrogram with an honest verification label', async ({ page }) => {
  await serve(page, { ...status, state: 'Working', jobs: [{ ...job, kind: 'Audio', title: 'Audio fixture',
    encoder: 'libopus', outputExtension: 'opus', hardwareDecoder: null, previewRevision: 1,
    sourceMedia: { videoCodec: null, width: null, height: null, durationSeconds: 100, audioCodecs: 'flac', pixelFormat: null } }] })
  await page.route('**/api/sidecar/jobs/42/preview*', route => route.fulfill({ contentType: 'image/svg+xml',
    body: '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="96"><rect width="320" height="96" fill="#102e3f"/></svg>' }))
  await page.goto('/sidecar.html')
  const card = page.getByRole('article', { name: 'Audio fixture' })
  await expect(card.getByRole('img', { name: 'Source audio spectrogram of Audio fixture' })).toBeVisible()
  await expect(card.getByText('Source spectrum', { exact: true })).toBeVisible()
  await expect(card.getByText(/verification runs separately/)).toBeVisible()
  await page.setViewportSize({ width: 375, height: 812 })
  await noHorizontalScroll(page)
})

test('local diagnostics can be exported without a server connection', async ({ page }) => {
  await serve(page, { ...status, state: 'Disconnected' })
  await page.route('**/api/sidecar/diagnostics', route => route.fulfill({
    contentType: 'application/json', headers: { 'content-disposition': 'attachment; filename="optimisarr-sidecar-diagnostics.json"' },
    body: '{"schemaVersion":1,"entries":[]}',
  }))
  await page.goto('/sidecar.html')
  const download = page.waitForEvent('download')
  await page.getByRole('link', { name: 'Export local diagnostics', exact: true }).click()
  expect((await download).suggestedFilename()).toBe('optimisarr-sidecar-diagnostics.json')
})
