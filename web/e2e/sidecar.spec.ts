import { test, expect } from '@playwright/test'

const status = {
  name: 'Quark', state: 'Connected', serverAddress: 'https://optimisarr.example',
  scratchPath: '/work', storage: { kind: 'RAM', freeBytes: 6 * 1024 ** 3, totalBytes: 8 * 1024 ** 3 },
  concurrency: 1, capabilities: { videoEncoders: ['hevc_qsv', 'av1_qsv'], hardwareDecoders: ['qsv'], vmaf: 'Cpu' },
  jobs: [], lastOutcome: null, version: '0.2.16',
  metrics: { cpuPercent: 37.4, gpuPercent: 62.1, gpuEngine: 'Video', sampledAt: new Date().toISOString() },
}

test('sidecar shows focused RAM and GPU status without server navigation', async ({ page }) => {
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: status }))
  await page.goto('/sidecar.html')
  await expect(page.getByRole('heading', { name: 'Quark' })).toBeVisible()
  await expect(page.getByText('Linux sidecar', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'RAM working storage' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open main server' })).toHaveAttribute('href', status.serverAddress + '/')
  await expect(page.getByRole('navigation')).toHaveCount(0)
  await expect(page.getByText('Ready for work')).toBeVisible()
  await page.setViewportSize({ width: 375, height: 812 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('sidecar shows jobs and distinguishes GPU capability from actual job use', async ({ page }) => {
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: {
    ...status, state: 'Working', jobs: [{ jobId: 42, title: 'Test clip', encoder: 'hevc_qsv', stage: 'Encoding', encodedSeconds: 12, sourceBytes: 123456789, outputExtension: 'mkv', hardwareDecoder: 'qsv', previewRevision: 0, sourceMedia: { videoCodec: 'h264', width: 1920, height: 1080, durationSeconds: 100, audioCodecs: 'aac', pixelFormat: 'yuv420p' } }],
  } }))
  await page.goto('/sidecar.html')
  await expect(page.getByText('Test clip')).toBeVisible()
  await expect(page.getByText('Encoding · hevc_qsv')).toBeVisible()
  await expect(page.getByText('12% encoded')).toBeVisible()
  await expect(page.getByText('1920 × 1080')).toBeVisible()
  await expect(page.getByText('37%')).toBeVisible()
  await expect(page.getByText('62%')).toBeVisible()
  await expect(page.getByText('GPU decode · qsv')).toBeVisible()
})

test('failed polling hides stale healthy state and offers retry', async ({ page }) => {
  await page.route('**/api/sidecar/status', route => route.fulfill({ status: 503 }))
  await page.goto('/sidecar.html')
  await expect(page.getByRole('alert')).toContainText('Cannot reach this sidecar')
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: status }))
  await page.getByRole('button', { name: 'Retry' }).click()
  await expect(page.getByText('Ready for work')).toBeVisible()
})

test('unavailable load readings stay explicit on a narrow screen', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: { ...status, metrics: null } }))
  await page.goto('/sidecar.html')
  await expect(page.getByText('No active GPU sample')).toBeVisible()
  await expect(page.getByText('Waiting for a CPU sample')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})
