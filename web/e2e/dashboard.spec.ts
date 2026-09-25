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

  // Scoped to the page: the sidebar's encoding card names the same file, encoder and figure.
  const main = page.locator('main')
  await expect(main.getByText('ENCODING', { exact: true })).toBeVisible()
  await expect(main.getByText('Harborlight.S02E07.mkv')).toBeVisible()
  await expect(main.getByText('hevc_qsv')).toBeVisible()
  await expect(main.getByText('68%')).toBeVisible()
  // The enum name is read out in words rather than printed as PascalCase.
  await expect(main.getByText('Adaptive VMAF')).toBeVisible()
})

test('the sidebar shows what is encoding, whichever page is open', async ({ page }) => {
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

  await expect(page.getByText('Episode.0.mkv')).toBeVisible()
  await expect(page.getByText('Episode.5.mkv')).toBeVisible()
  await expect(page.getByText('Episode.6.mkv')).toBeHidden()
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
    return { lit, clear, border }
  })
  expect(painted.lit).toBeGreaterThan(50)
  expect(painted.border.every(alpha => alpha === 0)).toBe(true)
  expect(painted.clear).toBeGreaterThan(288 * 288 * 0.3)
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
  await page.route('**/hubs/jobs/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'brand-test', connectionToken: 'brand-test',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }))
  await page.routeWebSocket('**/hubs/jobs?*', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) socket.send('{}\u001e')
    })
    notify = () => socket.send(JSON.stringify({ type: 1, target: 'jobsChanged', arguments: [] }) + '\u001e')
  })
  return () => notify()
}

test('job events change the cube state while suspended work keeps moving illumination', async ({ page }) => {
  const fixture: Fixture = { queue: { runningJobs: 1 }, jobs: [liveJob()] }
  await mockDashboard(page, fixture)
  const notify = await mockJobsHub(page)
  await page.goto('/#/')
  const mark = page.locator('aside canvas').first()
  await expect(mark).toHaveAttribute('data-light-motion', 'playing')
  fixture.queue = { runningJobs: 1, suspendedEncodeCount: 1, runningEncodesSuspended: true }
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
  await expect(page.locator('main').getByText('ENCODING', { exact: true })).toBeVisible()
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
  await page.goto('/#/')
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
  await page.goto('/#/')
  await expect(page.locator('aside [data-thumbnail]')).toBeVisible()
  await expect(page.locator('aside [data-thumbnail] img')).toHaveCount(0)
  fixture.jobs = [liveJob({ id: 8, mediaFileId: 8, relativePath: 'New film.mkv' })]
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
    for (const context of contexts) context.getExtension('WEBGL_lose_context')!.loseContext()
    return contexts.length
  })
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  fixture.queue = { runningJobs: 1 }
  fixture.jobs = [liveJob()]
  notify()
  await expect(mark).toHaveAttribute('data-light-state', 'excited')
  const before = await mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())
  await page.getByRole('button', { name: 'Toggle theme', exact: true }).click()
  await expect.poll(() => mark.evaluate((el: HTMLCanvasElement) => el.toDataURL())).not.toBe(before)
  await expect(mark).toHaveAttribute('data-light-motion', 'still')
  expect(await page.evaluate(() => (window as Window & { brandContexts: WebGLRenderingContext[] }).brandContexts.length)).toBe(contexts)
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
