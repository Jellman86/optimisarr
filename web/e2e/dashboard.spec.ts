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
  results?: unknown[]
  daily?: unknown[]
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
    if (path === '/api/results') return json(route, fixture.results ?? [])
    if (path === '/api/results/daily') return json(route, fixture.daily ?? [])
    if (path === '/api/queue/pause' && route.request().method() === 'POST') {
      return json(route, { ...QUEUE_CLEAR, ...fixture.queue, manuallyPaused: true, manualPauseMode: 'suspended', canStart: false, blockedReason: 'Paused manually.' })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
  return seen
}

test('the status bar names the state, and the job list says what is being worked on', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })

  await page.goto('/#/')

  // Scoped to main so the test checks the Dashboard's own in-flight list directly.
  const main = page.locator('main')
  await expect(page.locator('.status-strip-label')).toHaveText('Encoding')
  await expect(main.getByText('Harborlight S02E07', { exact: true })).toBeVisible()
  await expect(main.getByText('hevc_qsv')).toBeVisible()
  await expect(main.getByText('68%')).toBeVisible()
  // The enum name is read out in words rather than printed as PascalCase.
  await expect(main.getByText('Adaptive VMAF')).toBeVisible()
})

test('the sidebar shows what is encoding on pages without a live job list', async ({ page }) => {
  // The mark already turns while work runs; the card beneath the navigation says what the work
  // is, so a reader on Settings or Libraries does not have to go to the Queue to find out.
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })

  await page.goto('/#/settings')

  const card = page.locator('aside').getByRole('link', { name: /Now encoding/ })
  await expect(card).toBeVisible()
  await expect(card).toContainText('Harborlight.S02E07')
  await expect(card).toContainText('hevc_qsv')
  await expect(card).toContainText('68%')
  await expect(card.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '68')
})

test('an idle server carries no encoding card', async ({ page }) => {
  await mockDashboard(page)

  await page.goto('/#/')

  await expect(page.locator('aside').getByText('Now encoding')).toBeHidden()
})

test('the sidebar does not call an unfinished encode complete', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob({ progress: 0.9999 })] })
  await page.goto('/#/settings')
  const card = page.locator('aside').getByRole('link', { name: /Now encoding/ })
  await expect(card.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '99')
  await expect(card).toContainText('99%')
})

test('a candidate a worker has returned is shown being checked here, not encoding on the worker', async ({ page }) => {
  // Regression: the sidebar said "Now encoding · Scott's MacBook Air" while the Mac reported itself
  // idle. The job keeps the worker's name after delivery; the work is this server's now.
  await mockDashboard(page, {
    jobs: [liveJob({ status: 'AwaitingVerification', workerName: "Scott's MacBook Air", videoEncoder: 'hevc_videotoolbox', progress: 0 })],
  })
  await page.goto('/#/settings')

  const card = page.locator('aside').getByRole('link', { name: /Waiting to verify/ })
  await expect(card).toContainText("On this server, from Scott's MacBook Air")
  await expect(page.locator('aside').getByText('Now encoding')).toBeHidden()
})

test('an encode on a worker is placed on that worker', async ({ page }) => {
  await mockDashboard(page, {
    jobs: [liveJob({ status: 'Leased', remoteStage: 'Encoding', workerName: 'PICARD', videoEncoder: 'hevc_nvenc' })],
  })
  await page.goto('/#/settings')

  const card = page.locator('aside').getByRole('link', { name: /Now encoding/ })
  await expect(card).toContainText('On PICARD')
})

test('the dashboard asks only for jobs still being worked on', async ({ page }) => {
  // Regression: the unfiltered list is the entire job history — 1,773 rows on the server this
  // was checked against — and the dashboard re-reads it every fifteen seconds.
  const seen = await mockDashboard(page, { jobs: [] })

  await page.goto('/#/')
  await expect(page.getByText('Holding', { exact: true }).or(page.getByText('Nothing running', { exact: true }))).toBeVisible()

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

  await expect(page.getByText('Holding', { exact: true })).toBeVisible()
  await expect(page.getByText('Paused while Riker Plex is active (1 stream).').first()).toBeVisible()
})

test('a queue held by playback names what is playing and who is watching', async ({ page }) => {
  const hold = { watcher: 'Riker Plex', year: null, artist: null, paused: false }
  await mockDashboard(page, {
    queue: {
      canStart: false, blockedReason: 'Paused while Riker Plex is active (2 streams).',
      playbackHolds: [
        { ...hold, kind: 'Episode', title: 'Pilot', series: 'Example Show', season: 2, episode: 5, user: 'alex', device: 'Living Room TV' },
        { ...hold, kind: 'Movie', title: 'Example Film', series: null, season: null, episode: null, year: 1999, user: null, device: null, paused: true },
      ],
    },
  })

  await page.goto('/#/')

  // The strip has one line: the first playback and who, then how many more; every playback is in its tooltip.
  const reason = page.locator('.status-strip-reason')
  await expect(reason).toHaveText('Playback (2): Example Show · S2E5 · Pilot · alex +1 more')
  await expect(reason).toHaveAttribute('href', '#/queue')
  await reason.focus()
  await expect(reason).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/#\/queue$/)
  await expect(reason).toHaveAttribute('title', 'Riker Plex · Example Show · S2E5 · Pilot · alex on Living Room TV\nRiker Plex · Example Film (1999) (paused)')
})

test('the status strip reads as one tidy block: state over its reason, beside the artwork, without shouting', async ({ page }) => {
  await mockDashboard(page, {
    queue: {
      canStart: false, blockedReason: 'Paused while Riker Plex is active (1 stream).',
      playbackHolds: [{ watcher: 'Riker Plex', year: null, artist: null, paused: false, kind: 'Episode', title: 'Pilot',
        series: 'Example Show', season: 2, episode: 5, user: 'alex', device: 'Living Room TV', artworkUrl: '/api/artwork/1' }],
    },
  })
  await page.goto('/#/')
  const strip = page.locator('.status-strip')
  const state = strip.getByText('Holding', { exact: true })
  const reason = strip.locator('.status-strip-reason')
  await expect(state).toBeVisible()
  // The reason sits directly under the state, both beside the artwork, so the strip has one visual block.
  const [stateBox, reasonBox] = [await state.boundingBox(), await reason.boundingBox()]
  expect(reasonBox!.y).toBeGreaterThan(stateBox!.y + stateBox!.height - 1)
  expect(Math.abs(reasonBox!.x - stateBox!.x)).toBeLessThan(2)
  // A playback link reads as text until hovered or focused.
  await expect(reason).toHaveCSS('text-decoration-line', 'none')
  await reason.hover()
  await expect(reason).toHaveCSS('text-decoration-line', 'underline')
  // The figures share the block's vertical centre.
  const block = await strip.locator('.status-strip-state').boundingBox()
  const fact = await strip.locator('.status-strip-fact').first().boundingBox()
  expect(Math.abs((block!.y + block!.height / 2) - (fact!.y + fact!.height / 2))).toBeLessThan(3)
})

test('a shut optimise window is reported as waiting, in the server’s own words', async ({ page }) => {
  const waiting = '1418 job(s) waiting for the TV optimise window (00:00–05:00)'
  await mockDashboard(page, { queue: { waitingReason: waiting } })

  await page.goto('/#/')

  await expect(page.getByText('Waiting', { exact: true })).toBeVisible()
  await expect(page.getByText(waiting).first()).toBeVisible()
})

test('a queue that should be running but is not is called out rather than drawn as idle', async ({ page }) => {
  await mockDashboard(page, { queue: { canStart: false } })

  await page.goto('/#/')

  await expect(page.getByText('Nothing running', { exact: true })).toBeVisible()
})

test('a worker encoding while this server is held reads as work on workers, not a stall', async ({ page }) => {
  // Regression: a sidecar was encoding while playback held the server, and the bar said the
  // queue was not starting. The hold is still shown, as this server's reason.
  await mockDashboard(page, {
    queue: {
      canStart: false,
      blockedReason: 'Paused while Riker Plex is active (1 stream).',
      workloadLanes: [
        { lane: 'Video', active: 0, capacity: 1, waiting: 0, reason: null },
        { lane: 'Workers', active: 1, capacity: 2, waiting: 0, reason: null },
      ],
    },
  })

  await page.goto('/#/')

  const strip = page.getByRole('region', { name: 'State' })
  await expect(strip.locator('.status-strip-label')).toHaveText('Encoding on workers')
  await expect(strip.getByText('This server: Paused while Riker Plex is active (1 stream).')).toBeVisible()
  await expect(strip.getByText('1 / 2', { exact: true })).toBeVisible()
})

test('an empty queue reads as idle, not as a problem', async ({ page }) => {
  await mockDashboard(page, {
    stats: { queued: 0, running: 0, readyToReplace: 0, inQuarantine: 0, failed: 0 },
  })

  await page.goto('/#/')

  await expect(page.getByText('Idle', { exact: true })).toBeVisible()
  await expect(page.getByText('Nothing is waiting on a decision from you.')).toBeVisible()
})

test('failures are named by cause rather than counted', async ({ page }) => {
  await mockDashboard(page, {
    failures: [
      { category: 'SizeSaving', description: 'The attempt failed the configured size rule.', count: 7, samples: [] },
      { category: 'Verification', description: 'A verification gate rejected the output.', count: 3, samples: [] },
      { category: 'SourceUnreadable', description: 'Source unreadable', count: 1, samples: [] },
    ],
  })

  await page.goto('/#/')

  // Known categories use the app's short wording; one the app does not know keeps the server's.
  await expect(page.getByText('7 not small enough · 3 failed a check · 1 Source unreadable')).toBeVisible()
  await page.getByRole('button', { name: 'Inspect' }).click()
  await expect(page).toHaveURL(/#\/queue\/failures$/)
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

test('only the entries that need a decision carry a figure', async ({ page }) => {
  // A count on every row is seven numbers competing and five that never change. The queue and
  // quarantine can ask something of you; the rest are just places to go.
  await mockDashboard(page)

  await page.goto('/#/')

  const sidebar = page.locator('aside')
  await expect(sidebar.getByText('64', { exact: true })).toBeVisible()
  await expect(sidebar.getByText('14', { exact: true })).toBeVisible()
  // The inventory count is deliberately absent.
  await expect(sidebar.getByText('7,014')).toBeHidden()
})

test('a figure that is zero is absent rather than shown as nothing to do', async ({ page }) => {
  await mockDashboard(page, { stats: { queued: 0, inQuarantine: 0 } })

  await page.goto('/#/')

  const sidebar = page.locator('aside')
  await expect(sidebar.getByRole('button', { name: /Queue/ })).toBeVisible()
  await expect(sidebar.getByText('0', { exact: true })).toBeHidden()
})

test('a long in-flight list is capped and says what it is hiding', async ({ page }) => {
  // Real servers carry a dozen or more outstanding jobs; an uncapped list pushes the rest of
  // the page off the screen.
  const many = Array.from({ length: 13 }, (_, index) =>
    liveJob({ id: 6000 + index, relativePath: `TV/Show/S01/Episode.${index}.mkv` }),
  )
  await mockDashboard(page, { queue: { runningJobs: 13 }, jobs: many })

  await page.goto('/#/')

  // Rows read as titles: the extension and scene dots are dropped, the full path stays in the tooltip.
  await expect(page.getByText('Episode 0', { exact: true })).toBeVisible()
  await expect(page.getByText('Episode 5', { exact: true })).toBeVisible()
  await expect(page.getByText('Episode 6', { exact: true })).toBeHidden()
  await expect(page.getByRole('button', { name: '7 more in flight →' })).toBeVisible()
})

test('the application mark keeps transparency and reports the server state', async ({ page }) => {
  // A transparent sliced cube turns while work runs, without an opaque square in the rail.
  await mockDashboard(page, { queue: { runningJobs: 2 }, jobs: [liveJob()] })

  await page.goto('/#/')

  const mark = page.locator('aside canvas').first()
  await expect(mark).toBeVisible()
  // Decorative: every placement sits beside the word "Optimisarr", so the mark must not repeat it.
  await expect(mark).toHaveAttribute('aria-hidden', 'true')

  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  // Something is actually rasterised onto it, and it keeps its alpha channel.
  const painted = await mark.evaluate((el: HTMLCanvasElement) => {
    const d = el.getContext('2d')!.getImageData(0, 0, el.width, el.height).data
    let lit = 0
    let clear = 0
    for (let i = 0; i < d.length; i += 4) (d[i + 3] > 0 ? lit++ : clear++)
    const border = []
    for (let i = 0; i < el.width; i++) {
      border.push(d[i * 4 + 3], d[((el.height - 1) * el.width + i) * 4 + 3])
      border.push(d[(i * el.width) * 4 + 3], d[(i * el.width + el.width - 1) * 4 + 3])
    }
    return { lit, clear, border, area: el.width * el.height }
  })
  expect(painted.lit).toBeGreaterThan(50)
  expect(painted.border.every(alpha => alpha === 0)).toBe(true)
  expect(painted.clear).toBeGreaterThan(painted.area * 0.3)
})

test('the collapsed rail keeps a name on the brand button', async ({ page }) => {
  // The wordmark is hidden on the icon rail, and the mark itself is decorative, so the button
  // is the only thing left that can carry the name.
  await mockDashboard(page)

  await page.goto('/#/')

  await expect(page.locator('aside').getByRole('button', { name: 'Dashboard', exact: true }).first()).toBeVisible()
})

test('the favicon reports precession activity with an excited still', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })

  await page.goto('/#/')

  const href = page.locator('link[rel~="icon"]').first()
  await expect(href).toHaveAttribute('href', /\/brand\/favicon-(dark|light)-excited\.png$/, { timeout: 10_000 })
})

test('the cube has smooth edges and moving illumination when idle', async ({ page }) => {
  await mockDashboard(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-state', 'steady')
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => {
    const pixels = el.getContext('2d')!.getImageData(0, 0, el.width, el.height).data
    let partial = 0
    for (let i = 3; i < pixels.length; i += 4) if (pixels[i] > 0 && pixels[i] < 255) partial++
    return partial
  })).toBeGreaterThan(100)
  expect(await mark.evaluate((el: HTMLCanvasElement) => el.width / el.getBoundingClientRect().width)).toBeGreaterThanOrEqual(2)
  expect(await mark.evaluate(el => getComputedStyle(el).imageRendering)).toBe('auto')
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  const still = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(still)
})

test('remote work animates the slices; reduced motion holds a twisted still', async ({ page }) => {
  await mockDashboard(page, { jobs: [liveJob({ status: 'Leased', workerName: 'Mac mini' })] })
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  const before = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(before)
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  const held = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await page.waitForTimeout(400)
  expect(await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).toBe(held)
})

async function mockJobsHub(page: Page) {
  let notify = () => {}
  let handshake = () => {}
  const ready = new Promise<void>(resolve => { handshake = resolve })
  await page.route('**/hubs/jobs/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'brand-test', connectionToken: 'brand-test',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }))
  await page.routeWebSocket('**/hubs/jobs?*', socket => {
    socket.onMessage(message => {
      if (!String(message).includes('"protocol"')) return
      socket.send('{}\u001e')
      handshake()
    })
    notify = () => socket.send(JSON.stringify({ type: 1, target: 'jobsChanged', arguments: [] }) + '\u001e')
  })
  // Events sent before the handshake completes are dropped, so a test can wait for `ready`.
  return Object.assign(() => notify(), { ready })
}

test('a job event refreshes the dashboard list before its next poll', async ({ page }) => {
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob()] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const list = page.locator('main').getByRole('region', { name: 'In flight' })
  await expect(list.getByText('Harborlight S02E07', { exact: true })).toBeVisible()
  await expect(page.locator('aside canvas').first()).toHaveAttribute('data-light-state', 'excited')

  fixture.jobs = [liveJob({ id: 5846, mediaFileId: 5846, relativePath: 'New feature.mkv' })]
  await notify.ready
  notify()
  await expect(list.getByText('New feature', { exact: true })).toBeVisible()
  await expect(list.getByText('Harborlight S02E07', { exact: true })).toHaveCount(0)
})

test('a burst of job events folds into one follow-up dashboard refresh', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  const held: Route[] = []
  await page.route('**/api/results/daily*', route => { held.push(route) })
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  await expect.poll(() => held.length).toBe(1)
  await notify.ready

  for (let event = 0; event < 4; event++) notify()
  await page.waitForTimeout(300)
  expect(held.length).toBe(1)

  await json(held[0], [])
  await expect.poll(() => held.length).toBe(2)
  await json(held[1], [])
  await page.waitForTimeout(300)
  expect(held.length).toBe(2)
})

test('job events change the cube state while suspended work keeps moving illumination', async ({ page }) => {
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob()] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  fixture.queue = { runningJobs: 1, suspendedEncodeCount: 1, runningEncodesSuspended: true }
  await notify.ready
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'steady')
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  await page.waitForTimeout(550)
  const paused = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await page.waitForTimeout(200)
  expect(await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(paused)
  fixture.queue = { runningJobs: 1 }
  notify()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  fixture.queue = { runningJobs: 0 }
  fixture.jobs = []
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'steady')
  await expect(page.locator('link[rel~="icon"]').first()).toHaveAttribute('href', /\/brand\/favicon-(dark|light)-steady\.png$/)
})

test('unavailable precession graphics leaves a complete still and the app usable', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('optimisarr.brand', 'precession'))
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  await page.addInitScript(() => {
    const getContext = HTMLCanvasElement.prototype.getContext
    HTMLCanvasElement.prototype.getContext = function (...args: Parameters<typeof getContext>) {
      if (String(args[0]).startsWith('webgl')) return null
      return getContext.apply(this, args)
    } as typeof getContext
  })
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  expect(await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL().length)).toBeGreaterThan(1000)
  await expect(page.locator('.status-strip-label')).toHaveText('Encoding')
})

test('reduced-motion sessions never load graphics code', async ({ page }) => {
  const graphics: string[] = []
  page.on('request', request => { if (/(brand-(renderer|geometry|shaders)|stellar-(renderer|sky))/.test(request.url())) graphics.push(request.url()) })
  const fixture: Fixture = {}
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  await expect(page.locator('aside canvas').first()).toHaveAttribute('data-light-state', 'steady')
  await page.emulateMedia({ reducedMotion: 'reduce' })
  fixture.queue = { runningJobs: 1 }
  fixture.jobs = [liveJob()]
  await notify.ready
  notify()
  await expect(page.locator('aside canvas').first()).toHaveAttribute('data-light-state', 'excited')
  expect(graphics).toEqual([])
})

test('the icon stops scheduling paints offscreen and resumes a slow drift when idle', async ({ page }) => {
  await page.addInitScript(() => {
    const target = window as Window & { brandPaints: number }
    target.brandPaints = 0
    const clear = CanvasRenderingContext2D.prototype.clearRect
    CanvasRenderingContext2D.prototype.clearRect = function (...args) {
      if (this.canvas.closest('aside')) target.brandPaints++
      return clear.apply(this, args)
    }
  })
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob()] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  const paints = () => page.evaluate(() => (window as Window & { brandPaints: number }).brandPaints)
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  const moving = await paints()
  await expect.poll(paints).toBeGreaterThan(moving)
  await page.locator('aside').evaluate(el => { el.style.transform = 'translateX(-2000px)' })
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  const hidden = await paints()
  await page.waitForTimeout(250)
  expect(await paints()).toBe(hidden)
  await page.locator('aside').evaluate(el => { el.style.transform = '' })
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  fixture.queue = { runningJobs: 0 }
  fixture.jobs = []
  await notify.ready
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'steady')
  await page.waitForTimeout(550)
  const idle = await paints()
  await page.waitForTimeout(250)
  expect(await paints()).toBeGreaterThan(idle)
})

test('the desktop sidebar has breathing room above and below in both widths', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await mockDashboard(page)
  await page.goto('/#/')
  const rail = page.locator('aside')
  for (const collapsed of [false, true]) {
    if (collapsed) await page.getByRole('button', { name: 'Collapse sidebar', exact: true }).click()
    const box = await rail.boundingBox()
    expect(box!.y).toBeGreaterThanOrEqual(16)
    expect(900 - box!.y - box!.height).toBeGreaterThanOrEqual(16)
  }
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole('button', { name: 'Open menu', exact: true }).click()
  const drawer = await rail.boundingBox()
  expect(drawer!.y).toBe(0)
  expect(drawer!.height).toBe(812)
})

test('idle geometry stays fixed and work/theme changes reuse the same meshes', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('optimisarr.brand', 'precession'))
  await page.addInitScript(() => {
    const target = window as Window & { brandGraphics: { buffers: number; angle: number; light: number[] } }
    target.brandGraphics = { buffers: 0, angle: 0, light: [] }
    const prototype = WebGLRenderingContext.prototype
    const createBuffer = prototype.createBuffer
    prototype.createBuffer = function () {
      target.brandGraphics.buffers++
      return createBuffer.call(this)
    }
    const names = new Map<WebGLUniformLocation, string>()
    const location = prototype.getUniformLocation
    prototype.getUniformLocation = function (program, name) {
      const result = location.call(this, program, name)
      if (result) names.set(result, name)
      return result
    }
    const scalar = prototype.uniform1f
    prototype.uniform1f = function (location, value) {
      if (location && names.get(location) === 'angle') target.brandGraphics.angle = value
      return scalar.call(this, location, value)
    }
    const vector = prototype.uniform3fv
    prototype.uniform3fv = function (location, value) {
      if (location && names.get(location) === 'keyLight') target.brandGraphics.light = Array.from(value)
      return vector.call(this, location, value)
    }
  })
  const fixture: Fixture = {}
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  const graphics = () => page.evaluate(() => (window as Window & { brandGraphics: { buffers: number; angle: number; light: number[] } }).brandGraphics)
  const resting = await graphics()
  await page.waitForTimeout(500)
  expect((await graphics()).angle).toBe(0)
  expect((await graphics()).light).not.toEqual(resting.light)
  fixture.queue = { runningJobs: 1 }
  fixture.jobs = [liveJob()]
  await notify.ready
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  await expect.poll(async () => (await graphics()).angle, { timeout: 15_000 }).toBeGreaterThan(.05)
  await page.getByRole('button', { name: 'Toggle theme', exact: true }).click()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  fixture.queue = { runningJobs: 0 }
  fixture.jobs = []
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'steady')
  await page.waitForTimeout(500)
  expect((await graphics()).buffers).toBe(resting.buffers)
})

test('theme changes update reduced-motion stills and the favicon', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockDashboard(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  const favicon = page.locator('link[rel~="icon"]').first()
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  await expect(favicon).toHaveAttribute('href', '/brand/favicon-dark-steady.png')
  const before = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await page.getByRole('button', { name: 'Toggle theme', exact: true }).click()
  await expect(favicon).toHaveAttribute('href', '/brand/favicon-light-steady.png')
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(before)
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
})

test('a working sidebar keeps its footer reachable in a short window', async ({ page }) => {
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  await page.setViewportSize({ width: 1280, height: 600 })
  // Settings, because the rail's encoding card only shows on pages that do not show the work themselves.
  await page.goto('/#/settings')
  const rail = page.locator('aside')
  await expect(rail.getByRole('button', { name: 'Language: English' })).toBeInViewport()
  await expect(rail.getByRole('button', { name: 'Toggle theme' })).toBeInViewport()
  await rail.getByRole('button', { name: 'Language: English' }).click()
  await expect(page.getByRole('option', { name: '简体中文' })).toBeInViewport()
})


test('the sidebar recovers artwork when the next job follows one without a poster', async ({ page }) => {
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob({ mediaFileId: 7 })] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.route('**/api/media/7/thumbnail', route => route.fulfill({ status: 404 }))
  await page.route('**/api/media/8/thumbnail', route => route.fulfill({ contentType: 'image/png', path: 'public/favicon-192.png' }))
  await page.goto('/#/settings')
  await expect(page.locator('aside [data-thumbnail]')).toBeVisible()
  await expect(page.locator('aside [data-thumbnail] img')).toHaveCount(0)
  fixture.jobs = [liveJob({ id: 8, mediaFileId: 8, relativePath: 'New film.mkv' })]
  await notify.ready
  notify()
  await expect(page.locator('aside [data-thumbnail] img')).toHaveJSProperty('naturalWidth', 192)
  await expect(page.locator('aside [data-thumbnail] img')).toHaveCSS('opacity', '1')
})

test('a lost graphics context falls back once and still follows theme and activity', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('optimisarr.brand', 'precession'))
  await page.addInitScript(() => {
    const getContext = HTMLCanvasElement.prototype.getContext
    const target = window as Window & { brandContexts: WebGLRenderingContext[] }
    target.brandContexts = []
    HTMLCanvasElement.prototype.getContext = function (...args: Parameters<typeof getContext>) {
      const context = getContext.apply(this, args)
      if (context instanceof WebGLRenderingContext) target.brandContexts.push(context)
      return context
    } as typeof getContext
    // A renderer retired during initial theme/brand setup stays in the capture list.
    const retired = document.createElement('canvas')
    retired.getContext('webgl')?.getExtension('WEBGL_lose_context')?.loseContext()
    retired.width = retired.height = 1
  })
  await page.emulateMedia({ colorScheme: 'dark' })
  const fixture: Fixture = {}
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  const contexts = await page.evaluate(() => {
    const contexts = (window as Window & { brandContexts: WebGLRenderingContext[] }).brandContexts
    const active = contexts.filter(context => context.canvas.width > 1 && !context.isContextLost())
    for (const context of active) {
      const loss = context.getExtension('WEBGL_lose_context')
      if (!loss) throw new Error('A live brand context must support deliberate context loss')
      loss.loseContext()
    }
    return { created: contexts.length, deliberatelyLost: active.length }
  })
  expect(contexts.deliberatelyLost).toBeGreaterThan(0)
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  fixture.queue = { runningJobs: 1 }
  fixture.jobs = [liveJob()]
  await notify.ready
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  const before = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await page.getByRole('button', { name: 'Toggle theme', exact: true }).click()
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(before)
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  expect(await page.evaluate(() => (window as Window & { brandContexts: WebGLRenderingContext[] }).brandContexts.length)).toBe(contexts.created)
})


test('precession is first and default, while the stellar icon choice persists with its favicon', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' })
  await mockDashboard(page)
  await page.goto('/#/settings/system')
  const choice = page.getByLabel('Application icon', { exact: true })
  const mark = page.locator('aside canvas').first()
  const favicon = page.locator('link[rel~="icon"]').first()
  await expect(choice).toHaveValue('precession')
  await expect(choice.locator('option').first()).toHaveAttribute('value', 'precession')
  await expect(mark).toHaveAttribute('data-brand-style', 'precession')
  await expect(favicon).toHaveAttribute('href', '/brand/favicon-dark-steady.png')
  await choice.selectOption('stellar')
  await expect(mark).toHaveAttribute('data-brand-style', 'stellar')
  await expect(favicon).toHaveAttribute('href', '/brand/stellar/favicon-dark-steady.png')
  await page.reload()
  await expect(choice).toHaveValue('stellar')
  await expect(mark).toHaveAttribute('data-brand-style', 'stellar')
  await expect(favicon).toHaveAttribute('href', '/brand/stellar/favicon-dark-steady.png')
  await choice.selectOption('precession')
  await expect(mark).toHaveAttribute('data-brand-style', 'precession')
  await expect(favicon).toHaveAttribute('href', '/brand/favicon-dark-steady.png')
})

test('stellar activity settles back to idle and retains its renderer through theme changes', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('optimisarr.brand', 'stellar'))
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob()] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-brand-style', 'stellar')
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  await expect(mark).toHaveAttribute('data-brand-mode', 'cycle')
  await page.getByRole('button', { name: 'Toggle theme', exact: true }).click()
  await expect(mark).toHaveAttribute('data-brand-mode', 'cycle')
  fixture.queue = { runningJobs: 0 }
  fixture.jobs = []
  await notify.ready
  notify()
  await expect(mark).toHaveAttribute('data-brand-mode', 'settle')
  await expect(mark).toHaveAttribute('data-brand-mode', 'rest', { timeout: 15_000 })
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
})

test('an unavailable stellar renderer leaves a themed still and a working favicon', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('optimisarr.brand', 'stellar'))
  await page.route(/stellar-renderer/, route => route.abort())
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  await expect(page.locator('link[rel~="icon"]').first()).toHaveAttribute('href', /stellar\/favicon-(dark|light)-excited\.png$/)
  expect(await mark.evaluate((el: HTMLCanvasElement) => {
    const pixels = el.getContext('2d')!.getImageData(0, 0, el.width, el.height).data
    return pixels.some((v, i) => i % 4 === 3 && v > 0)
  })).toBe(true)
})

test('the status strip reports the queue on every page and pauses it from there', async ({ page }) => {
  await mockDashboard(page, { stats: { queued: 0 } })
  await page.goto('/#/libraries')

  const strip = page.getByRole('region', { name: 'State' })
  await expect(strip.getByText('Idle', { exact: true })).toBeVisible()
  await strip.getByRole('button', { name: 'Pause queue' }).click()
  await expect(strip.getByText('Paused', { exact: true })).toBeVisible()
  await expect(strip.getByRole('button', { name: 'Resume queue' })).toBeVisible()
})

test('recent results show each file before and after, with what it saved', async ({ page }) => {
  await mockDashboard(page, {
    results: [{
      jobId: 41, mediaFileId: 7, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E07 - The Long Tide WEBDL-1080p.mkv',
      sourceSizeBytes: 1_282_683_553, outputSizeBytes: 368_922_428, vmafHarmonicMean: 91.31,
      videoEncoder: 'hevc_qsv', workerName: null, finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }],
    daily: [{ date: '2026-09-24', bytesSaved: 0, files: 0 }, { date: '2026-09-25', bytesSaved: 913_761_125, files: 1 }],
  })
  await page.goto('/#/')

  const results = page.getByRole('region', { name: 'Recent results' })
  // The release tags describe the copy, not the programme; the row reads as show, episode, title.
  await expect(results.getByText('Harborlight', { exact: true })).toBeVisible()
  await expect(results.getByText('The Long Tide')).toBeVisible()
  await expect(results.getByText('1.2 GB → 352 MB')).toBeVisible()
  await expect(results.getByText('−71%')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Saved per day' }).getByRole('slider')).toHaveAccessibleName('Last 2 days: 871 MB saved')
})

test('a file that grew says so, and never claims a saving', async ({ page }) => {
  await mockDashboard(page, {
    results: [{
      jobId: 42, mediaFileId: 8, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E08 - The Quiet Hour WEBDL-1080p.mkv',
      sourceSizeBytes: 1_000_000_000, outputSizeBytes: 1_200_000_000, vmafHarmonicMean: 88.02,
      videoEncoder: 'hevc_qsv', workerName: null, finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }, {
      jobId: 43, mediaFileId: 9, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E09 - Low Tide WEBDL-1080p.mkv',
      sourceSizeBytes: 1_000_000_000, outputSizeBytes: 1_000_000_000, vmafHarmonicMean: 90.11,
      videoEncoder: 'hevc_qsv', workerName: null, finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }, {
      // A change finer than one percent, where an old rounding could read as no change at all.
      jobId: 44, mediaFileId: 10, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E10 - Salt Water WEBDL-1080p.mkv',
      sourceSizeBytes: 1_000_000_000, outputSizeBytes: 999_000_000, vmafHarmonicMean: 91.4,
      videoEncoder: 'hevc_qsv', workerName: null, finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }],
  })
  await page.goto('/#/')

  const results = page.getByRole('region', { name: 'Recent results' })
  // The figure is signed by what happened: a larger output reads "+20%", an unchanged one "0%".
  await expect(results.getByText('+20%')).toBeVisible()
  await expect(results.getByText('0%', { exact: true })).toBeVisible()
  await expect(results.getByText('−20%')).toHaveCount(0)
  // The growth is drawn in the warning tone, and a figure with nothing to say in neutral ink.
  await expect(results.getByText('+20%')).toHaveClass(/text-warn/)
  await expect(results.getByText('0%', { exact: true })).toHaveClass(/text-ink-2/)
  // A saving finer than a percent still reads as a saving, at a tenth of a percent: ink beside
  // the filled ok mark.
  const saving = results.getByText('−0.1%')
  await expect(saving).toHaveAttribute('data-size-change', 'saved')
  await expect(saving).toHaveClass(/text-ink/)
  await expect(saving).not.toHaveClass(/text-ok/)
  await expect(saving.locator('[data-ok-mark]')).toBeVisible()
})

test('an idle in-flight panel shows the last finished title instead of an empty box', async ({ page }) => {
  await mockDashboard(page, {
    results: [{
      jobId: 41, mediaFileId: 7, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E07 - The Long Tide WEBDL-1080p.mkv',
      sourceSizeBytes: 1_282_683_553, outputSizeBytes: 368_922_428, vmafHarmonicMean: 91.31,
      videoEncoder: 'hevc_qsv', workerName: 'Mac Studio', finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }],
  })
  await page.goto('/#/')

  const last = page.getByRole('region', { name: 'Last finished' })
  await expect(last).toContainText('Harborlight')
  await expect(last).toContainText('S02E07 · The Long Tide')
  await expect(last).toContainText('1.2 GB → 352 MB −71%')
  await expect(last).toContainText('TV · Mac Studio · 3 hours ago')
})

test('the idle in-flight panel reads a grown file the same way the results table does', async ({ page }) => {
  await mockDashboard(page, {
    results: [{
      jobId: 45, mediaFileId: 11, libraryId: 1, libraryName: 'TV',
      relativePath: 'Harborlight/Season 2/Harborlight - S02E11 - The Ferry WEBDL-1080p.mkv',
      sourceSizeBytes: 1_000_000_000, outputSizeBytes: 1_200_000_000, vmafHarmonicMean: 89.9,
      videoEncoder: 'hevc_qsv', workerName: null, finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    }],
  })
  await page.goto('/#/')

  const last = page.getByRole('region', { name: 'Last finished' })
  await expect(last).toContainText('954 MB → 1.1 GB +20%')
  await expect(page.getByRole('region', { name: 'Recent results' }).getByText('+20%')).toBeVisible()
})

test('an idle in-flight panel with no history says so and nothing more', async ({ page }) => {
  await mockDashboard(page, { stats: { queued: 0, running: 0, readyToReplace: 0, inQuarantine: 0, failed: 0 } })
  await page.goto('/#/')
  await expect(page.getByText('Nothing queued and nothing running. Everything eligible is done.')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Last finished' })).toHaveCount(0)
})

test('all playback holds are reachable by keyboard on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 720 })
  await mockDashboard(page, { queue: { canStart: false, blockedReason: 'Playback active.',
    playbackHolds: Array.from({ length: 10 }, (_, i) => ({ watcher: 'Plex', kind: 'Movie',
      title: `Film number ${i + 1}`, user: null, device: null, paused: false })),
  } })
  await page.goto('/#/queue')
  const more = page.locator('.playback-holds summary')
  await expect(more).toHaveText('+2 more')
  await more.focus()
  await page.keyboard.press('Enter')
  await expect(page.locator('.playback-holds').getByText('Film number 10', { exact: false })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(2)
})

test('an overlapping later poll does not starve a completed status response', async ({ page }) => {
  await mockDashboard(page)
  await page.goto('/#/')
  await expect(page.locator('.status-strip')).toBeVisible()
  // Finish startup polling before intercepting the two requests this test controls.
  await page.evaluate(async () => {
    const modulePath = '/src/lib/stores/counts.svelte.ts'
    const { counts } = await import(modulePath)
    counts.stop()
    await counts.refresh()
  })
  const pending: Route[] = []
  await page.route('**/api/queue/status', route => { pending.push(route) })
  for (let i = 0; i < 2; i++) {
    await page.evaluate(async () => {
      const modulePath = '/src/lib/stores/counts.svelte.ts'
      const { counts } = await import(modulePath)
      void counts.refresh()
    })
    await expect.poll(() => pending.length).toBe(i + 1)
  }
  await json(pending[0], { ...QUEUE_CLEAR, canStart: false, blockedReason: 'First completed poll' })
  await expect(page.locator('.status-strip-reason')).toHaveText('First completed poll')
  await json(pending[1], { ...QUEUE_CLEAR, canStart: false, blockedReason: 'Second completed poll' })
  await expect(page.locator('.status-strip-reason')).toHaveText('Second completed poll')
})

test('the rail leaves the encoding card to the pages that already show the work', async ({ page }) => {
  // Dashboard and Queue show the running job on the page; the rail repeating it beside them was
  // the same job three times on one screen. Every other page still gets the card.
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  const card = page.locator('aside').getByRole('link', { name: /Now encoding/ })

  await page.goto('/#/settings')
  await expect(card).toBeVisible()

  await page.goto('/#/')
  await expect(page.locator('.status-strip-label')).toHaveText('Encoding')
  await expect(card).toHaveCount(0)

  await page.goto('/#/queue')
  await expect(page.getByRole('heading', { level: 1, name: 'Queue' })).toBeVisible()
  await expect(card).toHaveCount(0)

  // Query parameters on Queue and unknown routes that fall back to Dashboard stay consistent.
  await page.goto('/#/queue?filter=active')
  await expect(card).toHaveCount(0)
  await page.goto('/#/nowhere')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()
  await expect(card).toHaveCount(0)

  // The rail's own activity figure stays everywhere, so the queue entry still says work is running.
  await expect(page.locator('aside').getByRole('button', { name: /Queue/ })).toContainText('1 / 14')
})

test('the browser tab names the page that is open', async ({ page }) => {
  await mockDashboard(page)
  await page.goto('/#/')
  await expect(page).toHaveTitle('Dashboard · Optimisarr')

  const rail = page.locator('aside')
  // The queue entry carries its count, so its name is "Queue 14".
  await rail.getByRole('button', { name: /^Queue/ }).click()
  await expect(page).toHaveTitle('Queue · Optimisarr')
  await rail.getByRole('button', { name: 'Settings', exact: true }).click()
  await expect(page).toHaveTitle('Settings · Optimisarr')

  // A page inside a section names itself first, then the section.
  await page.evaluate(() => { window.location.hash = '/settings/encoding' })
  await expect(page).toHaveTitle('Encoding · Settings · Optimisarr')
  await page.evaluate(() => { window.location.hash = '/inventory/duplicates' })
  await expect(page).toHaveTitle('Find duplicates · Inventory · Optimisarr')

  // The title follows the language too.
  await rail.getByRole('button', { name: 'Language: English' }).click()
  await page.getByRole('option', { name: 'Deutsch' }).click()
  await expect(page).not.toHaveTitle(/Find duplicates/)
  await expect(page).toHaveTitle(/ · Bestand · Optimisarr$/)
})

test('moving to another page puts keyboard focus on its heading, and the first page leaves focus alone', async ({ page }) => {
  await mockDashboard(page)
  await page.goto('/#/')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()
  await expect(page.locator('body')).toBeFocused()

  const rail = page.locator('aside')
  await rail.getByRole('button', { name: 'Schedule', exact: true }).focus()
  await page.keyboard.press('Enter')
  const schedule = page.getByRole('heading', { level: 1, name: 'Schedule' })
  await expect(schedule).toBeFocused()
  // Focus that the heading takes for a screen reader is not a tab stop.
  await expect(schedule).toHaveAttribute('tabindex', '-1')

  await rail.getByRole('button', { name: 'Inventory', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Inventory' })).toBeFocused()

  // Back and forward are navigation too.
  await page.goBack()
  await expect(schedule).toBeFocused()

  // Navigating between Settings rooms and overview keeps focus moving on Back and Forward too.
  await rail.getByRole('button', { name: 'Settings', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeFocused()
  await page.locator('#settings-room-encoding').click()
  await expect(page.locator('#room-heading')).toBeFocused()
  await page.goBack()
  await expect(page.locator('#settings-room-encoding')).toBeFocused()
  await page.goForward()
  await expect(page.locator('#room-heading')).toBeFocused()
})

test('a new page starts at its top', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 600 })
  await mockDashboard(page)
  await page.goto('/#/')
  const main = page.locator('main')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()
  await main.evaluate((element) => { element.style.paddingBottom = '3000px'; element.scrollTop = 1500 })
  await expect.poll(() => main.evaluate((element) => element.scrollTop)).toBeGreaterThan(0)
  await page.locator('aside').getByRole('button', { name: 'Schedule', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Schedule' })).toBeFocused()
  expect(await main.evaluate((element) => element.scrollTop)).toBe(0)
})

test('a long status reason wraps onto further lines instead of being cut off once the phone strip is open', async ({ page }) => {
  // The reason used to be clipped to one line with an ellipsis, and the rest lived only in a
  // tooltip that touch and keyboard users could not open. A phone still shows one line until the
  // strip is opened, with a real button, and then the whole reason is on screen.
  const waiting = '1418 job(s) waiting for the TV optimise window (00:00–05:00), and 12 more waiting for the Films optimise window (01:00–06:00) on this server'
  await page.setViewportSize({ width: 390, height: 844 })
  await mockDashboard(page, { queue: { waitingReason: waiting } })
  await page.goto('/#/')

  const reason = page.locator('.status-strip-reason')
  await expect(reason).toHaveText(waiting)
  await page.getByRole('region', { name: 'State' }).getByRole('button', { name: 'Status details' }).click()
  await expect(reason).not.toHaveCSS('text-overflow', 'ellipsis')
  await expect(reason).not.toHaveCSS('white-space', 'nowrap')
  // All of it is on screen: more than one line tall, and nothing hidden past its right edge.
  const metrics = await reason.evaluate((element) => ({
    height: element.getBoundingClientRect().height,
    lineHeight: Number.parseFloat(getComputedStyle(element).lineHeight),
    overflow: element.scrollWidth - element.clientWidth,
  }))
  expect(metrics.height).toBeGreaterThan(metrics.lineHeight * 1.5)
  expect(metrics.overflow).toBeLessThanOrEqual(0)
  // The full sentence is on screen, so it needs no tooltip repeating it.
  await expect(reason).not.toHaveAttribute('title')
  const strip = await page.locator('.status-strip').boundingBox()
  expect(strip!.x + strip!.width).toBeLessThanOrEqual(390)
})

test('a wrapped playback link keeps its lines together on a touch screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const hold = { watcher: 'Riker Plex', year: null, artist: null, paused: false, kind: 'Episode', season: 2, episode: 5, device: 'Living Room TV' }
  await mockDashboard(page, {
    queue: {
      canStart: false, blockedReason: 'Paused while Riker Plex is active (2 streams).',
      playbackHolds: [
        { ...hold, title: 'A Very Long Episode Title That Will Need More Than One Line', series: 'An Example Show With A Long Name', user: 'alex' },
        { ...hold, title: 'Second', series: 'Other', user: 'sam' },
      ],
    },
  })
  await page.goto('/#/')
  await page.getByRole('region', { name: 'State' }).getByRole('button', { name: 'Status details' }).click()
  const reason = page.locator('.status-strip-playback')
  await expect(reason).toContainText('An Example Show With A Long Name')
  const metrics = await reason.evaluate((element) => ({
    height: element.getBoundingClientRect().height,
    lineHeight: Number.parseFloat(getComputedStyle(element).lineHeight),
    fontSize: Number.parseFloat(getComputedStyle(element).fontSize),
  }))
  // A 44px target, but lines spaced as text rather than 44px apart.
  expect(metrics.height).toBeGreaterThanOrEqual(44)
  expect(metrics.lineHeight).toBeLessThan(metrics.fontSize * 2)
})

const NOTHING_WAITING = { readyToReplace: 0, inQuarantine: 0, failed: 0 }

async function panelHeadings(page: Page) {
  return page.locator('main').getByRole('heading', { level: 2 }).allTextContents()
}

for (const viewport of [{ width: 390, height: 844 }, { width: 1280, height: 800 }, { width: 2560, height: 1440 }]) {
  test(`a decision waiting on you leads the dashboard at ${viewport.width}px, ahead of the figures`, async ({ page }) => {
    await page.setViewportSize(viewport)
    await mockDashboard(page, { stats: { readyToReplace: 3 } })
    await page.goto('/#/')

    const needs = page.getByRole('region', { name: 'Needs you' })
    const savings = page.getByRole('region', { name: 'Total space saved' })
    await expect(needs.getByRole('button', { name: 'Replace' })).toBeVisible()
    // First in the document as well as on screen, so a keyboard reaches it first too.
    expect((await panelHeadings(page))[0]).toBe('Needs you')
    const [needsBox, savingsBox] = [await needs.boundingBox(), await savings.boundingBox()]
    expect(needsBox!.y + needsBox!.height).toBeLessThanOrEqual(savingsBox!.y + 1)
    // The figure beside each decision is a title, never a second display figure competing with the saving.
    await expect(needs.getByText('3', { exact: true })).toHaveClass(/text-title/)
    await expect(page.locator('main .text-display')).toHaveCount(1)
  })

  test(`with nothing waiting at ${viewport.width}px, the saving and the work in flight lead instead`, async ({ page }) => {
    await page.setViewportSize(viewport)
    await mockDashboard(page, { stats: NOTHING_WAITING })
    await page.goto('/#/')

    const needs = page.getByRole('region', { name: 'Needs you' })
    const savings = page.getByRole('region', { name: 'Total space saved' })
    await expect(needs.getByText('Nothing is waiting on a decision from you.')).toBeVisible()
    const headings = await panelHeadings(page)
    expect(headings[0]).toBe('Total space saved')
    expect(headings.indexOf('Needs you')).toBeGreaterThan(0)
    const [needsBox, savingsBox] = [await needs.boundingBox(), await savings.boundingBox()]
    expect(needsBox!.y).toBeGreaterThan(savingsBox!.y + savingsBox!.height - 1)
  })
}

test('waiting decisions sit side by side when the panel spans a wide screen, and stack on a phone', async ({ page }) => {
  await mockDashboard(page, { stats: { readyToReplace: 3 } })
  const items = page.getByRole('region', { name: 'Needs you' }).getByRole('listitem')

  await page.setViewportSize({ width: 2560, height: 1440 })
  await page.goto('/#/')
  await expect(items).toHaveCount(3)
  const wide = await Promise.all([0, 1, 2].map(index => items.nth(index).boundingBox()))
  expect(Math.abs(wide[0]!.y - wide[2]!.y)).toBeLessThan(2)
  expect(wide[1]!.x).toBeGreaterThan(wide[0]!.x + wide[0]!.width - 2)

  await page.setViewportSize({ width: 390, height: 844 })
  const narrow = await Promise.all([0, 1].map(index => items.nth(index).boundingBox()))
  expect(narrow[1]!.y).toBeGreaterThan(narrow[0]!.y + narrow[0]!.height - 2)
})

test('one or two waiting decisions fill the wide banner without leaving empty columns', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await mockDashboard(page, { stats: { readyToReplace: 2, inQuarantine: 0, failed: 0 } })
  await page.goto('/#/')
  const panel = page.getByRole('region', { name: 'Needs you' })
  const one = panel.getByRole('listitem')
  await expect(one).toHaveCount(1)
  const [panelBox, oneBox] = [await panel.boundingBox(), await one.first().boundingBox()]
  expect(oneBox!.width).toBeGreaterThan(panelBox!.width * 0.9)

  await page.unroute('**/api/**')
  await mockDashboard(page, { stats: { readyToReplace: 2, inQuarantine: 5, failed: 0 } })
  await page.reload()
  const two = panel.getByRole('listitem')
  await expect(two).toHaveCount(2)
  const [left, right] = [await two.nth(0).boundingBox(), await two.nth(1).boundingBox()]
  expect(Math.abs(left!.y - right!.y)).toBeLessThan(2)
  expect(left!.width + right!.width).toBeGreaterThan(panelBox!.width * 0.9)
})

test('suspended local encodes pause cleanly in In flight while finalizing work labels itself as replacing', async ({ page }) => {
  await mockDashboard(page, {
    queue: { runningJobs: 1, manuallyPaused: true, manualPauseMode: 'suspended', runningEncodesSuspended: true, canStart: false, blockedReason: 'Paused manually.' },
    jobs: [
      liveJob(),
      liveJob({ id: 5846, mediaFileId: 5846, relativePath: 'TV/Harborlight/S02/Harborlight.S02E08.mkv', status: 'ReadyToReplace', finalizing: true, progress: 0 }),
    ],
  })
  await page.goto('/#/')
  const inFlight = page.getByRole('region', { name: 'In flight' })
  const pausedStage = inFlight.locator('[data-stage-tone="paused"]')
  await expect(pausedStage).toHaveText('Paused')
  await expect(pausedStage).toHaveClass(/text-warn/)
  await expect(pausedStage.locator('.animate-pulse')).toHaveCount(0)
  const finalizingStage = inFlight.locator('[data-stage-tone="live"]')
  await expect(finalizingStage).toContainText('Finalising replacement')
})

test('in-flight values carry their names and the encoder’s own quality control', async ({ page }) => {
  await mockDashboard(page, {
    queue: { runningJobs: 1 },
    jobs: [
      liveJob({ videoQualityMode: 'icq', effectiveVideoQuality: 20 }),
      liveJob({ id: 5846, mediaFileId: 5846, relativePath: 'TV/Harborlight/S02/Harborlight.S02E08.mkv', videoEncoder: 'libx265', videoQualityMode: 'AdaptiveVmaf' }),
    ],
  })

  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport)
    await page.goto('/#/')
    const main = page.locator('main')
    // A hardware encode is labelled with its own control, never "CRF" beside a bare "icq".
    await expect(main.getByText('ICQ 20', { exact: true })).toBeVisible()
    await expect(main.getByText('icq', { exact: true })).toHaveCount(0)
    await expect(main.getByText('CRF 24', { exact: true })).toBeVisible()
    await expect(main.getByText('Adaptive VMAF', { exact: true })).toBeVisible()
    // Every quality value sits under its name, on a phone as well as on a wide screen.
    const labels = main.getByText('Quality mode', { exact: true })
    await expect(labels).toHaveCount(2)
    await expect(labels.first()).toBeVisible()
  }
})

test('only running work is drawn in the live blue; waiting work is plain and a failure is failed', async ({ page }) => {
  await mockDashboard(page, {
    queue: { runningJobs: 1 },
    jobs: [
      liveJob(),
      liveJob({ id: 5846, mediaFileId: 5846, relativePath: 'TV/Harborlight/S02/Harborlight.S02E08.mkv', status: 'Queued', progress: 0 }),
      liveJob({ id: 5847, mediaFileId: 5847, relativePath: 'TV/Harborlight/S02/Harborlight.S02E09.mkv', status: 'ReadyToReplace', progress: 0 }),
      liveJob({ id: 5848, mediaFileId: 5848, relativePath: 'TV/Harborlight/S02/Harborlight.S02E10.mkv', status: 'Failed', progress: 0 }),
    ],
  })
  await page.goto('/#/')

  const stage = (name: string) => page.locator('main [data-stage-tone]').filter({ hasText: name })
  await expect(stage('Encoding')).toHaveClass(/text-accent/)
  await expect(stage('Encoding').locator('.animate-pulse')).toHaveCount(1)
  for (const name of ['Queued', 'Ready to replace']) {
    await expect(stage(name)).toHaveClass(/text-ink-2/)
    await expect(stage(name)).not.toHaveClass(/text-accent/)
    await expect(stage(name).locator('.animate-pulse')).toHaveCount(0)
  }
  await expect(stage('Failed')).toHaveClass(/text-bad/)
  await expect(stage('Failed')).not.toHaveClass(/text-accent/)
  // Waiting work holds still rather than sweeping as if it were running.
  const queuedRow = page.locator('main li').filter({ has: stage('Queued') })
  await expect(queuedRow.locator('.progress-indeterminate')).toHaveCount(0)
  await expect(queuedRow.getByText('in progress')).toHaveCount(0)
})

test('an in-flight title stays readable when the panel takes half of a laptop screen', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 })
  await mockDashboard(page, { queue: { runningJobs: 1 }, jobs: [liveJob()] })
  await page.goto('/#/')

  const title = page.locator('main').getByRole('button', { name: /^Harborlight S02E07/ })
  await expect(title).toBeVisible()
  // The full title fits: it is not cut to its first letter by a three-column row.
  expect(await title.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
})

test('the fleet names its columns and shows this server’s free work space', async ({ page }) => {
  await mockDashboard(page, { workersAvailable: true })
  await page.goto('/#/')

  const fleet = page.locator('.area-fleet')
  const columns = fleet.locator('[data-fleet-columns]')
  await expect(columns).toBeVisible()
  await expect(columns).toHaveText(/Machine\s*Load\s*Free space/)
  // QUEUE_CLEAR reports 3,171,405,266,944 bytes free on the work disk.
  const local = fleet.getByRole('listitem').first()
  await expect(local).toContainText('This server')
  await expect(local.getByText('2.9 TB', { exact: true })).toBeVisible()

  await fleet.getByRole('button', { name: 'Pair one →' }).click()
  await expect(page).toHaveURL(/#\/settings\/workers$/)
})

test('on a phone, free space is named on its own line rather than left as a bare size', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockDashboard(page, { queue: { freeDiskBytes: null } })
  await page.goto('/#/')

  const local = page.locator('.area-fleet').getByRole('listitem').first()
  await expect(page.locator('.area-fleet [data-fleet-columns]')).toBeHidden()
  await expect(local.getByText('Free space', { exact: true })).toBeVisible()
  // An unknown size is an accessible dash, not an empty cell.
  const cell = local.getByText('Free space', { exact: true }).locator('..')
  await expect(cell.getByText('—', { exact: true })).toBeVisible()
  await expect(cell).toHaveText(/^Free space\s*—\s*Unknown$/)
})

test('the saved-per-day chart steps through days from the keyboard and reads its figures in words', async ({ page }) => {
  const MB = 1024 * 1024
  await mockDashboard(page, {
    daily: [
      { date: '2026-09-21', bytesSaved: 100 * MB, files: 1 },
      { date: '2026-09-22', bytesSaved: 0, files: 0 },
      { date: '2026-09-23', bytesSaved: 500 * MB, files: 2 },
      { date: '2026-09-24', bytesSaved: 1024 * MB, files: 1 },
      { date: '2026-09-25', bytesSaved: 200 * MB, files: 3 },
    ],
  })
  await page.goto('/#/')

  const chart = page.getByRole('region', { name: 'Saved per day' })
  const plot = chart.getByRole('slider')
  const readout = chart.locator('[data-saved-readout]')
  const spoken = chart.locator('[data-saved-announcement]')
  await expect(plot).toHaveAccessibleName('Last 5 days: 1.8 GB saved')
  await expect(plot).toHaveAccessibleDescription('Use Left and Right arrow keys to inspect individual days.')
  await expect(spoken).toHaveAttribute('aria-live', 'polite')

  // The figures are on screen without a pointer.
  const summary = chart.locator('[data-saved-summary]')
  await expect(readout).toHaveText('Last 5 days: 1.8 GB saved')
  await expect(summary).toContainText('365 MB a day on average')
  await expect(summary).toContainText(/Peak .+: 1\.0 GB/)
  // Nothing is spoken until a key moves the selection; hovering stays quiet.
  await plot.hover()
  await expect(spoken).toHaveText('')

  await page.mouse.move(0, 0)
  await plot.focus()
  await page.keyboard.press('ArrowLeft')
  // The first key lands on the latest day that saved something: today here.
  await expect(readout).toHaveText(/200 MB saved from 3 files/)
  await expect(spoken).toHaveText(/200 MB saved from 3 files/)
  await page.keyboard.press('ArrowLeft')
  await expect(readout).toHaveText(/1\.0 GB saved from 1 file$/)
  await expect(spoken).toHaveText(/1\.0 GB saved from 1 file$/)
  await expect(chart.locator('.saved-day-selected')).toHaveCount(1)
  await page.keyboard.press('ArrowLeft')
  await page.keyboard.press('ArrowLeft')
  await expect(readout).toHaveText(/nothing finished/)
  await page.keyboard.press('ArrowRight')
  await expect(readout).toHaveText(/500 MB saved from 2 files/)
  await page.keyboard.press('Home')
  await expect(readout).toHaveText(/100 MB saved from 1 file$/)
  await page.keyboard.press('End')
  await expect(readout).toHaveText(/200 MB saved from 3 files/)
  await page.keyboard.press('Escape')
  await expect(readout).toHaveText('Last 5 days: 1.8 GB saved')
  await expect(spoken).toHaveText('')
  await expect(chart.locator('.saved-day-selected')).toHaveCount(0)

  // Leaving the chart also clears the day selection back to the summary.
  await page.keyboard.press('ArrowLeft')
  await expect(chart.locator('.saved-day-selected')).toHaveCount(1)
  await plot.blur()
  await expect(readout).toHaveText('Last 5 days: 1.8 GB saved')
  await expect(chart.locator('.saved-day-selected')).toHaveCount(0)
})

test('the sidebar toggle names what pressing it will do', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await mockDashboard(page)
  await page.goto('/#/')
  const collapse = page.getByRole('button', { name: 'Collapse sidebar', exact: true })
  await expect(collapse).toHaveAttribute('aria-expanded', 'true')
  await collapse.click()
  const expand = page.getByRole('button', { name: 'Expand sidebar', exact: true })
  await expect(expand).toHaveAttribute('aria-expanded', 'false')
  await expand.click()
  await expect(collapse).toBeVisible()
})
