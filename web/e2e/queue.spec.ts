import { expect, test, type Page, type Route, type WebSocketRoute } from '@playwright/test'

function job(id: number, status: string, verificationPassed: boolean | null) {
  return {
    id,
    mediaFileId: id,
    libraryId: 1,
    relativePath: `Film ${id}.mkv`,
    status,
    priority: 0,
    progress: 1,
    errorMessage: null,
    enqueueReason: null,
    failureCategory: null,
    ffmpegArguments: null,
    videoEncoder: 'libx265',
    requestedVideoQuality: 24,
    effectiveVideoQuality: 24,
    videoQualityMode: 'crf',
    qualityRetryCount: 0,
    outputSizeBytes: 1_000,
    verificationPassed,
    verificationReportJson: null,
    verifiedAt: verificationPassed ? '2026-07-23T12:00:00Z' : null,
    enqueuedAt: '2026-07-23T11:00:00Z',
    startedAt: null,
    finishedAt: null,
    clearable: false,
    workerName: null,
    remoteStage: null,
    waitingForWorker: false,
  }
}

async function mockQueue(page: Page) {
  let jobs = [
    job(1, 'ReadyToReplace', true),
    job(2, 'ReadyToReplace', true),
    job(3, 'Queued', null),
  ]
  let bulkRequests = 0

  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, {
      version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true,
    })
    if (path === '/api/jobs' && route.request().method() === 'GET') return json(route, jobs)
    if (path === '/api/queue/status') return json(route, {
      canStart: true,
      blockedReason: null,
      manuallyPaused: false,
      manualPauseMode: 'inactive',
      runningEncodesSuspended: false,
      suspendedEncodeCount: 0,
      pauseFailedEncodeCount: 0,
      runningJobs: 0,
      hardwareAccelerated: false,
      freeDiskBytes: 100_000_000_000,
      workRoot: '/work',
      waitingReason: null,
    })
    if (path === '/api/jobs/replace-ready' && route.request().method() === 'POST') {
      bulkRequests += 1
      jobs = jobs.map((item) =>
        item.status === 'ReadyToReplace' ? { ...item, status: 'Completed' } : item)
      return json(route, { attempted: 2, replaced: 2, failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  return () => bulkRequests
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

test('replace all confirms once, replaces every ready job in one request, and opens quarantine once', async ({ page }) => {
  const bulkRequests = await mockQueue(page)
  let confirmationCount = 0
  page.on('dialog', async (dialog) => {
    confirmationCount += 1
    expect(dialog.message()).toContain('Replace all 2 verified outputs?')
    await dialog.accept()
  })

  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Replace all (2)' }).click()

  await expect(page).toHaveURL(/#\/quarantine$/)
  expect(confirmationCount).toBe(1)
  expect(bulkRequests()).toBe(1)
})

test('a remote job says where it is, and a job kept for a worker says it is waiting', async ({ page }) => {
  const remote = {
    ...job(7, 'Leased', null),
    relativePath: 'Chicago Fire S14E12.mkv',
    progress: 0.62,
    workerName: 'Mac Studio',
    remoteStage: 'Encoding',
  }
  const returned = { ...job(8, 'AwaitingVerification', null), relativePath: 'Slow Horses S05E01.mkv', workerName: 'MacBook Air' }
  const held = { ...job(9, 'Queued', null), relativePath: 'Severance S03E04.mkv', waitingForWorker: true }
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs') return json(route, [remote, returned, held])
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0, runningJobs: 1,
      hardwareAccelerated: false, freeDiskBytes: 100_000_000_000, workRoot: '/work', waitingReason: null,
    })
    return json(route, {})
  })
  await page.goto('/#/queue')

  // The hero leads with the remote encode and names the machine.
  await expect(page.getByText('Now encoding on Mac Studio', { exact: true })).toBeVisible()
  await expect(page.getByText('Returned from MacBook Air', { exact: true })).toBeVisible()

  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working).toContainText('encoding on Mac Studio')
  await expect(working).toContainText('62%')
  const rows = page.locator('tbody tr')
  await expect(rows.filter({ hasText: 'Chicago Fire' })).toHaveCount(0)
  await expect(rows.filter({ hasText: 'Severance' })).toContainText('waiting for a worker')
})

const clearQueue = {
  canStart: true, manuallyPaused: false, manualPauseMode: 'inactive', runningEncodesSuspended: false,
  suspendedEncodeCount: 0, pauseFailedEncodeCount: 0, runningJobs: 1, hardwareAccelerated: true,
  freeDiskBytes: 100_000_000_000, blockedReason: null, waitingReason: null,
}

async function mockWorkingQueue(page: Page, fixture: { jobs: ReturnType<typeof job>[]; queue?: Record<string, unknown> }) {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { completed: true, completedStep: 5, stepCount: 5 })
    if (path === '/api/jobs') return json(route, fixture.jobs)
    if (path === '/api/queue/status') return json(route, { ...clearQueue, ...fixture.queue })
    if (path === '/api/health') return json(route, { status: 'healthy', version: '0.2.12' })
    if (path === '/api/queue/pause') {
      fixture.queue = { manuallyPaused: true, runningEncodesSuspended: true, manualPauseMode: 'suspended', blockedReason: 'Running encodes suspended.' }
      return json(route, { ...clearQueue, ...fixture.queue })
    }
    if (path === '/api/queue/resume') { fixture.queue = {}; return json(route, clearQueue) }
    return route.fulfill({ status: 404, body: '{}' })
  })
}

test('Now and next keeps working jobs separate and opens a keyboard-accessible job dialog', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Transcoding', null), progress: .9999 }, job(2, 'Queued', null)] })
  await page.goto('/#/queue')
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '99')
  await expect(page.locator('tbody tr')).toHaveCount(1)
  const opener = working.getByRole('button', { name: 'View job' })
  await opener.focus()
  await page.keyboard.press('Enter')
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details).toBeVisible()
  await expect(details.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '99')
  await expect(details.getByText('Film 1.mkv', { exact: true })).toBeVisible()
  await details.getByRole('button', { name: 'Close details' }).click()
  await expect(opener).toBeFocused()
})

test('a loaded poster is visible when the working job first appears', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Transcoding', null), progress: .64 }] })
  await page.route('**/api/media/*/thumbnail', route => route.fulfill({
    contentType: 'image/svg+xml',
    body: '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#52748c"/></svg>',
  }))
  await page.goto('/#/queue')
  const poster = page.getByRole('region', { name: 'Working now' }).locator('img')
  await expect(poster).toHaveJSProperty('complete', true)
  await expect(poster).toHaveCSS('opacity', '1')
})

test('pausing local work leaves remote work and verification described accurately', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [
    { ...job(1, 'Transcoding', null), progress: .64 },
    { ...job(2, 'Leased', null), progress: .38, workerName: 'Mac mini', remoteStage: 'Encoding' } as ReturnType<typeof job>,
    { ...job(3, 'Verifying', null), progress: .18 },
  ] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Pause queue', exact: true }).click()
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working.locator('article').filter({ hasText: 'Paused' })).toHaveCount(1)
  await expect(working.getByText('Now encoding on Mac mini', { exact: true })).toBeVisible()
  await expect(working.getByText('Now verifying', { exact: true })).toBeVisible()
  await working.getByRole('button', { name: 'View job', exact: true }).first().click()
  await expect(page.getByRole('dialog').locator('header .badge')).toHaveText('Paused')
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'Resume queue', exact: true }).click()
  await expect(working.getByText('Now encoding', { exact: true })).toBeVisible()
})

test('empty history filters retain controls and explain that work is still running', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Transcoding', null), progress: .6 }, job(2, 'Queued', null)] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Failed · 0', exact: true }).click()
  await expect(page.getByText('No jobs match this filter.', { exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Working now' })).toBeVisible()
  await page.getByRole('button', { name: 'All · 1', exact: true }).click()
  await expect(page.locator('tbody tr')).toHaveCount(1)
})

test('missing posters and long paths keep the job controls usable on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 667 })
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Transcoding', null), relativePath: 'Films/' + 'A long media filename '.repeat(10) + '.mkv', progress: .64 }] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details.getByRole('button', { name: 'Stop & remove' })).toBeInViewport()
  await expect(details.getByRole('button', { name: 'Close details' })).toBeInViewport()
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', 375)
  const close = await details.getByRole('button', { name: 'Close details' }).boundingBox()
  expect(close!.x + close!.width).toBeLessThanOrEqual(375)
})

async function queueHub(page: Page) {
  const sockets: WebSocketRoute[] = []
  await page.route('**/hubs/jobs/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'queue-test', connectionToken: 'queue-test',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }))
  await page.routeWebSocket('**/hubs/jobs?*', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) { socket.send('{}\u001e'); sockets.push(socket) }
    })
  })
  return async (target: string, value?: unknown) => {
    await expect.poll(() => sockets.length).toBeGreaterThanOrEqual(2)
    sockets.forEach(socket => socket.send(JSON.stringify({ type: 1, target, arguments: value === undefined ? [] : [value] }) + '\u001e'))
  }
}

test('a queue refresh cannot rewind telemetry received while the request was in flight', async ({ page }) => {
  const fixture = { jobs: [{ ...job(1, 'Transcoding', null), progress: .3 }] }
  await mockWorkingQueue(page, fixture)
  const send = await queueHub(page)
  await page.goto('/#/queue')
  const bar = page.getByRole('region', { name: 'Working now' }).getByRole('progressbar')
  await expect(bar).toHaveAttribute('aria-valuenow', '30')
  let held: Route | undefined
  await page.route('**/api/jobs', route => { held = route })
  await send('jobsChanged')
  await expect.poll(() => Boolean(held)).toBe(true)
  await send('jobProgress', { jobId: 1, progress: .68, fps: 24, speed: 2, etaSeconds: 120 })
  await expect(bar).toHaveAttribute('aria-valuenow', '68')
  await json(held!, fixture.jobs)
  await expect(bar).toHaveAttribute('aria-valuenow', '68')
  await page.unroute('**/api/jobs')
  fixture.jobs = [{ ...fixture.jobs[0], status: 'Verifying', progress: .12 }]
  await send('jobsChanged')
  await expect(bar).toHaveAttribute('aria-valuenow', '12')
  await expect(page.getByRole('region', { name: 'Working now' })).not.toContainText('2.00×')
})

test('a completed job moves into history while its open details remain current', async ({ page }) => {
  const fixture = { jobs: [{ ...job(1, 'Transcoding', null), progress: .9 }] }
  await mockWorkingQueue(page, fixture)
  const send = await queueHub(page)
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  await send('jobProgress', { jobId: 1, progress: .95, fps: 24, speed: 2, etaSeconds: 20 })
  await expect(page.getByRole('dialog').getByRole('progressbar')).toHaveAttribute('aria-valuenow', '95')
  fixture.jobs = [{ ...job(1, 'ReadyToReplace', true), progress: 1 }]
  await send('jobsChanged')
  await expect(page.getByRole('region', { name: 'Working now' })).toHaveCount(0)
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details.getByRole('button', { name: 'Replace original' })).toBeVisible()
  await expect(page.locator('tbody tr')).toHaveCount(1)
  await details.getByRole('button', { name: 'Close details' }).click()
  await expect(page.locator('#queue-job-1')).toBeFocused()
  await page.locator('#queue-job-1').click()
  fixture.jobs = [{ ...job(1, 'Completed', true), progress: 1 }]
  await send('jobsChanged')
  await expect(details.locator('header .badge')).toHaveText('Completed')
  await expect(details.locator('footer')).toHaveCount(0)
  await page.keyboard.press('Escape')
})

test('failed stop keeps the job, exposes the error, and never sends the remove request', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Transcoding', null), progress: .4 }] })
  const requests: string[] = []
  await page.route('**/api/jobs/1**', route => {
    requests.push(route.request().method() + ' ' + new URL(route.request().url()).pathname)
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'Unable to stop encoder' }) })
  })
  page.on('dialog', dialog => dialog.accept())
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await details.getByRole('button', { name: 'Stop & remove', exact: true }).click()
  await expect(details).toContainText('Unable to stop encoder')
  expect(requests.some(request => request.startsWith('DELETE'))).toBe(false)
  await expect(details.getByRole('button', { name: 'Stop & remove', exact: true })).toBeEnabled()
})


test('opening a job far down the queue preserves the row and scroll position on Escape or backdrop dismissal', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: Array.from({ length: 80 }, (_, i) => job(i + 1, 'Queued', null)) })
  await page.goto('/#/queue')
  const row = page.locator('#queue-job-75')
  await row.scrollIntoViewIfNeeded()
  const before = await page.locator('main').evaluate(el => el.scrollTop)
  const rowBefore = await row.boundingBox()
  expect(before).toBeGreaterThan(2000)
  for (const dismissal of ['escape', 'backdrop']) {
    await row.click()
    const dialog = page.getByRole('dialog', { name: 'Job details Film 75' })
    await expect(dialog).toBeInViewport({ ratio: 1 })
    expect(await dialog.evaluate(el => el.matches(':modal'))).toBe(true)
    await expect(dialog.getByRole('button', { name: 'Close details' })).toBeInViewport()
    expect(await page.locator('main').evaluate(el => el.scrollTop)).toBeCloseTo(before, 0)
    if (dismissal === 'escape') await page.keyboard.press('Escape')
    else await page.mouse.click(2, 2)
    await expect(dialog).toHaveCount(0)
    await expect(row).toBeFocused()
    expect(await page.locator('main').evaluate(el => el.scrollTop)).toBeCloseTo(before, 0)
    expect((await row.boundingBox())!.y).toBeCloseTo(rowBefore!.y, 0)
  }
})

test('job dialog contains keyboard focus and keeps artwork and actions visible in a short window', async ({ page }) => {
  await page.setViewportSize({ width: 667, height: 375 })
  await mockWorkingQueue(page, { jobs: [{ ...job(1, 'Failed', false), relativePath: 'Films/' + 'A long name '.repeat(20) + '.mkv', verificationReportJson: JSON.stringify({ checks: [{ name: 'Perceptual quality (VMAF)', outcome: 'Failed', detail: 'Score 84; target 93.' }] }) }] })
  await page.route('**/api/media/*/thumbnail', route => route.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#52748c"/></svg>' }))
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').focus()
  await page.keyboard.press('Enter')
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeInViewport({ ratio: 1 })
  await expect(dialog.locator('[data-thumbnail] img')).toHaveCSS('opacity', '1')
  await expect(dialog.getByRole('button', { name: 'Close details' })).toBeInViewport()
  await expect(dialog.getByRole('button', { name: 'Retry at higher quality' })).toBeInViewport()
  await expect(dialog.getByRole('button', { name: 'Remove from queue' })).toBeInViewport()
  for (let i = 0; i < 10; i++) {
    await page.keyboard.press('Tab')
    // Native dialogs allow browser-chrome focus, but never a background control.
    expect(await dialog.evaluate(el => el.contains(document.activeElement) || document.activeElement === document.body)).toBe(true)
  }
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
})

test('loaded queue artwork stays visible across progress updates and queue refreshes', async ({ page }) => {
  const fixture = { jobs: [
    { ...job(1, 'Transcoding', null), progress: .3 },
    { ...job(2, 'Verifying', null), progress: .2 },
    job(3, 'Queued', null),
  ] }
  await mockWorkingQueue(page, fixture)
  const send = await queueHub(page)
  await page.route('**/api/media/*/thumbnail', route => route.fulfill({
    contentType: 'image/svg+xml',
    body: '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#52748c"/></svg>',
  }))
  await page.goto('/#/queue')
  const posters = page.locator('main [data-thumbnail] img')
  await expect(posters).toHaveCount(3)
  for (const poster of await posters.all()) await expect(poster).toHaveCSS('opacity', '1')
  await page.getByRole('button', { name: 'View job', exact: true }).first().click()
  await expect(posters).toHaveCount(4)
  for (const poster of await posters.all()) await expect(poster).toHaveCSS('opacity', '1')
  const originalImages = await posters.elementHandles()
  for (let cycle = 1; cycle <= 3; cycle++) {
    await send('jobProgress', { jobId: 1, progress: .3 + cycle / 10, fps: 24, speed: 2, etaSeconds: 120 })
    fixture.jobs = fixture.jobs.map(item => ({ ...item, progress: item.id === 1 ? .3 + cycle / 10 : item.progress }))
    const refreshed = page.waitForResponse('**/api/jobs')
    await send('jobsChanged')
    await refreshed
    await expect(page.getByRole('region', { name: 'Working now' }).getByRole('progressbar').first()).toHaveAttribute('aria-valuenow', String(30 + cycle * 10))
    for (const poster of await posters.all()) await expect(poster).toHaveCSS('opacity', '1')
    for (const image of originalImages) expect(await image.evaluate(element => element.isConnected)).toBe(true)
  }
})

test('a missing working poster stays settled through refreshes and recovers for the next media item', async ({ page }) => {
  const fixture = { jobs: [{ ...job(1, 'Transcoding', null), progress: .3 }] }
  await mockWorkingQueue(page, fixture)
  const send = await queueHub(page)
  let missingRequests = 0
  await page.route('**/api/media/*/thumbnail', route => {
    if (route.request().url().includes('/media/1/')) {
      missingRequests++
      return route.fulfill({ status: 404 })
    }
    return route.fulfill({ contentType: 'image/png', path: 'public/favicon-192.png' })
  })
  await page.goto('/#/queue')
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working.locator('[data-thumbnail]')).toBeVisible()
  await expect(working.locator('img')).toHaveCount(0)
  const initialRequests = missingRequests
  fixture.jobs = [{ ...fixture.jobs[0], progress: .5 }]
  await send('jobsChanged')
  await expect(working.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
  await expect(working.locator('img')).toHaveCount(0)
  expect(missingRequests).toBe(initialRequests)
  fixture.jobs = [{ ...job(2, 'Transcoding', null), progress: .1 }]
  await send('jobsChanged')
  await expect(working.locator('img')).toHaveAttribute('src', '/api/media/2/thumbnail')
  await expect(working.locator('img')).toHaveJSProperty('naturalWidth', 192)
  await expect(working.locator('img')).toHaveCSS('opacity', '1')
})
