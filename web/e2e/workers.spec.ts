import { expect, test, type Page, type Route } from '@playwright/test'

// The Workers tab exists only while the preview flag is present and the switch is on; the
// server says both through /api/settings, so the mock says both.
const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  hdrToneMapMode: 'Software',
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 14,
  remoteWorkersEnabled: true,
  remoteWorkersAvailable: true,
}

const now = Date.now()
const iso = (msAgo: number) => new Date(now - msAgo).toISOString()

const workers = [
  {
    id: 1, name: 'Mac Studio', operatingSystem: 'macos', architecture: 'arm64', protocolVersion: 2,
    videoEncoders: ['hevc_videotoolbox', 'h264_videotoolbox', 'libx265', 'libsvtav1'],
    hardwareDecoders: ['videotoolbox'], vmaf: 'Cpu', freeScratchBytes: 118 * 1024 ** 3, maxConcurrency: 2,
    pairedAt: iso(86_400_000 * 12), lastSeenAt: iso(4_000), revokedAt: null, online: true,
    drainRequestedAt: null, heldLeases: 1,
    activeJobs: [{ jobId: 41, relativePath: 'Chicago Fire/Season 14/Chicago Fire S14E12 1080p AMZN WEB-DL.mkv', stage: 'Encoding', progress: 0.62 }],
    lastProblem: null, lastProblemAt: null,
  },
  {
    id: 2, name: 'MacBook Air', operatingSystem: 'macos', architecture: 'arm64', protocolVersion: 2,
    videoEncoders: ['hevc_videotoolbox', 'libx265'], hardwareDecoders: [], vmaf: 'Cpu',
    freeScratchBytes: 41 * 1024 ** 3, maxConcurrency: 1,
    pairedAt: iso(86_400_000 * 3), lastSeenAt: iso(12_000), revokedAt: null, online: true,
    drainRequestedAt: iso(180_000), heldLeases: 0, activeJobs: [],
    lastProblem: 'Its candidate for Slow Horses S05E01.mkv was encoded from a different source and was refused.',
    lastProblemAt: iso(3_600_000 * 3),
  },
  {
    id: 3, name: 'Office PC', operatingSystem: 'windows', architecture: 'x64', protocolVersion: 1,
    videoEncoders: ['hevc_nvenc'], hardwareDecoders: ['cuda'], vmaf: 'None',
    freeScratchBytes: 0, maxConcurrency: 1,
    pairedAt: iso(86_400_000 * 40), lastSeenAt: iso(86_400_000 * 2), revokedAt: null, online: false,
    drainRequestedAt: null, heldLeases: 0, activeJobs: [],
    lastProblem: 'Its lease on The Bear S04E02.mkv lapsed after 2 minutes of silence; the job went back to the queue.',
    lastProblemAt: iso(86_400_000 * 2),
  },
]

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockWorkers(page: Page, rows = workers) {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    const method = route.request().method()
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/health') return json(route, { status: 'healthy', service: 'optimisarr', version: 'test' })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/workers/pairing-code') return route.fulfill({ status: 204 })
    if (path === '/api/settings/cleanup') return json(route, {
      retentionDays: 14, dryRunMode: true, failedOutputCount: 0, failedOutputBytes: 0,
      quarantinedOriginalCount: 0, quarantinedOriginalBytes: 0, planToken: 'test', totalCount: 0, totalBytes: 0,
    })
    if (path === '/api/system/tools') return json(route, { tools: [] })
    if (path === '/api/system/hardware') return json(route, { hardware: { hardwareAccelerators: [], encoders: [], nvidiaRuntimeAvailable: false, driDeviceAvailable: false, error: null } })
    if (path === '/api/workers') return json(route, rows)
    const drain = path.match(/^\/api\/workers\/(\d+)\/drain$/)
    if (drain) {
      const worker = rows.find((w) => w.id === Number(drain[1]))!
      const drained = { ...worker, drainRequestedAt: method === 'POST' ? new Date().toISOString() : null }
      rows = rows.map((w) => (w.id === worker.id ? drained : w))
      return json(route, drained)
    }
    if (path === '/api/activity-watchers' || path === '/api/arr-connections' || path === '/api/notification-targets') {
      return json(route, [])
    }
    return json(route, {})
  })
}

test('each paired sidecar is a card that says what it can do, what it is doing, and what went wrong', async ({ page }) => {
  await mockWorkers(page)
  await page.goto('/#/settings')
  await page.getByRole('tab', { name: 'Workers' }).click()

  const cards = page.locator('[data-testid="worker-card"]')
  await expect(cards).toHaveCount(3)

  const studio = cards.nth(0)
  await expect(studio).toContainText('Mac Studio')
  await expect(studio.getByText('Online')).toBeVisible()
  await expect(studio).toContainText('hevc_videotoolbox')
  await expect(studio).toContainText('Chicago Fire S14E12 1080p AMZN WEB-DL.mkv')
  await expect(studio).toContainText('encoding 62%')
  await expect(studio).toContainText('1 of 2 jobs')
  await expect(studio.getByRole('button', { name: 'Drain after this job' })).toBeVisible()

  const air = cards.nth(1)
  // Drained, not draining: nothing is held any more, so the drain has nothing left to wait on.
  await expect(air.getByText('Drained')).toBeVisible()
  await expect(air).toContainText('finished its last job and takes no more')
  await expect(air).toContainText('encoded from a different source')
  await expect(air.getByRole('button', { name: 'Resume taking work' })).toBeVisible()

  const office = cards.nth(2)
  await expect(office.getByText('Offline')).toBeVisible()
  await expect(office).toContainText('2 days ago')
  await expect(office).toContainText('lapsed after 2 minutes of silence')
  await expect(office.getByRole('button', { name: 'Stop taking work' })).toBeVisible()
})

test('drain and resume act on one card and show what the server recorded', async ({ page }) => {
  await mockWorkers(page)
  await page.goto('/#/settings')
  await page.getByRole('tab', { name: 'Workers' }).click()

  const studio = page.locator('[data-testid="worker-card"]').nth(0)
  await studio.getByRole('button', { name: 'Drain after this job' }).click()
  // Still holding one job, so it is draining rather than drained, and only resume is offered.
  await expect(studio.getByText('Draining')).toBeVisible()
  await expect(studio.getByRole('button', { name: 'Resume taking work' })).toBeVisible()

  await studio.getByRole('button', { name: 'Resume taking work' }).click()
  await expect(studio.getByText('Online')).toBeVisible()
  await expect(studio.getByRole('button', { name: 'Drain after this job' })).toBeVisible()
})

test('a sidecar that redeems the code appears on the page without a reload', async ({ page }) => {
  // The list and the code both live server-side; the page has to keep asking while a code is
  // showing, because the only thing that ends a pairing is the sidecar redeeming it elsewhere.
  let rows: typeof workers = []
  let code: { code: string; expiresUtc: string; attemptsRemaining: number } | null = null
  await mockWorkers(page, rows)
  await page.route((url) => url.pathname === '/api/workers', (route) => json(route, rows))
  await page.route((url) => url.pathname === '/api/workers/pairing-code', (route) => {
    const method = route.request().method()
    if (method === 'POST') {
      code = { code: '82397571', expiresUtc: new Date(Date.now() + 300_000).toISOString(), attemptsRemaining: 5 }
      return json(route, code)
    }
    if (method === 'DELETE') {
      code = null
      return route.fulfill({ status: 204 })
    }
    return code ? json(route, code) : route.fulfill({ status: 204 })
  })

  await page.goto('/#/settings')
  await page.getByRole('tab', { name: 'Workers' }).click()
  await expect(page.getByText('No sidecars are paired.')).toBeVisible()

  await page.getByRole('button', { name: 'Pair a sidecar' }).click()
  await expect(page.getByText('8239 7571')).toBeVisible()

  // The sidecar redeems the code: the server forgets it and lists the new worker.
  code = null
  const stamp = new Date().toISOString()
  rows = [{ ...workers[1], id: 7, name: 'Scott’s MacBook Air', pairedAt: stamp, lastSeenAt: stamp, drainRequestedAt: null, lastProblem: null, lastProblemAt: null }]

  const card = page.locator('[data-testid="worker-card"]')
  await expect(card).toHaveCount(1, { timeout: 10_000 })
  await expect(card).toContainText('Scott’s MacBook Air')
  await expect(page.getByText('8239 7571')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Pair a sidecar' })).toBeVisible()
})
