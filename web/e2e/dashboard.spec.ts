import { expect, test, type Page, type Route } from '@playwright/test'

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

// Shaped after a live server rather than invented, so the fixtures exercise real proportions:
// a large lifetime tally, a small queue, and a failure count far larger than either.
const STATS = {
  bytesSaved: 1_803_156_582_298,
  originalBytes: 2_489_056_686_785,
  optimisedBytes: 685_900_104_487,
  filesOptimised: 1_885,
  averageSavingPercent: 72.44,
  inQuarantine: 64,
  quarantineReclaimableBytes: 71_454_607_697,
  queued: 14,
  running: 24,
  readyToReplace: 0,
  failed: 144,
  libraries: 4,
  enabledLibraries: 4,
  discoveredFiles: 7_014,
}

const QUEUE_CLEAR = {
  canStart: true,
  blockedReason: null,
  manuallyPaused: false,
  manualPauseMode: 'inactive',
  runningEncodesSuspended: false,
  suspendedEncodeCount: 0,
  pauseFailedEncodeCount: 0,
  runningJobs: 0,
  hardwareAccelerated: false,
  freeDiskBytes: 3_171_405_266_944,
  workRoot: '/work',
  waitingReason: null,
  maxConcurrentJobs: 1,
}

function liveJob(overrides: Record<string, unknown> = {}) {
  return {
    id: 5845,
    mediaFileId: 5845,
    libraryId: 1,
    relativePath: 'TV/Harborlight/S02/Harborlight.S02E07.mkv',
    status: 'Transcoding',
    priority: 0,
    progress: 0.68,
    errorMessage: null,
    enqueueReason: null,
    failureCategory: null,
    ffmpegArguments: null,
    videoEncoder: 'hevc_qsv',
    requestedVideoQuality: 24,
    effectiveVideoQuality: 24,
    videoQualityMode: 'AdaptiveVmaf',
    qualityRetryCount: 0,
    outputSizeBytes: null,
    verificationPassed: null,
    verificationReportJson: null,
    verifiedAt: null,
    enqueuedAt: '2026-09-15T11:00:00Z',
    startedAt: '2026-09-15T11:40:00Z',
    finishedAt: null,
    clearable: false,
    workerName: null,
    remoteStage: null,
    waitingForWorker: false,
    ...overrides,
  }
}

type Fixture = {
  queue?: Record<string, unknown>
  jobs?: unknown[]
  workers?: unknown[]
  workersAvailable?: boolean
  failures?: unknown[]
  stats?: Record<string, unknown>
}

async function mockDashboard(page: Page, fixture: Fixture = {}) {
  const workersAvailable = fixture.workersAvailable ?? false
  const seen: string[] = []
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    seen.push(url.pathname + url.search)
    const path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') {
      return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    }
    if (path === '/api/health') return json(route, { status: 'healthy' })
    if (path === '/api/system/tools') {
      return json(route, { tools: [{ name: 'ffmpeg', required: true, available: true, version: '7.1' }] })
    }
    if (path === '/api/stats') return json(route, { ...STATS, ...fixture.stats })
    if (path === '/api/queue/status') return json(route, { ...QUEUE_CLEAR, ...fixture.queue })
    if (path === '/api/jobs') return json(route, fixture.jobs ?? [])
    if (path === '/api/jobs/failures') return json(route, fixture.failures ?? [])
    if (path === '/api/settings') {
      return json(route, { remoteWorkersAvailable: workersAvailable, remoteWorkersEnabled: workersAvailable })
    }
    if (path === '/api/workers') return json(route, fixture.workers ?? [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
  return seen
}

test('the status bar names the state, and the job list says what is being worked on', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })

  await page.goto('/#/')

  await expect(page.getByText('ENCODING', { exact: true })).toBeVisible()
  await expect(page.getByText('Harborlight.S02E07.mkv')).toBeVisible()
  await expect(page.getByText('hevc_qsv')).toBeVisible()
  await expect(page.getByText('68%')).toBeVisible()
  // The enum name is read out in words rather than printed as PascalCase.
  await expect(page.getByText('Adaptive VMAF')).toBeVisible()
})

test('the dashboard asks only for jobs still being worked on', async ({ page }) => {
  // Regression: the unfiltered list is the entire job history — 1,773 rows on the server this
  // was checked against — and the dashboard re-reads it every fifteen seconds.
  const seen = await mockDashboard(page, { jobs: [] })

  await page.goto('/#/')
  await expect(page.getByText('HOLDING', { exact: true }).or(page.getByText('NOT STARTING', { exact: true }))).toBeVisible()

  expect(seen.some((request) => request === '/api/jobs?live=true')).toBe(true)
  expect(seen.some((request) => request === '/api/jobs')).toBe(false)
})

test('a queue held by playback says so instead of showing an empty page', async ({ page }) => {
  // The defect this dashboard exists to fix: "0 running, 14 queued" used to render identically
  // whether the queue was held, paused, waiting on a window, or wedged. The wording is the
  // server's own, taken from a live instance.
  await mockDashboard(page, {
    queue: { canStart: false, blockedReason: 'Paused while Riker Plex is active (1 stream).' },
  })

  await page.goto('/#/')

  await expect(page.getByText('HOLDING', { exact: true })).toBeVisible()
  await expect(page.getByText('Paused while Riker Plex is active (1 stream).').first()).toBeVisible()
})

test('a shut optimise window is reported as waiting, in the server’s own words', async ({ page }) => {
  const waiting = '1418 job(s) waiting for the TV optimise window (00:00–05:00)'
  await mockDashboard(page, { queue: { waitingReason: waiting } })

  await page.goto('/#/')

  await expect(page.getByText('WAITING', { exact: true })).toBeVisible()
  await expect(page.getByText(waiting).first()).toBeVisible()
})

test('a queue that should be running but is not is called out rather than drawn as idle', async ({ page }) => {
  await mockDashboard(page, { queue: { canStart: false } })

  await page.goto('/#/')

  await expect(page.getByText('NOT STARTING', { exact: true })).toBeVisible()
})

test('an empty queue reads as idle, not as a problem', async ({ page }) => {
  await mockDashboard(page, {
    stats: { queued: 0, running: 0, readyToReplace: 0, inQuarantine: 0, failed: 0 },
  })

  await page.goto('/#/')

  await expect(page.getByText('IDLE', { exact: true })).toBeVisible()
  await expect(page.getByText('Nothing is waiting on a decision from you.')).toBeVisible()
})

test('failures are named by cause rather than counted', async ({ page }) => {
  await mockDashboard(page, {
    failures: [
      { category: 'VmafBelowTarget', description: 'VMAF below target', count: 7, samples: [] },
      { category: 'DecoderCorruption', description: 'Decoder corruption', count: 3, samples: [] },
      { category: 'SourceUnreadable', description: 'Source unreadable', count: 1, samples: [] },
    ],
  })

  await page.goto('/#/')

  await expect(page.getByText('VMAF below target ×7 · Decoder corruption ×3 · Source unreadable ×1')).toBeVisible()
})

test('the fleet lists this server alone when remote workers are switched off', async ({ page }) => {
  await mockDashboard(page, { workersAvailable: false })

  await page.goto('/#/')

  await expect(page.getByText('This server').first()).toBeVisible()
  await expect(page.getByText('No workers paired.')).toBeHidden()
})

test('a paired worker appears beside this server, and its last objection is shown', async ({ page }) => {
  await mockDashboard(page, {
    workersAvailable: true,
    workers: [
      {
        id: 1,
        name: "Scott's MacBook Air",
        operatingSystem: 'macOS',
        architecture: 'arm64',
        protocolVersion: 1,
        sidecarVersion: '0.1.4',
        cpuBusyFraction: 0.41,
        gpuBusyFraction: null,
        loadReportedAt: '2026-09-15T11:59:00Z',
        videoEncoders: ['hevc_videotoolbox'],
        audioEncoders: ['aac'],
        hardwareDecoders: ['videotoolbox'],
        vmaf: { available: true, cuda: false },
        freeScratchBytes: 96_000_000_000,
        maxConcurrency: 2,
        pairedAt: '2026-09-13T09:00:00Z',
        lastSeenAt: '2026-09-15T11:59:00Z',
        revokedAt: null,
        online: true,
        drainRequestedAt: null,
        heldLeases: 1,
        activeJobs: [],
        lastProblem: 'worker does not advertise libopus',
        lastProblemAt: '2026-09-15T10:00:00Z',
      },
    ],
  })

  await page.goto('/#/')

  await expect(page.getByText("Scott's MacBook Air")).toBeVisible()
  await expect(page.getByText('worker does not advertise libopus')).toBeVisible()
  // One held lease is "1 job", not "1 jobs".
  await expect(page.getByText('1 job · 0.1.4')).toBeVisible()
  // A machine that reports no accelerator is drawn as reporting nothing, never as idle.
  await expect(page.getByText('GPU not reported').first()).toBeVisible()
  // This server counts itself in the tally above the list.
  await expect(page.getByText('2 of 2 reporting')).toBeVisible()
})

test('resetting the lifetime total asks first, and the bindable confirm reaches the rail', async ({ page }) => {
  // Guards the two-way binding onto TelemetryRail: a bind: onto an absent prop throws at
  // runtime and blanks the page while `npm run check` stays clean.
  await mockDashboard(page)

  await page.goto('/#/')

  await page.getByRole('button', { name: 'Reset the lifetime space-saved total' }).click()
  await expect(page.getByRole('button', { name: 'Reset', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('button', { name: 'Reset the lifetime space-saved total' })).toBeVisible()
})

test('the sidebar carries live counts so a page need not be opened to see them', async ({ page }) => {
  await mockDashboard(page)

  await page.goto('/#/')

  const sidebar = page.locator('aside')
  await expect(sidebar.getByText('7,014')).toBeVisible()
  await expect(sidebar.getByText('64', { exact: true })).toBeVisible()
})

test('a long in-flight list is capped and says what it is hiding', async ({ page }) => {
  // Real servers carry a dozen or more outstanding jobs; an uncapped list pushes the rest of
  // the page off the screen.
  const many = Array.from({ length: 13 }, (_, index) =>
    liveJob({ id: 6000 + index, relativePath: `TV/Show/S01/Episode.${index}.mkv` }),
  )
  await mockDashboard(page, { queue: { runningJobs: 13 }, jobs: many })

  await page.goto('/#/')

  await expect(page.getByText('Episode.0.mkv')).toBeVisible()
  await expect(page.getByText('Episode.5.mkv')).toBeVisible()
  await expect(page.getByText('Episode.6.mkv')).toBeHidden()
  await expect(page.getByRole('button', { name: '7 more in flight →' })).toBeVisible()
})

test('the application mark is drawn, not fetched, and reports the server state', async ({ page }) => {
  // The mark is a canvas rather than an <img>: it turns while work is running and settles when
  // the queue goes quiet, so the icon answers "is it still going?" on its own.
  await mockDashboard(page, { queue: { runningJobs: 2 }, jobs: [liveJob()] })

  await page.goto('/#/')

  const mark = page.locator('aside canvas').first()
  await expect(mark).toBeVisible()
  // Decorative: every placement sits beside the word "Optimisarr", so the mark must not repeat it.
  await expect(mark).toHaveAttribute('aria-hidden', 'true')

  // Something is actually rasterised onto it, and it keeps its alpha channel.
  const painted = await mark.evaluate((el: HTMLCanvasElement) => {
    const d = el.getContext('2d')!.getImageData(0, 0, el.width, el.height).data
    let lit = 0
    let clear = 0
    for (let i = 0; i < d.length; i += 4) (d[i + 3] > 0 ? lit++ : clear++)
    return { lit, clear }
  })
  expect(painted.lit).toBeGreaterThan(50)
  expect(painted.clear).toBeGreaterThan(painted.lit)
})

test('the collapsed rail keeps a name on the brand button', async ({ page }) => {
  // The wordmark is hidden on the icon rail, and the mark itself is decorative, so the button
  // is the only thing left that can carry the name.
  await mockDashboard(page)

  await page.goto('/#/')

  await expect(page.locator('aside').getByRole('button', { name: 'Dashboard', exact: true }).first()).toBeVisible()
})

test('the favicon is replaced by the drawn mark', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })

  await page.goto('/#/')

  const href = page.locator('link[rel~="icon"]').first()
  await expect(href).toHaveAttribute('href', /^data:image\/png/, { timeout: 10_000 })
})
