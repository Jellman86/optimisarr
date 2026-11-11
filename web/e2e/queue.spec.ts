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
    appliedAudioFilter: null,
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
  await queueHub(page)
  let jobs = [
    job(1, 'ReadyToReplace', true),
    job(2, 'ReadyToReplace', true),
    job(3, 'Queued', null),
  ]
  let bulkRequests = 0

  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
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

test('replace all confirms on page, replaces every ready job in one request, and opens quarantine once', async ({ page }) => {
  const bulkRequests = await mockQueue(page)
  page.on('dialog', () => { throw new Error('Replacement must not rely on browser dialogs') })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Replace all (2)' }).click()
  const confirmation = page.locator('#queue-replacement-dialog')
  await expect(confirmation).toContainText('Replace all 2 verified outputs?')
  expect(bulkRequests()).toBe(0)
  await confirmation.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(page).toHaveURL(/#\/quarantine$/)
  expect(bulkRequests()).toBe(1)
})

test('replacement refusal stays visible above the job details and allows a retry', async ({ page }) => {
  await mockQueue(page)
  let requests = 0
  await page.route('**/api/jobs/1/replace', async route => {
    requests++
    if (requests === 1) return route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({
      error: 'Dry-run mode is enabled, so Optimisarr will stop after verification and leave originals untouched.'
    }) })
    return json(route, { id: 1, status: 'Replaced' })
  })
  page.on('dialog', () => { throw new Error('Replacement must not rely on browser dialogs') })
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  await page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' }).click()
  const confirmation = page.locator('#queue-replacement-dialog')
  await expect(confirmation).toBeVisible()
  await confirmation.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(confirmation.getByRole('alert')).toContainText('Dry-run mode is enabled')
  await expect(page.locator('#queue-job-dialog')).toContainText('Ready to replace')
  await confirmation.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(page).toHaveURL(/#\/quarantine$/)
  expect(requests).toBe(2)
})

test('cancelling on-page replacement leaves verified jobs untouched', async ({ page }) => {
  const bulkRequests = await mockQueue(page)
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Replace all (2)' }).click()
  await page.locator('#queue-replacement-dialog').getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page.locator('#queue-replacement-dialog')).toHaveCount(0)
  expect(bulkRequests()).toBe(0)
  await expect(page.getByRole('button', { name: 'Replace all (2)' })).toBeVisible()
})

test('bulk replacement keeps each refusal reason visible and retries remaining jobs', async ({ page }) => {
  await mockQueue(page)
  let requests = 0
  await page.route('**/api/jobs/replace-ready', async route => {
    requests++
    if (requests === 1) {
      await page.route('**/api/jobs', route => json(route, [job(2, 'ReadyToReplace', true), job(3, 'Queued', null)]))
      return json(route, { attempted: 2, replaced: 1, failures: [{ jobId: 2, message: 'Dry-run mode is enabled; the original was not replaced.' }] })
    }
    return json(route, { attempted: 1, replaced: 1, failures: [] })
  })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Replace all (2)' }).click()
  const dialog = page.locator('#queue-replacement-dialog')
  await dialog.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('1 ready job(s) could not be replaced')
  await expect(dialog.getByRole('alert')).toContainText('Film 2.mkv')
  await expect(dialog.getByRole('alert')).toContainText('Dry-run mode is enabled')
  await expect(dialog).toContainText('Replace all 1 verified outputs?')
  await dialog.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(page).toHaveURL(/#\/quarantine$/)
  expect(requests).toBe(2)
})

test('an empty bulk replacement result closes confirmation without implying a replacement', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs/replace-ready', async route => {
    await page.route('**/api/jobs', route => json(route, [job(3, 'Queued', null)]))
    return json(route, { attempted: 0, replaced: 0, failures: [] })
  })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'Replace all (2)' }).click()
  await page.locator('#queue-replacement-dialog').getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(page.locator('#queue-replacement-dialog')).toHaveCount(0)
  await expect(page).toHaveURL(/#\/queue$/)
  await expect(page.getByRole('button', { name: /Replace all/ })).toHaveCount(0)
})

test('a pending replacement ignores repeated submission and Escape until its result arrives', async ({ page }) => {
  await mockQueue(page)
  let pending: Route | undefined
  let requests = 0
  await page.route('**/api/jobs/1/replace', route => { requests++; pending = route })
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  await page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' }).click()
  const dialog = page.locator('#queue-replacement-dialog')
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused()
  await page.keyboard.press('Tab')
  await page.keyboard.press('Enter')
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.keyboard.press('Enter')
  await page.keyboard.press('Escape')
  await expect(dialog).toBeVisible()
  await expect(dialog).toHaveAttribute('aria-busy', 'true')
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled()
  expect(requests).toBe(1)
  await pending!.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'The source changed; re-verify it first.' }) })
  await expect(dialog.getByRole('alert')).toContainText('The source changed')
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' })).toBeFocused()
})

test('long replacement refusals keep cancel reachable in a short narrow viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 375 })
  await mockQueue(page)
  await page.route('**/api/jobs/1/replace', route => route.fulfill({
    status: 409, contentType: 'application/json',
    body: JSON.stringify({ error: 'The retained source changed. ' + 'Keep both files and re-verify the candidate before retrying. '.repeat(12) }),
  }))
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  await page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' }).click()
  const dialog = page.locator('#queue-replacement-dialog')
  await dialog.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('The retained source changed')
  const cancel = dialog.getByRole('button', { name: 'Cancel', exact: true })
  const bounds = (await dialog.boundingBox())!
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)
  await page.mouse.wheel(0, 5_000)
  await expect(cancel).toBeInViewport()
  await cancel.click()
  await expect(dialog).toHaveCount(0)
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
    if (path === '/api/diagnostics/captures') return json(route, [])
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
  await expect(page.getByText('Waiting for this server’s verdict · MacBook Air', { exact: true })).toBeVisible()

  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working).toContainText('encoding on Mac Studio')
  await expect(working).toContainText('62%')
  const rows = page.locator('tbody tr')
  await expect(rows.filter({ hasText: 'Chicago Fire' })).toHaveCount(0)
  await expect(rows.filter({ hasText: 'Severance' })).toContainText('waiting for a worker')
})

test('a job a worker failed on QSV says so, shows what FFmpeg said and is retried without QSV', async ({ page }) => {
  const failed = {
    number: 1, workerName: 'Boothby', videoEncoder: 'hevc_qsv', hardwareDecoder: 'qsv',
    startedAt: '2026-11-09T02:03:45Z', endedAt: '2026-11-09T02:11:08Z', verificationPassed: null,
    verificationReportJson: null, verifiedAt: null, outputSizeBytes: null, outcome: 'Failed', reason: 'IntelQsvTimestamps',
    processLog: 'FFmpeg exited with code 234.\n[vost#0:0/hevc_qsv @ 0x5626] Non-monotonic DTS; Error submitting a packet to the muxer: Invalid argument',
  }
  const retried = {
    ...job(8, 'Transcoding', null), relativePath: 'Pokémon S20E35.mkv', workerName: 'Boothby', videoEncoder: 'hevc_vaapi',
    executionAttempt: 2, retryReason: 'AvoidIntelQsv', attemptHistoryJson: JSON.stringify([failed]), outputSizeBytes: null,
  }
  await mockWorkingQueue(page, { jobs: [retried] })
  await page.goto('/#/queue')
  await page.getByRole('region', { name: 'Working now' }).getByRole('button', { name: 'View job' }).click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details).toContainText('Retrying without Intel QSV')
  await expect(details).toContainText("Intel QSV on Boothby stopped because this file's picture times jump")
  const attempts = details.getByRole('list', { name: 'Attempts' })
  await expect(attempts).toContainText('Attempt 1 · Failed attempt')
  await expect(attempts).toContainText("Intel QSV stopped on the file's timestamps")
  await expect(attempts).toContainText('Boothby · hevc_qsv')
  await expect(details.getByText(/Non-monotonic DTS/)).toBeHidden()
  await attempts.getByText('What FFmpeg said').click()
  await expect(details.getByText(/Non-monotonic DTS/)).toBeVisible()
})

test('a software-decode retry shows its current worker and keeps the rejected Mac checks in history', async ({ page }) => {
  const previous = {
    number: 1, workerName: 'MacBook Air', videoEncoder: 'hevc_videotoolbox',
    hardwareDecoder: 'videotoolbox', startedAt: '2026-09-20T22:08:00Z', endedAt: '2026-09-20T22:20:00Z',
    verificationPassed: false, verificationReportJson: JSON.stringify({ checks: [{ name: 'Decode health', outcome: 'Failed', detail: 'Frame mismatch.' }] }),
    verifiedAt: '2026-09-20T22:20:00Z', outputSizeBytes: 1234, outcome: 'Rejected', reason: 'HardwareDecodeCorruption',
  }
  const retried = {
    ...job(8, 'AwaitingVerification', null), relativePath: 'Mad Men S02E06.mkv',
    workerName: 'PICARD', videoEncoder: 'hevc_nvenc', executionAttempt: 2,
    retryReason: 'SoftwareDecode', attemptHistoryJson: JSON.stringify([previous]),
    outputSizeBytes: null, verifiedAt: null,
  }
  await mockWorkingQueue(page, { jobs: [retried] })
  await page.goto('/#/queue')
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working).toContainText('Waiting for this server’s verdict · PICARD')
  await expect(working).toContainText('Retrying with software decode')
  await working.getByRole('button', { name: 'View job' }).click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details).toContainText('Attempt 2')
  await expect(details).toContainText('hevc_nvenc')
  await expect(details).toContainText('The candidate from MacBook Air (hevc_videotoolbox) failed verification')
  const attempts = details.getByRole('list', { name: 'Attempts' })
  await expect(attempts).toContainText('Attempt 2 · Current attempt')
  await expect(attempts).toContainText('Attempt 1 · Rejected candidate')
  await expect(attempts).toContainText('Hardware decode corruption')
  await expect(attempts).toContainText('PICARD · hevc_nvenc')
  await expect(attempts).toContainText('MacBook Air · hevc_videotoolbox')
  await expect(details.getByText('Frame mismatch.')).toBeHidden()
  await attempts.getByText('Verification checks').click()
  await expect(details.getByText('Frame mismatch.')).toBeVisible()
})

test('a job skipped before encoding says why', async ({ page }) => {
  const skipped = { ...job(55, 'Cancelled', null), errorMessage: 'Skipped before encoding: Interlaced video (field order tb): re-encoding would bake in the combing and drop the interlace flag, so it is left untouched' }
  await mockWorkingQueue(page, { jobs: [skipped] })
  await page.goto('/#/queue?filter=all')
  await page.getByRole('button', { name: new RegExp(skipped.relativePath ?? 'Example') }).first().click()

  await expect(page.getByTestId('job-cancelled-reason')).toContainText('Interlaced video')
})

test('job detail explains why the quality used differs from the one requested', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [
    { ...job(8, 'Failed', false), videoEncoder: 'hevc_qsv', videoQualityMode: 'ICQ', requestedVideoQuality: 24, effectiveVideoQuality: 20 },
    job(9, 'Failed', false),
  ] })
  await page.goto('/#/queue')
  await page.locator('#queue-job-8').click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details.getByText('ICQ 20')).toBeVisible()
  await expect(details.getByText('requested 24')).toBeVisible()
  await details.getByRole('button', { name: 'About: Video quality', exact: true }).focus()
  await expect(details.getByRole('tooltip')).toContainText('hardware encoders start a few points lower')

  await page.keyboard.press('Escape')
  await page.keyboard.press('Escape')
  await expect(details).toBeHidden()
  await page.locator('#queue-job-9').click()
  await expect(details.getByText('crf 24')).toBeVisible()
  await expect(details.getByRole('button', { name: 'About: Video quality', exact: true })).toHaveCount(0)
})

test('job detail downloads only a matching opt-in diagnostic capture', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [job(8, 'Failed', false), job(9, 'Failed', false)] })
  let releaseLookup!: () => void
  const lookup = new Promise<void>(resolve => { releaseLookup = resolve })
  await page.route('**/api/diagnostics/capture', async route => {
    await lookup
    return json(route, {
      id: 'capture-1', status: 'Recording', scopedJobId: 8, startedAt: '2026-09-20T22:00:00Z',
      expiresAt: null, stoppedAt: null, includePaths: false, eventsStored: 4, maximumEvents: 10_000,
      eventLimitReached: false,
    })
  })
  await page.route('**/api/diagnostics/capture/capture-1/jobs/8/bundle', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{"jobId":8}' }))

  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const otherDetails = page.getByRole('dialog', { name: /Job details/ })
  await expect(otherDetails.locator('.queue-diagnostic-action').getByRole('status')).toContainText('Loading…')
  await expect(otherDetails.getByRole('button', { name: 'Open diagnostic settings' })).toHaveCount(0)
  releaseLookup()
  await expect(otherDetails.getByRole('button', { name: 'Download diagnostics' })).toHaveCount(0)
  await otherDetails.getByRole('button', { name: 'Open diagnostic settings' }).click()
  await expect(page).toHaveURL(/#\/settings\/system#diagnostic-capture$/)
  await expect(page.locator('#diagnostic-capture')).toBeInViewport()
  await page.goto('/#/queue')
  await page.locator('#queue-job-8').click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  const downloadButton = details.getByRole('button', { name: 'Download diagnostics' })
  await expect(downloadButton).toBeVisible()
  const [download] = await Promise.all([page.waitForEvent('download'), downloadButton.click()])
  expect(download.suggestedFilename()).toBe('optimisarr-diagnostics-8-capture-1.json')
})

const clearQueue = {
  canStart: true, manuallyPaused: false, manualPauseMode: 'inactive', runningEncodesSuspended: false,
  suspendedEncodeCount: 0, pauseFailedEncodeCount: 0, runningJobs: 1, hardwareAccelerated: true,
  freeDiskBytes: 100_000_000_000, blockedReason: null, waitingReason: null,
}

async function mockWorkingQueue(page: Page, fixture: { jobs: ReturnType<typeof job>[]; queue?: Record<string, unknown> }) {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
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

for (const scenario of [
  { name: 'mobile tap', width: 390, height: 844, keyboard: false },
  { name: 'desktop keyboard', width: 1280, height: 900, keyboard: true },
  { name: 'mobile landscape with reduced motion', width: 844, height: 390, keyboard: false },
]) {
  test(`Needs review brings held jobs into view with ${scenario.name}`, async ({ page }) => {
    await page.setViewportSize({ width: scenario.width, height: scenario.height })
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await mockWorkingQueue(page, { jobs: [
      { ...job(1, 'Transcoding', null), progress: .4 },
      { ...job(2, 'Leased', null), workerName: 'PICARD', remoteStage: 'Encoding', progress: .3 },
      { ...job(3, 'Leased', null), workerName: 'Mac', remoteStage: 'Verifying', progress: .2 },
      ...Array.from({ length: 105 }, (_, index) => job(100 + index, 'Completed', true)),
      job(19, 'AwaitingSizeReview', null),
      job(20, 'AwaitingSizeReview', null),
    ], queue: { workloadLanes: [
      { lane: 'Video', active: 1, capacity: 1, waiting: 0, reason: null },
      { lane: 'Evidence', active: 0, capacity: 2, waiting: 0, reason: null },
      { lane: 'Workers', active: 2, capacity: 3, waiting: 0, reason: null },
    ] } })
    await page.goto('/#/queue')
    await page.getByRole('button', { name: 'Next page', exact: true }).click()
    const alert = page.locator('.queue-review-alert')
    if (scenario.keyboard) {
      await alert.focus()
      await page.keyboard.press('Enter')
    } else {
      await alert.click()
    }
    await expect(page.locator('#queue-job-19')).toBeInViewport()
    const review = page.getByRole('region', { name: 'Needs review', exact: true })
    await expect(review.getByRole('heading', { name: 'Needs review', exact: true })).toBeFocused()
    await expect(review.locator('tbody tr')).toHaveCount(2)
    await expect(review.getByRole('button', { name: 'Needs review · 2', exact: true })).toHaveAttribute('aria-pressed', 'true')
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await page.locator('#queue-job-19').click()
    await expect(page.getByRole('dialog').getByText('Full encode paused for size review')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.locator('#queue-job-19')).toBeFocused()
  })
}

test('a size preflight hold explains the estimate and requeues only after confirmation', async ({ page }) => {
  let held = { ...job(19, 'AwaitingSizeReview', null), progress: 0,
    errorMessage: 'Three video-only quality samples project video data at about 150% of the source file.' }
  let approvals = 0
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { completed: true, completedStep: 5, stepCount: 5 })
    if (path === '/api/jobs') return json(route, [held])
    if (path === '/api/queue/status') return json(route, { ...clearQueue, runningJobs: 0 })
    if (path === '/api/jobs/19/approve-size-preflight' && route.request().method() === 'POST') {
      approvals += 1
      held = { ...held, status: 'Queued', errorMessage: null }
      return json(route, { id: 19, status: 'Queued' })
    }
    return route.fulfill({ status: 404, body: '{}' })
  })
  page.on('dialog', async dialog => {
    expect(dialog.message()).toContain('Final size and quality gates still apply')
    await dialog.accept()
  })

  await page.goto('/#/queue')
  await page.locator('.queue-review-alert').click()
  await page.locator('#queue-job-19').click()
  await expect(page.locator('#queue-job-dialog').getByText('Full encode paused for size review')).toBeVisible()
  await expect(page.getByText(/project video data at about 150%/)).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  const dialogBox = await page.locator('#queue-job-dialog').boundingBox()
  expect(dialogBox).not.toBeNull()
  expect(dialogBox!.x).toBeGreaterThanOrEqual(0)
  expect(dialogBox!.x + dialogBox!.width).toBeLessThanOrEqual(390)
  await expect(page.getByRole('button', { name: 'Encode anyway' })).toBeVisible()
  await page.getByRole('button', { name: 'Encode anyway' }).click()
  await expect(page.getByText('Review size')).toHaveCount(0)
  expect(approvals).toBe(1)
})

test('predicted oversize is a failure with its estimate and no encode-anyway action', async ({ page }) => {
  const failed = { ...job(19, 'Failed', null), progress: 0, outputSizeBytes: null,
    failureCategory: 'SizeSaving',
    errorMessage: 'Size saving prediction: samples project 130% of the source. The full encode was not run. The original is unchanged.' }
  await mockWorkingQueue(page, { jobs: [failed] })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/#/queue')
  await expect(page.locator('.queue-review-alert')).toHaveCount(0)
  await page.locator('#queue-job-19').click()
  const dialog = page.locator('#queue-job-dialog')
  await expect(dialog.getByText(/Size saving prediction: samples project 130%/)).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Encode anyway' })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Retry', exact: true })).toBeVisible()
  const box = await dialog.boundingBox()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(390)
})

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
  const poster = page.getByRole('region', { name: 'Working now' }).locator('[data-thumbnail] img')
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

test('strict evidence and legacy media verification show distinct phases beside lane capacity', async ({ page }) => {
  const strict = { ...job(10, 'Verifying', null), workerName: 'PICARD', sidecarVerification: true, progress: .3 }
  const legacy = { ...job(11, 'Verifying', null), workerName: 'MacBook Air', sidecarVerification: false, progress: .4 }
  await mockWorkingQueue(page, { jobs: [strict, legacy], queue: { workloadLanes: [
    { lane: 'Video', active: 1, capacity: 1, waiting: 2, reason: 'All video slots are busy.' },
    { lane: 'NonVideo', active: 0, capacity: 1, waiting: 1, reason: 'Checking library windows.' },
    { lane: 'Evidence', active: 1, capacity: 2, waiting: 0, reason: null },
    { lane: 'Finalization', active: 2, capacity: 2, waiting: 1, reason: 'Both finalisation slots are busy.' },
    { lane: 'Workers', active: 0, capacity: 2, waiting: 0, reason: null },
  ] } })
  await page.goto('/#/queue')
  const lanes = page.getByRole('region', { name: 'Where work runs' })
  await expect(lanes).toContainText('Video on this server')
  await expect(lanes).toContainText('Audio & images')
  await expect(lanes).toContainText('All video slots are busy.')
  await expect(lanes).toContainText('Replacing files')
  await expect(lanes).toContainText('Both finalisation slots are busy.')
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working).toContainText('Checking the worker’s results')
  await expect(working).toContainText('This server does not repeat the media checks.')
  await expect(working).toContainText('This server is verifying the file returned by MacBook Air.')
  await working.getByRole('button', { name: 'View job' }).first().click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details).toContainText('Checking the worker’s results')
  await expect(details.getByRole('region', { name: 'Execution path' })).toContainText('PICARD')
  await expect(details.getByRole('region', { name: 'Execution path' })).toContainText('This server')
  await details.getByRole('button', { name: 'Close details' }).click()
  await page.setViewportSize({ width: 375, height: 667 })
  await expect(lanes).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('safe replacement remains visible as work and cannot be started twice', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [{ ...job(12, 'ReadyToReplace', true), finalizing: true }] })
  await page.goto('/#/queue')
  const working = page.getByRole('region', { name: 'Working now' })
  await expect(working).toContainText('Finalising replacement')
  await expect(working).toContainText('The original is kept for rollback.')
  await working.getByRole('button', { name: 'View job' }).click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details.locator('header .badge')).toHaveText('Finalising replacement')
  await expect(details.getByRole('button', { name: 'Replace original' })).toHaveCount(0)
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

test('a short window scrolls the queue inside the tray instead of growing the page', async ({ page }) => {
  // The table header's screen-reader label is absolutely positioned, so it only stays inside
  // the tray's scroll range while main is the containing block for it. Without that, its box
  // lands below the fold, escapes the scroll container, and the document grows a scroll range
  // that shows nothing but blank background.
  await page.setViewportSize({ width: 812, height: 375 })
  await mockWorkingQueue(page, { jobs: [
    job(1, 'ReadyToReplace', true),
    job(2, 'Queued', null),
    job(3, 'Queued', null),
    job(4, 'Queued', null),
  ] })
  await page.goto('/#/queue')
  await expect(page.locator('#queue-list')).toBeVisible()
  // The section renders before its table; measure only once the rows (and the sr-only
  // header label above them) exist, or the assertions describe the loading layout.
  await expect(page.locator('#queue-list tbody tr')).toHaveCount(4)
  const pageRange = await page.evaluate(() => ({
    scrollable: document.documentElement.scrollHeight,
    viewport: window.innerHeight,
  }))
  expect(pageRange.scrollable, 'the document must not scroll past the viewport').toBeLessThanOrEqual(pageRange.viewport)
  const tray = await page.locator('main').evaluate(el => ({ scrollable: el.scrollHeight, visible: el.clientHeight }))
  expect(tray.scrollable, 'the tray is what scrolls, not the document').toBeGreaterThan(tray.visible)
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
  // The poster and the glow behind the card each ask once; neither may ask again.
  const initialRequests = missingRequests
  fixture.jobs = [{ ...fixture.jobs[0], progress: .5 }]
  await send('jobsChanged')
  await expect(working.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
  await expect(working.locator('img')).toHaveCount(0)
  expect(missingRequests).toBe(initialRequests)
  fixture.jobs = [{ ...job(2, 'Transcoding', null), progress: .1 }]
  await send('jobsChanged')
  const poster = working.locator('[data-thumbnail] img')
  await expect(poster).toHaveAttribute('src', '/api/media/2/thumbnail')
  await expect(poster).toHaveJSProperty('naturalWidth', 192)
  await expect(poster).toHaveCSS('opacity', '1')
  // The glow is the same artwork as ambience: hidden from assistive technology, never content.
  await expect(working.locator('.poster-glow')).toHaveAttribute('aria-hidden', 'true')
  await expect(working.locator('.poster-glow img')).toHaveAttribute('alt', '')
})

for (const width of [375, 1440]) {
  for (const theme of ['light', 'dark']) {
    for (const kind of ['Audio', 'Image']) {
      test(`fresh retry ${kind} distinguishes measured selection from its minimum at ${width}px ${theme}`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 })
        await page.emulateMedia({ colorScheme: theme as 'light' | 'dark', reducedMotion: 'reduce' })
        await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
        const minimum = kind === 'Audio' ? 112 : 70
        const selected = kind === 'Audio' ? 128 : 85
        const search = { kind, encoder: kind === 'Audio' ? 'aac' : 'libwebp', sourceSha256: 'a'.repeat(64),
          measurementLocation: 'Worker', minimumValue: minimum, minimumImageScore: 85, maximumAudioDistance: 0.02,
          audioWindows: [], result: { decision: { selectedValue: selected, nextValue: null, fellBack: false, reason: 'Measured again on the current encoding host.' },
            probes: [{ value: selected, encodedBytes: 1000, measurement: { available: true, meetsTarget: true, score: kind === 'Audio' ? 0.01 : 90 } }] } }
        await mockWorkingQueue(page, { jobs: [{ ...job(31, 'Failed', false), relativePath: 'Fresh retry fixture',
          verificationReportJson: JSON.stringify({ checks: [], perceptualSearch: search }) }] })
        await page.goto('/#/queue')
        await page.getByRole('button', { name: /^Fresh retry fixture/ }).click()
        const panel = page.getByRole('region', { name: 'Quality-first search', exact: true })
        const suffix = kind === 'Audio' ? ' kbps' : ''
        await expect(panel).toContainText(`Selected setting: ${selected}${suffix}`)
        await expect(panel).toContainText(`Minimum setting for the retry: ${minimum}${suffix}`)
        await expect(panel).toContainText('Higher-quality retry')
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
        await panel.screenshot({ path: testInfo.outputPath('fresh-retry-report.png') })
      })
    }
  }
}

test('a pending quality retry labels the earlier selection separately from the upcoming minimum', async ({ page }) => {
  const search = { kind: 'Audio', encoder: 'aac', sourceSha256: 'a'.repeat(64), measurementLocation: 'Server',
    retryValue: 112, minimumImageScore: null, maximumAudioDistance: 0.02, audioWindows: [],
    result: { decision: { selectedValue: 96, nextValue: null, fellBack: false, reason: 'Previous attempt selected 96 kbps.' }, probes: [] } }
  await mockWorkingQueue(page, { jobs: [{ ...job(32, 'Queued', false), relativePath: 'Pending retry fixture', perceptualSearchJson: JSON.stringify(search) }] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: /^Pending retry fixture/ }).click()
  const panel = page.getByRole('region', { name: 'Quality-first search', exact: true })
  await expect(panel).toContainText('Previous selected setting: 96 kbps')
  await expect(panel).toContainText('Minimum setting for the retry: 112 kbps')
  await expect(panel).not.toContainText('Selected setting: 112 kbps')
})

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    for (const available of [true, false]) {
      test(`image quality ${available ? 'measured' : 'unavailable'} report at ${width}px in ${theme}`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 })
        await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), theme)
        const image = { measurement: available ? { width: 96, height: 64, alpha: true, maximumAlphaError: 0, score: 82.125,
          backgroundScores: [82.125, 84] } : null, error: available ? null : 'Animated images are unsupported.',
          gateEnabled: true, minimumScore: 80, gatePassed: available }
        await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), relativePath: 'Synthetic.webp',
          verificationReportJson: JSON.stringify({ checks: [], imagePerceptualQuality: image }) }] })
        await page.goto('/#/queue')
        await page.getByRole('button', { name: 'View job', exact: true }).click()
        const panel = page.getByRole('region', { name: 'Image quality report', exact: true })
        if (available) {
          await expect(panel).toContainText('82.1')
          await expect(panel).toContainText('96 × 64')
        } else {
          await expect(panel).toContainText('Animated images are unsupported.')
          await expect(panel).not.toContainText('82.1')
        }
        await expect(panel).toContainText('Every configured check must pass')
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
        await panel.screenshot({ path: testInfo.outputPath('image-report.png') })
      })
    }
  }
}

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`audio report fits the job dialog at ${width}px in ${theme}`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 900 })
      await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), theme)
      const report = {
        checks: [{ name: 'Decode health', outcome: 'Passed', detail: 'Decoded cleanly.' }],
        audioQuality: { measurementLocation: 'Worker', unavailableReason: null,
          evidence: { metric: 'zimtohrli', revision: 'pinned', preparation: '48k', assessment: {
            measured: true, coveredSeconds: 90, elapsedSeconds: 2,
            windows: [{ window: { startSeconds: 0, durationSeconds: 30 }, distances: { frames: 1440000, channelDistances: [0.001234, 0.005678] } }],
          } },
        },
      }
      await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), relativePath: 'Free music.opus', verificationReportJson: JSON.stringify(report) }] })
      await page.goto('/#/queue')
      await page.getByRole('button', { name: 'View job', exact: true }).click()
      const panel = page.getByRole('region', { name: 'Audio quality report', exact: true })
      await expect(panel).toContainText('0.00123')
      await expect(panel).toContainText('0.00568')
      await expect(panel).toContainText('Report only')
      expect(await panel.locator('dl > div').first().evaluate(element => parseFloat(getComputedStyle(element).paddingLeft))).toBeGreaterThanOrEqual(12)
      await panel.scrollIntoViewIfNeeded()
      const box = await panel.boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width)
      expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
      await page.screenshot({ path: testInfo.outputPath('audio-report.png'), fullPage: true })
    })
  }
}

test('missing worker audio evidence shows an unavailable report with the reason', async ({ page }) => {
  const report = { checks: [{ name: 'Decode health', outcome: 'Passed', detail: 'Decoded cleanly.' }],
    audioQuality: { measurementLocation: 'Worker', evidence: null, unavailableReason: 'The worker returned no audio quality report.' } }
  await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), verificationReportJson: JSON.stringify(report) }] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  const panel = page.getByRole('region', { name: 'Audio quality report', exact: true })
  await expect(panel).toContainText('Unavailable')
  await expect(panel).toContainText('The worker returned no audio quality report.')
  await expect(panel).not.toContainText('Channel 1')
})

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`audio gate verdict and selected limit stay clear at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1100 })
      await page.addInitScript(selected => localStorage.setItem('optimisarr-theme', selected), theme)
      const report = { checks: [{ name: 'Perceptual audio quality (Zimtohrli)', outcome: 'Failed', detail: 'Above the selected limit. The original is unchanged.' }],
        audioQuality: { measurementLocation: 'Worker', unavailableReason: null, gateEnabled: true, gatePassed: false, maximumDistance: 0.005,
          evidence: { metric: 'zimtohrli', revision: 'pinned', preparation: '48k', assessment: { measured: true, coveredSeconds: 90,
            windows: [{ window: { startSeconds: 0, durationSeconds: 30 }, distances: { frames: 1440000, channelDistances: [0.001234, 0.005678] } }] } } } }
      await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), verificationReportJson: JSON.stringify(report) }] })
      await page.goto('/#/queue')
      await page.getByRole('button', { name: 'View job', exact: true }).click()
      const panel = page.getByRole('region', { name: 'Audio quality report', exact: true })
      await expect(panel).toContainText('Blocked')
      await expect(panel).toContainText('Maximum allowed difference: 0.005')
      await expect(panel).not.toContainText('Report only')
      await expect(panel.locator('.badge')).toHaveClass(/tone-bad/)
      await panel.scrollIntoViewIfNeeded()
      expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
    })
  }
}

test('audio gate blocks missing evidence', async ({ page }) => {
  const report = { checks: [{ name: 'Perceptual audio quality (Zimtohrli)', outcome: 'Failed', detail: 'Missing evidence.' }],
    audioQuality: { measurementLocation: 'Worker', evidence: null, unavailableReason: 'The worker returned no audio quality report.',
      gateEnabled: true, gatePassed: false, maximumDistance: 0 } }
  await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), verificationReportJson: JSON.stringify(report) }] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  const panel = page.getByRole('region', { name: 'Audio quality report', exact: true })
  await expect(panel).toContainText('Blocked')
  await expect(panel).toContainText('Maximum allowed difference: 0')
  await expect(panel).toContainText('The worker returned no audio quality report.')
})

test('a measured passing audio gate shows its verdict and limit', async ({ page }) => {
  const report = { checks: [{ name: 'Perceptual audio quality (Zimtohrli)', outcome: 'Passed', detail: 'Within the selected limit.' }],
    audioQuality: { measurementLocation: 'Worker', unavailableReason: null, gateEnabled: true, gatePassed: true, maximumDistance: 0.005,
      evidence: { metric: 'zimtohrli', revision: 'pinned', preparation: '48k', assessment: { measured: true, coveredSeconds: 30,
        windows: [{ window: { startSeconds: 0, durationSeconds: 30 }, distances: { frames: 1440000, channelDistances: [0.001234] } }] } } } }
  await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Verifying', null), verificationReportJson: JSON.stringify(report) }] })
  await page.goto('/#/queue')
  await page.getByRole('button', { name: 'View job', exact: true }).click()
  const panel = page.getByRole('region', { name: 'Audio quality report', exact: true })
  await expect(panel.locator('.badge')).toHaveText('Passed')
  await expect(panel.locator('.badge')).toHaveClass(/tone-ok/)
  await expect(panel).toContainText('Maximum allowed difference: 0.005')
  await expect(panel).not.toContainText('Report only')
  await panel.getByRole('button').click()
  await expect(panel.getByRole('tooltip')).toContainText('Blocks replacement')
  await expect(panel.getByRole('tooltip')).not.toContainText('does not pass or fail')
})

for (const width of [375, 1440]) {
  test(`soundtrack results identify separate languages and a failed channel at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    const waiting = job(4, 'Verifying', null)
    waiting.verificationReportJson = JSON.stringify({ checks: [{ name: 'Soundtrack 2', outcome: 'Failed', detail: 'Above limit' }],
      soundtrackQuality: { gateEnabled: true, gatePassed: false, maximumDistance: .005, unavailableReason: null,
        tracks: ['eng', 'fra'].map((language, index) => ({
          track: { sourceAudioIndex: index, candidateAudioIndex: index, language, title: index ? 'Commentary' : 'Main' },
          report: { measurementLocation: 'Worker', unavailableReason: null, gateEnabled: true, gatePassed: index === 0, maximumDistance: .005,
            evidence: { assessment: { measured: true, coveredSeconds: 3, windows: [{ distances: { channelDistances: index ? [.1, .2] : [.001, .002] } }] } } }
        })) } })
    await mockWorkingQueue(page, { jobs: [waiting] })
    await page.goto('/#/queue')
    await page.getByRole('button', { name: 'View job', exact: true }).click()
    const panel = page.getByRole('region', { name: 'Soundtrack quality report', exact: true })
    await expect(panel).toBeVisible()
    await expect(panel.getByText('Soundtrack 1 · eng', { exact: true })).toBeVisible()
    await expect(panel.getByText('Soundtrack 2 · fra', { exact: true })).toBeVisible()
    await expect(panel.getByText('Commentary', { exact: true })).toBeVisible()
    await expect(panel.getByText('0.2', { exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  })
}

test('a failure leads with its most fundamental cause and lists every failed check', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs/failures', (route) => json(route, [{
    category: 'SizeSaving',
    description: 'The attempt failed the configured size rule.',
    count: 1,
    samples: [{
      jobId: 41,
      mediaFileId: 41,
      relativePath: 'adult/Mad Men/Season 2/Mad Men - S02E06 - Maidenform Bluray-720p.mkv',
      jobType: 'Normal',
      errorMessage: 'Verification failed: Size saving; Source video timeline',
      verificationChecks: [
        { name: 'Size saving', outcome: 'Failed', detail: 'Output is not smaller than the original.' },
        { name: 'Source video timeline', outcome: 'Failed', detail: 'The source video spans 2898.395s while its primary audio spans 3268.863s.' },
      ],
    }],
  }]))

  await page.goto('/#/queue/failures')

  await expect(page.getByRole('heading', { name: 'The encode was not small enough to be worth keeping.' })).toBeVisible()
  const row = page.getByRole('listitem').filter({ hasText: 'Mad Men' })
  await expect(row).toContainText('Mad Men S02E06 · Maidenform')
  await expect(page.getByText('The source file looks damaged', { exact: true })).toBeVisible()
  // The gate details stay available, but behind the disclosure rather than in a red wall.
  await expect(page.getByText('The source video spans 2898.395s', { exact: false })).toBeHidden()
  await page.getByText('Technical detail').click()
  await expect(page.getByText('The source video spans 2898.395s', { exact: false })).toBeVisible()
})

test('a finished job says how big it was before and after', async ({ page }) => {
  await mockQueue(page)
  const finished = { ...job(51, 'Completed', true), sourceSizeBytes: 4_673_211_458, outputSizeBytes: 1_817_794_396, finishedAt: '2026-10-03T09:18:26Z' }
  const bigger = { ...job(52, 'Failed', false), sourceSizeBytes: 405_091_089, outputSizeBytes: 545_986_048, failureCategory: 'SizeSaving' }
  await page.route(/\/api\/jobs(\?.*)?$/, (route) => json(route, [finished, bigger]))

  await page.goto('/#/queue')
  await page.locator('#queue-job-51').click()
  const details = page.locator('#queue-job-dialog')
  await expect(details).toContainText('4.4 GB → 1.7 GB 61% smaller')
  await details.getByRole('button', { name: 'Close details' }).click()

  // A larger output gives both sizes and claims no saving.
  await page.locator('#queue-job-52').click()
  await expect(details).toContainText('386 MB → 521 MB')
  await expect(details).not.toContainText('smaller')
})


test('a job shows the audio filter its command applied beside the FFmpeg command', async ({ page }) => {
  await mockQueue(page)
  const remapped = {
    ...job(61, 'Completed', true),
    ffmpegArguments: '-i /media/Film 61.mkv -c:a libopus -af channelmap=channel_layout=5.1 /work/61.mkv',
    appliedAudioFilter: 'channelmap=channel_layout=5.1',
  }
  await page.route(/\/api\/jobs(\?.*)?$/, (route) => json(route, [remapped]))

  await page.goto('/#/queue')
  await page.locator('#queue-job-61').click()
  const details = page.locator('#queue-job-dialog')
  await expect(details).toContainText('Audio filter:')
  await expect(details).toContainText('channelmap=channel_layout=5.1')
})

test('retained capture shows historical gates after a newer capture targets another job', async ({ page }) => {
  await mockWorkingQueue(page, { jobs: [job(8, 'Failed', false)] })
  const older = { id: 'retained', status: 'Stopped', scopedJobId: 8, eventsStored: 1, startedAt: '2026-10-01T10:00:00Z', retainUntil: '2026-11-01T10:00:00Z' }
  await page.route('**/api/diagnostics/capture', route => json(route, { ...older, id: 'newer', scopedJobId: 9 }))
  await page.route('**/api/diagnostics/captures?jobId=8', route => json(route, [older]))
  await page.route('**/api/diagnostics/capture/retained/jobs/8/bundle', route => json(route, {
    manifest: { manifestId: 'retained-reference', schemaVersion: 4, omissions: [] }, job: { id: 8, path: '/private/title.mkv' }, attempts: [],
    events: [{ id: 1, receivedAt: '2026-10-01T10:00:00Z', occurredAt: '2026-10-01T10:00:00Z', attempt: 1, workerId: 4,
      source: 'Server', reasonCode: 'Job.StatusChanged', currentStatus: 'Failed', details: { report: { passed: false, location: 'Worker', checks: [{ name: 'TimestampIntegrity', outcome: 'Failed' }], vmafHarmonicMean: 97.5 } } }],
  }))
  await page.goto('/#/queue')
  await page.locator('#queue-job-8').click()
  const details = page.getByRole('dialog', { name: /Job details/ })
  await expect(details.getByRole('button', { name: 'Download diagnostics' })).toBeVisible()
  await details.getByText('Verification · Worker', { exact: true }).click()
  await expect(details.getByText('TimestampIntegrity', { exact: true })).toBeVisible()
  await expect(details.getByText('VMAF: 97.50')).toBeVisible()
})

for (const clipboard of ['missing', 'denied']) {
  test(`diagnostic summaries remain copyable when clipboard access is ${clipboard}`, async ({ page }) => {
    await page.addInitScript(mode => Object.defineProperty(navigator, 'clipboard', { configurable: true,
      value: mode === 'missing' ? undefined : { writeText: () => Promise.reject(new Error('Permission denied')) } }), clipboard)
    await mockWorkingQueue(page, { jobs: [job(8, 'Failed', false)] })
    const capture = { id: 'summary', status: 'Recording', scopedJobId: null, eventsStored: 146, startedAt: '2026-10-09T10:00:00Z' }
    await page.route('**/api/diagnostics/capture', route => json(route, capture))
    await page.route('**/api/diagnostics/captures?jobId=8', route => json(route, [capture]))
    await page.route('**/api/diagnostics/capture/summary/jobs/8/bundle', route => json(route, {
      manifest: { manifestId: 'summary-reference', schemaVersion: 4, omissions: [] }, job: { id: 8, path: '/private/title.mkv' }, attempts: [], events: [] }))
    await page.goto('/#/queue')
    await page.locator('#queue-job-8').click()
    const details = page.getByRole('dialog', { name: /Job details/ })
    await details.getByRole('button', { name: 'Copy issue summary', exact: true }).click()
    const summary = details.getByRole('textbox', { name: 'Copy issue summary', exact: true })
    await expect(summary).toBeVisible()
    await expect(summary).toHaveValue(/summary-reference/)
    expect(await summary.inputValue()).not.toContain('/private/title.mkv')
    await expect(details.getByText('No events were captured for this job.', { exact: true })).toBeVisible()
    await expect(details.locator('.queue-diagnostic-action')).not.toContainText('146 events')
  })
}

for (const width of [375, 1440]) {
  for (const kind of ['Audio', 'Image']) {
    test(`quality-first ${kind} evidence remains separate from final verification at ${width}px`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 900 })
      const theme = width === 375 ? 'dark' : 'light'
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
      const baseline = kind === 'Audio' ? 128 : 80
      const selected = kind === 'Audio' ? 96 : 60
      const search = { kind, encoder: kind === 'Audio' ? 'aac' : 'mjpeg', sourceSha256: 'a'.repeat(64),
        measurementLocation: 'Server', minimumImageScore: 80, maximumAudioDistance: 0.01,
        audioWindows: kind === 'Audio' ? [{ startSeconds: 0, durationSeconds: 30 }] : [],
        result: { decision: { selectedValue: selected, nextValue: null, fellBack: false, reason: 'The smallest measured passing candidate was selected.' },
          probes: [baseline, selected].map((value, index) => ({ value, encodedBytes: 1000 - index * 200,
            measurement: { available: true, meetsTarget: true, score: kind === 'Audio' ? 0.005 : 85.2, error: null } })) } }
      await mockWorkingQueue(page, { jobs: [{ ...job(15, 'Failed', false), relativePath: 'Synthetic source',
        verificationReportJson: JSON.stringify({ checks: [{ name: 'Final quality', outcome: 'Failed', detail: 'The finished candidate missed the required target.' }], perceptualSearch: search }) }] })
      await page.goto('/#/queue')
      await page.getByRole('button', { name: /^Synthetic source/ }).click()
      const panel = page.getByRole('region', { name: 'Quality-first search', exact: true })
      await expect(panel).toContainText(`Selected setting: ${selected}${kind === 'Audio' ? ' kbps' : ''}`)
      await expect(panel).toContainText(kind === 'Audio' ? 'Zimtohrli ≤ 0.01' : 'SSIMULACRA2 ≥ 80')
      await expect(panel).toContainText('Final verification decides replacement.')
      await expect(page.getByText('The finished candidate missed the required target.', { exact: true })).toBeVisible()
      expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
      await panel.screenshot({ path: testInfo.outputPath('quality-first-report.png') })
    })
  }
}

for (const state of ['Probing', 'Cancelled']) {
  test(`quality-first retains completed probes during ${state.toLowerCase()} without claiming selection`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 375, height: 900 })
    await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'light' })
    await page.addInitScript(() => localStorage.setItem('optimisarr.theme', 'light'))
    const search = { kind: 'Audio', encoder: 'aac', sourceSha256: 'a'.repeat(64), measurementLocation: 'Server',
      minimumImageScore: null, maximumAudioDistance: 0.01, audioWindows: [{ startSeconds: 0, durationSeconds: 30 }],
      result: { decision: { selectedValue: 128, nextValue: 112, fellBack: false, reason: 'Try stronger compression while the target is met.' },
        probes: [{ value: 128, encodedBytes: 1000, measurement: { available: true, meetsTarget: true, score: 0.005, error: null } }] } }
    await mockWorkingQueue(page, { jobs: [{ ...job(15, state, null), relativePath: 'Audio probe', perceptualSearchJson: JSON.stringify(search) }] })
    await page.goto('/#/queue')
    if (state === 'Probing') await page.getByRole('button', { name: 'View job', exact: true }).click()
    else await page.getByRole('button', { name: /^Audio probe/ }).click()
    const panel = page.getByRole('region', { name: 'Quality-first search', exact: true })
    await expect(panel).toContainText('Candidate: 128 kbps')
    await expect(panel).not.toContainText('Selected setting:')
    await expect(panel).toContainText(state === 'Probing' ? 'Searching for a setting' : 'Search stopped before selection')
    await expect(panel).toContainText('Final verification decides replacement.')
    expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    await panel.screenshot({ path: testInfo.outputPath('quality-first-partial-light.png') })
  })
}

for (const width of [375, 1440]) {
  for (const theme of ['light', 'dark']) {
    test(`resource wait preserves the queued state at ${width}px ${theme}`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 900 })
      await page.emulateMedia({ colorScheme: theme as 'light' | 'dark', reducedMotion: 'reduce' })
      await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
      await mockWorkingQueue(page, { jobs: [{ ...job(46, 'Queued', null), relativePath: 'Image waiting safely', videoEncoder: null, requestedVideoQuality: null, effectiveVideoQuality: null, videoQualityMode: null, outputSizeBytes: null, waitingForResources: true }] })
      await page.goto('/#/queue')
      await expect(page.getByText('waiting for memory or space…')).toBeVisible()
      await page.getByRole('button', { name: /^Image waiting safely/ }).click()
      const dialog = page.getByRole('dialog')
      await expect(dialog.getByText('Waiting for memory or working space')).toBeVisible()
      await expect(dialog.getByText(/Your original and retry settings are preserved/)).toBeVisible()
      await expect(dialog.getByRole('button', { name: 'Retry', exact: true })).toHaveCount(0)
      await expect(dialog.getByText('Queued', { exact: true })).toBeVisible()
      expect(await page.locator('body').evaluate(el => el.scrollWidth <= window.innerWidth)).toBe(true)
      await page.screenshot({ path: testInfo.outputPath('resource-wait.png'), fullPage: true })
    })
  }
}

const nothingPending = { retentionDays: 30, originalsToDelete: 0, originalBytes: 0, duplicatesToDelete: 0, duplicateBytes: 0, librariesCleaningDuplicatesDaily: 0, jobsToReplace: 0, needsConfirmation: false }

async function mockReplacementSettings(
  page: Page,
  settings: Record<string, unknown>,
  save?: (route: Route) => Promise<void> | void,
  unlockEffects: Record<string, unknown> | null = nothingPending,
) {
  let current = settings
  const saved: Record<string, unknown>[] = []
  // What unlocking would start. Null stands for a server that cannot say.
  await page.route('**/api/settings/dry-run-unlock', route => unlockEffects
    ? json(route, unlockEffects)
    : route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }))
  await page.route('**/api/settings', async route => {
    if (route.request().method() !== 'PUT') return json(route, current)
    saved.push(route.request().postDataJSON())
    if (save) return save(route)
    current = route.request().postDataJSON()
    return json(route, current)
  })
  return saved
}

function failedJob(failedCheck: string, detail = 'Below what this library asks for.', extra: Record<string, unknown> = {}) {
  return {
    ...job(9, 'Failed', false),
    relativePath: 'Film 9.mkv', failureCategory: 'Verification', clearable: true,
    errorMessage: `Verification failed: ${failedCheck}`,
    verificationReportJson: JSON.stringify({ checks: [
      { name: 'Decode health', outcome: 'Passed', detail: 'Complete decode finished without errors.' },
      { name: failedCheck, outcome: 'Failed', detail },
    ], ...extra }),
  }
}

test('a passed job says what replacing does beside the button', async ({ page }) => {
  await mockQueue(page)
  await mockReplacementSettings(page, { dryRunMode: false, replacementQuarantineRetentionDays: 30 })
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  const result = page.locator('#queue-job-dialog [data-job-result]')

  await expect(result).toHaveAttribute('data-job-result', 'passed')
  await expect(result.getByRole('heading', { name: 'Every check passed' })).toBeVisible()
  await expect(result).toContainText('The original has not been touched.')
  await expect(result).toContainText('The original moves to Quarantine. The new file takes its place.')
  await expect(result.locator('[data-replace-undo]')).toHaveText('You can put the original back for 30 days. After that it is deleted and this cannot be undone.')
  await expect(result).toContainText('You get the space back when the original is deleted, not before.')
  await expect(result.getByRole('button', { name: 'Replace original' })).toBeEnabled()
  await expect(result.locator('[data-dry-run-unlock]')).toHaveCount(0)
})

test('with replacing locked, Replace is off with the reason and one click unlocks it for every library', async ({ page }) => {
  await mockQueue(page)
  const saved = await mockReplacementSettings(page, { dryRunMode: true, replacementQuarantineRetentionDays: 0, maxConcurrentJobs: 2 })
  await page.goto('/#/queue')

  // Nothing on the list offers a replacement that cannot happen.
  await expect(page.getByRole('button', { name: 'Replace all (2)' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Replace all (2)' })).toHaveAccessibleDescription('Replace is off because replacing is locked (dry-run).')
  await expect(page.locator('[data-replace-all-locked]')).toBeVisible()
  await expect(page.locator('tbody').getByRole('button', { name: 'Replace', exact: true })).toHaveCount(0)

  await page.locator('#queue-job-1').click()
  const result = page.locator('#queue-job-dialog [data-job-result]')
  const replace = result.getByRole('button', { name: 'Replace original' })
  await expect(replace).toBeDisabled()
  await expect(replace).toHaveAccessibleDescription('Replace is off because replacing is locked (dry-run).')
  await expect(result.locator('[data-replace-undo]')).toContainText('until you approve it in Quarantine')
  const unlock = result.locator('[data-dry-run-unlock]')
  // Dry-run holds back more than the Replace button, and the text says all of it.
  await expect(unlock).toContainText('This turns dry-run off for every library, not only this file.')
  await expect(unlock).toContainText('libraries set to replace automatically do so without asking')
  await expect(unlock).toContainText('originals and duplicate copies in Quarantine are deleted for good once they pass the time limit')
  await expect(unlock).toContainText('libraries set to clean up duplicate copies every day start doing so')
  await expect(unlock).toContainText('You can lock it again in Settings → Files & safety.')
  expect(saved).toEqual([])

  await unlock.getByRole('button', { name: 'Unlock replacing' }).click()

  await expect(replace).toBeEnabled()
  await expect(unlock).toHaveCount(0)
  // Only the lock changes; every other setting is sent back as it was read.
  expect(saved).toEqual([{ dryRunMode: false, replacementQuarantineRetentionDays: 0, maxConcurrentJobs: 2 }])
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Replace all (2)' })).toBeEnabled()
  await expect(page.locator('[data-replace-all-locked]')).toHaveCount(0)
})

test('an unlock the server refuses says so and leaves replacing locked', async ({ page }) => {
  await mockQueue(page)
  await mockReplacementSettings(page, { dryRunMode: true, replacementQuarantineRetentionDays: 14 },
    route => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'Settings could not be saved.' }) }))
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  const result = page.locator('#queue-job-dialog [data-job-result]')

  await result.getByRole('button', { name: 'Unlock replacing' }).click()

  await expect(result.getByRole('alert')).toContainText('Settings could not be saved.')
  await expect(result.getByRole('button', { name: 'Replace original' })).toBeDisabled()
  await expect(result.getByRole('button', { name: 'Unlock replacing' })).toBeEnabled()
})

test('a job that missed its picture-quality check is told what can be done, and never offered a replacement', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [failedJob('Perceptual quality (VMAF)')]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const dialog = page.locator('#queue-job-dialog')
  const result = dialog.locator('[data-job-result]')

  await expect(result).toHaveAttribute('data-job-result', 'missed')
  await expect(result.getByRole('heading', { name: 'Missed 1 check' })).toBeVisible()
  await expect(result).toContainText('The original has not been touched. The new file will not replace it.')
  await expect(result.locator('[data-advice]')).toHaveCount(3)
  await expect(result.locator('[data-advice="retry"]')).toContainText('Retry at higher quality encodes this file again one step better')
  await expect(result.locator('[data-advice="target"]')).toContainText('This changes the check itself, for every file in the library.')
  await expect(result.getByRole('link', { name: 'Open the library’s video settings' })).toHaveAttribute('href', '#/libraries/1/configure/video')
  await expect(result.locator('[data-advice="exclude"]')).toContainText('until you remove it from the library’s Excluded list')
  await expect(result).toContainText('There is no “replace anyway”. A file that missed a check never replaces an original.')
  await expect(dialog.getByRole('button', { name: 'Replace original' })).toHaveCount(0)
  // The buttons the advice names are the ones in the dialog.
  await expect(dialog.getByRole('button', { name: 'Retry at higher quality', exact: true })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Exclude', exact: true })).toBeVisible()
})

test('a job that missed another check is not told to lower the quality target', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [failedJob('Duration')]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const result = page.locator('#queue-job-dialog [data-job-result]')

  await expect(result.locator('[data-advice]')).toHaveCount(1)
  await expect(result.locator('[data-advice="exclude"]')).toBeVisible()
  await expect(result).toContainText('There is no “replace anyway”.')
})

test('each check is named in plain words and keeps the name a log would quote', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [failedJob('Perceptual quality (VMAF)')]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const dialog = page.locator('#queue-job-dialog')

  await expect(dialog.locator('[data-check-name="Decode health"]')).toHaveText('Plays from start to end without errors')
  await expect(dialog.locator('[data-check-name="Decode health"]')).toHaveAttribute('title', 'Decode health')
  await expect(dialog.locator('[data-check-name="Perceptual quality (VMAF)"]')).toHaveText('Picture quality')
})

test('when unlocking would delete or replace straight away, it shows the counts and needs a second press', async ({ page }) => {
  await mockQueue(page)
  const saved = await mockReplacementSettings(page, { dryRunMode: true, replacementQuarantineRetentionDays: 14 }, undefined, {
    retentionDays: 14, originalsToDelete: 3, originalBytes: 12_000_000_000, duplicatesToDelete: 1, duplicateBytes: 4_000_000_000,
    librariesCleaningDuplicatesDaily: 2, jobsToReplace: 5, needsConfirmation: true,
  })
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  const unlock = page.locator('#queue-job-dialog [data-dry-run-unlock]')

  await unlock.getByRole('button', { name: 'Unlock replacing' }).click()

  const confirm = unlock.locator('[data-dry-run-confirm]')
  await expect(confirm).toContainText('Unlocking will do this straight away')
  await expect(confirm.locator('[data-unlock-effect="replace"]')).toHaveText('5 verified files waiting in libraries that replace automatically will replace their originals.')
  await expect(confirm.locator('[data-unlock-effect="originals"]')).toContainText('3 originals in Quarantine are past the time limit and will be deleted for good')
  await expect(confirm.locator('[data-unlock-effect="duplicates"]')).toContainText('1 quarantined duplicate copy is past the time limit and will be deleted for good')
  await expect(confirm.locator('[data-unlock-effect="dedupe"]')).toHaveText('2 libraries will start finding and quarantining duplicate copies every day.')
  expect(saved).toEqual([])

  await confirm.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(confirm).toHaveCount(0)
  await expect(page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' })).toBeDisabled()
  expect(saved).toEqual([])

  await unlock.getByRole('button', { name: 'Unlock replacing' }).click()
  await unlock.getByRole('button', { name: 'Unlock and let this happen' }).click()
  await expect(page.locator('#queue-job-dialog').getByRole('button', { name: 'Replace original' })).toBeEnabled()
  expect(saved).toHaveLength(1)
  expect(saved[0]).toMatchObject({ dryRunMode: false })
})

test('when the server cannot say what unlocking would start, the confirmation is still asked for', async ({ page }) => {
  await mockQueue(page)
  const saved = await mockReplacementSettings(page, { dryRunMode: true, replacementQuarantineRetentionDays: 14 }, undefined, null)
  await page.goto('/#/queue')
  await page.locator('#queue-job-1').click()
  const unlock = page.locator('#queue-job-dialog [data-dry-run-unlock]')

  await unlock.getByRole('button', { name: 'Unlock replacing' }).click()

  await expect(unlock.locator('[data-unlock-effect="unknown"]')).toContainText('Optimisarr couldn’t check what unlocking would start right now.')
  expect(saved).toEqual([])
})

test('a quality search that already tried its highest setting is not told to try higher again', async ({ page }) => {
  await mockQueue(page)
  const fellBack = failedJob('Perceptual quality (VMAF)', 'Harmonic mean 92.4; needed 93.', { perceptualSearch: { result: { decision: { fellBack: true } } } })
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [fellBack]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const dialog = page.locator('#queue-job-dialog')

  await expect(dialog.locator('[data-advice="retry"]')).toHaveCount(0)
  await expect(dialog.locator('[data-advice="target"]')).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Retry at higher quality', exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Retry', exact: true })).toBeVisible()
})

test('a picture check that could not be measured is not blamed on the target', async ({ page }) => {
  await mockQueue(page)
  const unmeasured = failedJob('Perceptual quality (VMAF)', 'Quality could not be measured: ffmpeg exited with code 1')
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [unmeasured]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const dialog = page.locator('#queue-job-dialog')

  await expect(dialog.locator('[data-advice="unmeasured"]')).toContainText('There is no score to compare, so changing the target would not help.')
  await expect(dialog.locator('[data-advice="target"]')).toHaveCount(0)
  await expect(dialog.locator('[data-advice="retry"]')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Retry at higher quality', exact: true })).toHaveCount(0)
})

test('the failure headline leaves the advice to the result card and says target, not gate', async ({ page }) => {
  await mockQueue(page)
  await page.route('**/api/jobs', route => route.request().method() === 'GET' ? json(route, [failedJob('Perceptual quality (VMAF)', 'Harmonic mean 92.4; needed 93.')]) : route.fallback())
  await page.goto('/#/queue')
  await page.locator('#queue-job-9').click()
  const dialog = page.locator('#queue-job-dialog')

  await expect(dialog).toContainText('Its picture-quality score fell below this library’s target.')
  await expect(dialog).not.toContainText('lower the gate')
  await expect(dialog).not.toContainText('Retry at higher quality, or lower the target')
  await expect(dialog.locator('[data-advice="retry"]')).toBeVisible()
})
