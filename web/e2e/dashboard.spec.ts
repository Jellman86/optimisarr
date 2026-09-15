import { expect, test, type Page, type Route } from '@playwright/test'

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

const STATS = {
  bytesSaved: 4_310_000_000_000,
  originalBytes: 10_400_000_000_000,
  optimisedBytes: 6_090_000_000_000,
  filesOptimised: 2_184,
  averageSavingPercent: 41,
  inQuarantine: 24,
  quarantineReclaimableBytes: 96_000_000_000,
  queued: 1_418,
  running: 0,
  readyToReplace: 6,
  failed: 11,
  libraries: 4,
  enabledLibraries: 4,
  discoveredFiles: 11_240,
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
  freeDiskBytes: 214_000_000_000,
  workRoot: '/work',
  waitingReason: null,
  maxConcurrentJobs: 4,
}

function runningJob(overrides: Record<string, unknown> = {}) {
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
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
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
    if (path === '/api/jobs' && route.request().method() === 'GET') return json(route, fixture.jobs ?? [])
    if (path === '/api/jobs/failures') return json(route, fixture.failures ?? [])
    if (path === '/api/settings') {
      return json(route, { remoteWorkersAvailable: workersAvailable, remoteWorkersEnabled: workersAvailable })
    }
    if (path === '/api/workers') return json(route, fixture.workers ?? [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

test('the status bar names the state and the queue depth while work is running', async ({ page }) => {
  await mockDashboard(page, {
    queue: { runningJobs: 1 },
    jobs: [runningJob()],
  })

  await page.goto('/#/')

  await expect(page.getByText('ENCODING', { exact: true })).toBeVisible()
  await expect(page.getByText('Harborlight.S02E07.mkv')).toBeVisible()
  await expect(page.getByText('hevc_qsv')).toBeVisible()
  await expect(page.getByText('68%')).toBeVisible()
})

test('a queue held by playback says so instead of showing an empty page', async ({ page }) => {
  // The defect this dashboard exists to fix: "0 running, 1,418 queued" used to render
  // identically whether the queue was held, paused, waiting on a window, or wedged.
  await mockDashboard(page, {
    queue: { canStart: false, blockedReason: 'Plex has 2 active streams', runningJobs: 0 },
  })

  await page.goto('/#/')

  await expect(page.getByText('HOLDING', { exact: true })).toBeVisible()
  await expect(page.getByText('Plex has 2 active streams').first()).toBeVisible()
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
  await mockDashboard(page, { stats: { queued: 0, readyToReplace: 0, inQuarantine: 0, failed: 0 } })

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
  // A machine that reports no accelerator is drawn as reporting nothing, never as idle.
  await expect(page.getByText('GPU not reported').first()).toBeVisible()
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
