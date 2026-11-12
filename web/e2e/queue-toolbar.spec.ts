import { expect, test, type Page, type Route } from '@playwright/test'

// The filters and the batch bar share one grid cell, sized to the taller of the two, so
// checking a row swaps one for the other and never moves the table below it.

function job(id: number, status: string, verificationPassed: boolean | null = null) {
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
    verifiedAt: null,
    enqueuedAt: '2026-07-23T11:00:00Z',
    startedAt: null,
    finishedAt: null,
    clearable: true,
    workerName: null,
    remoteStage: null,
    waitingForWorker: false,
  }
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

async function mockQueue(page: Page, jobs: ReturnType<typeof job>[]) {
  const requests: string[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') return json(route, jobs)
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, runningJobs: 0,
      hardwareAccelerated: false, freeDiskBytes: 100_000_000_000, workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/clear') {
      requests.push(route.request().url())
      return json(route, { cleared: 1 })
    }
    if (path === '/api/jobs/batch/retry') {
      requests.push(route.request().url())
      return json(route, {
        requested: 2,
        succeeded: [1],
        failures: [{ id: 3, code: 'job.retry.invalidState', message: 'Only a failed job can be retried.' }],
      })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
  return requests
}

test('picking a row replaces the filters in place and leaves the table where it was', async ({ page }) => {
  await mockQueue(page, [job(1, 'Queued'), job(2, 'Queued'), job(3, 'Queued')])
  await page.goto('/#/queue')

  const toolbar = page.locator('[data-queue-toolbar]')
  const filters = page.locator('[data-queue-filters]')
  const batch = page.locator('[data-queue-batch]')
  const row = page.locator('#queue-list tbody tr').first()

  // With little showing, the toolbar is a single row, and it keeps its height in either state.
  const toolbarBefore = (await toolbar.boundingBox())!
  expect(toolbarBefore.height).toBeGreaterThanOrEqual(47)
  expect(toolbarBefore.height).toBeLessThanOrEqual(49)

  const before = (await row.boundingBox())!.y
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()

  // The batch bar took the filters' place rather than pushing the table down.
  await expect(filters).toBeHidden()
  await expect(batch).toBeVisible()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()
  const toolbarAfter = (await toolbar.boundingBox())!
  expect(Math.abs(toolbarAfter.y - toolbarBefore.y)).toBeLessThanOrEqual(1)
  expect(Math.abs(toolbarAfter.height - toolbarBefore.height)).toBeLessThanOrEqual(1)
  const after = (await row.boundingBox())!.y
  expect(Math.abs(after - before)).toBeLessThanOrEqual(1)

  // Unchecking gives the filters back, in the same place.
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).uncheck()
  await expect(filters).toBeVisible()
  await expect(batch).toBeHidden()
  expect(Math.abs((await row.boundingBox())!.y - before)).toBeLessThanOrEqual(1)
})

test('the queue no longer hides its management behind a toggle', async ({ page }) => {
  const requests = await mockQueue(page, [job(1, 'Failed'), job(2, 'Queued')])
  await page.goto('/#/queue')

  await expect(page.getByRole('button', { name: 'Manage queue' })).toHaveCount(0)
  const clearErrors = page.getByRole('button', { name: 'Clear errored (1)' })
  await expect(clearErrors).toBeVisible()

  // The action sits at the right end of the filter row, not in a row of its own.
  const filters = page.locator('[data-queue-filters]')
  const row = filters.locator('.queue-filters')
  const actions = filters.locator('.queue-toolbar-actions')
  expect((await actions.boundingBox())!.x).toBeGreaterThanOrEqual((await row.boundingBox())!.x)

  await clearErrors.click()
  await expect.poll(() => requests.some(url => url.includes('/api/jobs/clear?scope=errored'))).toBe(true)
})

test('the clear-errors action is absent when nothing failed or was cancelled', async ({ page }) => {
  await mockQueue(page, [job(1, 'Queued'), job(2, 'Completed')])
  await page.goto('/#/queue')

  await expect(page.getByRole('button', { name: /^Clear errored/ })).toHaveCount(0)
})

test('the clear-errors count is what the clear removes: failed and cancelled jobs it can clear', async ({ page }) => {
  // The server clears cancelled jobs too, but never one still holding a rollback.
  const held = { ...job(4, 'Failed'), clearable: false }
  await mockQueue(page, [job(1, 'Failed'), job(2, 'Cancelled'), job(3, 'Cancelled'), held])
  await page.goto('/#/queue')

  await expect(page.getByRole('button', { name: 'Clear errored (3)' })).toBeVisible()
})

test('a queue with only cancelled jobs can still clear them', async ({ page }) => {
  await mockQueue(page, [job(1, 'Cancelled'), job(2, 'Queued')])
  await page.goto('/#/queue')

  await expect(page.getByRole('button', { name: 'Clear errored (1)' })).toBeVisible()
})

test('the selection is dropped when the tab changes, and a batch keeps only the rows it refused', async ({ page }) => {
  await mockQueue(page, [job(1, 'Failed'), job(2, 'Queued'), job(3, 'Failed')])
  await page.goto('/#/queue')

  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()

  // The Failures tab shows other rows, so the picks do not follow the operator there.
  const tabs = page.locator('.queue-tabs')
  await tabs.getByRole('button', { name: /^Failures/ }).click()
  await tabs.getByRole('button', { name: /^Queue/ }).click()
  await expect(page.locator('[data-queue-batch]')).toBeHidden()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toHaveCount(0)

  // The server handles job 1 and refuses job 3, so only job 3 stays picked to act on again.
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select Film 3.mkv' }).check()
  await page.locator('[data-queue-batch]').getByRole('button', { name: 'Retry' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Retry' }).click()
  await expect(page.getByText('1 succeeded')).toBeVisible()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Select Film 3.mkv' })).toBeChecked()
  await expect(page.getByRole('checkbox', { name: 'Select Film 1.mkv' })).not.toBeChecked()
})

for (const scheme of ['light', 'dark'] as const) {
  test(`the batch bar is readable and reachable in the ${scheme} scheme`, async ({ page }) => {
    // The bar is a dark slab with its own inks, so it carries its own contrast rules.
    const { default: AxeBuilder } = await import('@axe-core/playwright')
    await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), scheme)
    await mockQueue(page, [job(1, 'ReadyToReplace', true), job(2, 'Failed'), job(3, 'Queued')])
    await page.goto('/#/queue')

    await page.getByRole('checkbox', { name: 'Select Film 2.mkv' }).check()
    const bar = page.locator('[data-queue-batch]')
    await expect(bar).toBeVisible()
    await expect(bar.getByRole('button', { name: 'Retry' })).toBeVisible()
    await expect(bar.getByRole('button', { name: 'Remove' })).toBeVisible()
    await expect(bar.getByRole('button', { name: 'Clear selection' })).toBeVisible()

    const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
    expect(scan.violations, JSON.stringify(scan.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([])
  })
}
