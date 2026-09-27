import { test, expect } from '@playwright/test'

const status = {
  name: 'Quark', state: 'Connected', serverAddress: 'https://optimisarr.example',
  scratchPath: '/work', storage: { kind: 'RAM', freeBytes: 6 * 1024 ** 3, totalBytes: 8 * 1024 ** 3 },
  concurrency: 1, capabilities: { videoEncoders: ['hevc_qsv', 'av1_qsv'], hardwareDecoders: ['qsv'], vmaf: 'Cpu' },
  jobs: [], lastOutcome: null, version: '0.2.16',
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
    ...status, state: 'Working', jobs: [{ jobId: 42, title: 'Test clip', encoder: 'hevc_qsv', stage: 'Encoding', encodedSeconds: 12 }],
  } }))
  await page.goto('/sidecar.html')
  await expect(page.getByText('Test clip')).toBeVisible()
  await expect(page.getByText('Encoding · hevc_qsv')).toBeVisible()
  await expect(page.getByText('12s encoded')).toBeVisible()
})

test('failed polling hides stale healthy state and offers retry', async ({ page }) => {
  await page.route('**/api/sidecar/status', route => route.fulfill({ status: 503 }))
  await page.goto('/sidecar.html')
  await expect(page.getByRole('alert')).toContainText('Cannot reach this sidecar')
  await page.route('**/api/sidecar/status', route => route.fulfill({ json: status }))
  await page.getByRole('button', { name: 'Retry' }).click()
  await expect(page.getByText('Ready for work')).toBeVisible()
})
