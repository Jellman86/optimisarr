import { expect, test, type Page, type Route } from '@playwright/test'

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

  const rows = page.locator('tbody tr')
  await expect(rows.filter({ hasText: 'Chicago Fire' })).toContainText('encoding on Mac Studio')
  await expect(rows.filter({ hasText: 'Chicago Fire' })).toContainText('62%')
  await expect(rows.filter({ hasText: 'Slow Horses' })).toContainText('returned from MacBook Air')
  await expect(rows.filter({ hasText: 'Severance' })).toContainText('waiting for a worker')
})
