import { expect, test, type Page } from '@playwright/test'

const report = {
  libraryId: 1, status: 'Completed', finishedAt: '2026-11-07T10:00:00Z', isStale: false,
  progress: { checked: 2, skipped: 0, total: 2, bytesRead: 2048, reused: 0 },
  libraries: [{ id: 1, name: 'Films', root: '/media/films', mediaType: 'Film' }],
  result: { page: 0, hasMore: false, totalGroups: 1, truncated: false, groups: [{
    sha256: 'a'.repeat(64), sizeBytes: 1024, extraCopyBytes: 1024, totalCopies: 2, reviewKey: 'b'.repeat(64), canDecide: true, decision: null,
    copies: [
      { id: 1, libraryId: 1, libraryName: 'Films', libraryType: 'Film', relativePath: 'A/Title.mkv', sizeBytes: 1024, sha256: 'a'.repeat(64), hardLinkCount: 1, checkedAt: '2026-11-07T10:00:00Z' },
      { id: 2, libraryId: 1, libraryName: 'Films', libraryType: 'Film', relativePath: 'B/Title.mkv', sizeBytes: 1024, sha256: 'a'.repeat(64), hardLinkCount: 1, checkedAt: '2026-11-07T10:00:00Z' },
    ],
  }] },
}
const idle = { libraryId: 1, running: false, total: 0, done: 0, moved: 0, bytesHeld: 0, failures: [] }
const offSchedule = { libraryId: 1, enabled: false, supported: true, dryRun: false, lastRunAt: null, lastOutcome: null, lastMoved: 0, lastBytes: 0, nextRunAfter: null }
const preview = { ready: true, groups: 1, copies: 1, bytesToFree: 1024, leftAlone: { SharedExtra: 2, ManagerUnavailable: 1 } }

async function mock(page: Page, options: { preview?: unknown; status?: unknown; report?: unknown; schedule?: Record<string, unknown>; nextSchedule?: () => Record<string, unknown> | null } = {}) {
  let status = options.status ?? idle
  let schedule = options.schedule ?? offSchedule
  const calls: string[] = []
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url()), path = url.pathname, method = route.request().method()
    if (path === '/api/auth/status') return route.fulfill({ json: { required: false } })
    if (path === '/api/setup') return route.fulfill({ json: { version: 1, completed: true } })
    if (path === '/api/libraries') return route.fulfill({ json: [{ id: 1, name: 'Films' }] })
    if (path === '/api/libraries/1/duplicates') return route.fulfill({ json: options.report ?? report })
    if (path === '/api/libraries/1/duplicates/schedule') {
      if (method === 'PUT') {
        const enabled = route.request().postDataJSON().enabled
        calls.push(`schedule:${enabled}`)
        schedule = { ...schedule, enabled, nextRunAfter: enabled ? '2026-11-08T03:00:00Z' : null }
      }
      return route.fulfill({ json: options.nextSchedule?.() ?? schedule })
    }
    if (path === '/api/libraries/1/duplicates/cleanup/preview') { calls.push('preview'); return route.fulfill({ json: options.preview ?? preview }) }
    if (path === '/api/libraries/1/duplicates/cleanup') {
      if (method === 'POST') {
        calls.push('start')
        status = { libraryId: 1, running: false, total: 1, done: 1, moved: 1, bytesHeld: 1024, failures: [] }
        return route.fulfill({ status: 202, json: { ...(status as object), running: true, done: 0, moved: 0 } })
      }
      if (method === 'DELETE') { calls.push('stop'); return route.fulfill({ status: 202 }) }
      return route.fulfill({ json: status })
    }
    return route.fulfill({ json: path === '/api/jobs' ? [] : {} })
  })
  return calls
}

test('the page says what can move and why others stay, and asks the managers once', async ({ page }) => {
  const calls = await mock(page)
  await page.goto('/#/libraries/1/duplicates')

  const card = page.getByTestId('duplicate-cleanup')
  await expect(card).toContainText('Spare copies that can move: 1. Groups: 1.')
  await expect(card).toContainText('Extras such as trailers can belong to several titles')
  await expect(card).toContainText('(2)')
  await expect(card).toContainText('Sonarr or Radarr could not be asked')
  expect(calls.filter(call => call === 'preview')).toHaveLength(1)
})

test('starting a cleanup reports what moved and links to Quarantine', async ({ page }) => {
  const calls = await mock(page)
  await page.goto('/#/libraries/1/duplicates')

  await page.getByRole('button', { name: 'Quarantine spare copies' }).click()

  const card = page.getByTestId('duplicate-cleanup')
  await expect(card).toContainText('Copies moved to Quarantine: 1')
  await expect(card.getByRole('link', { name: 'Quarantine' })).toHaveAttribute('href', '#/quarantine')
  expect(calls).toContain('start')
})

test('a running cleanup shows progress and can be stopped', async ({ page }) => {
  const calls = await mock(page, { status: { libraryId: 1, running: true, total: 4, done: 1, moved: 1, bytesHeld: 1024, failures: [] } })
  await page.goto('/#/libraries/1/duplicates')

  const card = page.getByTestId('duplicate-cleanup')
  await expect(card).toContainText('Checked 1 of 4 groups. Copies moved: 1.')
  await expect(card.getByRole('button', { name: 'Quarantine spare copies' })).toHaveCount(0)
  await card.getByRole('button').click()
  await expect.poll(() => calls).toContain('stop')
})

test('groups it could not clean are listed with their reason', async ({ page }) => {
  await mock(page, { status: { libraryId: 1, running: false, total: 2, done: 2, moved: 1, bytesHeld: 1024, failures: [{ path: '/media/films/B/Title.mkv', reason: 'Sonarr or Radarr now tracks this copy. Scan again.' }] } })
  await page.goto('/#/libraries/1/duplicates')

  await expect(page.getByTestId('duplicate-cleanup')).toContainText('/media/films/B/Title.mkv: Sonarr or Radarr now tracks this copy. Scan again.')
})

test('with nothing that can move there is no button, and the reasons still show', async ({ page }) => {
  await mock(page, { preview: { ready: true, groups: 0, copies: 0, bytesToFree: 0, leftAlone: { NamesDiffer: 1 } } })
  await page.goto('/#/libraries/1/duplicates')

  const card = page.getByTestId('duplicate-cleanup')
  await expect(card).toContainText('No spare copy here can move without your choice.')
  await expect(card).toContainText('The copies have different names')
  await expect(card.getByRole('button', { name: 'Quarantine spare copies' })).toHaveCount(0)
})

test('a stale report offers no cleanup and does not ask the managers', async ({ page }) => {
  const calls = await mock(page, { report: { ...report, isStale: true } })
  await page.goto('/#/libraries/1/duplicates')

  await expect(page.getByRole('heading', { name: 'Exact copies' }).or(page.locator('h1')).first()).toBeVisible()
  await expect(page.getByTestId('duplicate-cleanup')).toHaveCount(0)
  expect(calls).not.toContain('preview')
})

test('a slow start for one library never shows on another', async ({ page }) => {
  await mock(page)
  let release = () => {}
  await page.route('**/api/libraries', route => route.fulfill({ json: [{ id: 1, name: 'Films' }, { id: 2, name: 'Shows' }] }))
  await page.route('**/api/libraries/2/duplicates', route => route.fulfill({ json: { ...report, libraryId: 2, libraries: [{ id: 2, name: 'Shows', root: '/media/shows', mediaType: 'Tv' }] } }))
  await page.route('**/api/libraries/2/duplicates/cleanup/preview', route => route.fulfill({ json: preview }))
  await page.route('**/api/libraries/2/duplicates/cleanup', route => route.fulfill({ json: { ...idle, libraryId: 2 } }))
  await page.route('**/api/libraries/1/duplicates/cleanup', async route => {
    if (route.request().method() !== 'POST') return route.fulfill({ json: idle })
    await new Promise<void>(resolve => { release = resolve })
    return route.fulfill({ status: 202, json: { libraryId: 1, running: true, total: 9, done: 3, moved: 3, bytesHeld: 1, failures: [] } })
  })
  await page.goto('/#/inventory/duplicates')
  await page.getByLabel('Library', { exact: true }).selectOption('1')
  const button = page.getByRole('button', { name: 'Quarantine spare copies' })
  await button.click()
  await expect(button).toBeDisabled()

  await page.getByLabel('Library', { exact: true }).selectOption('2')
  await expect(page.getByRole('button', { name: 'Quarantine spare copies' })).toBeEnabled()
  release()

  await page.waitForTimeout(300)
  await expect(page.getByTestId('duplicate-cleanup')).not.toContainText('Checked 3 of 9')
})

test('a finished run asks again what can move', async ({ page }) => {
  const calls = await mock(page)
  await page.goto('/#/libraries/1/duplicates')
  await page.getByRole('button', { name: 'Quarantine spare copies' }).click()
  await expect(page.getByTestId('duplicate-cleanup')).toContainText('Copies moved to Quarantine: 1')
  await expect.poll(() => calls.filter(call => call === 'preview').length).toBeGreaterThanOrEqual(2)
})

test('automatic cleanup starts off, explains itself, and can be turned on', async ({ page }) => {
  const calls = await mock(page)
  await page.goto('/#/libraries/1/duplicates')

  const card = page.getByTestId('duplicate-schedule')
  await expect(card).toContainText('Once a day, Optimisarr scans this library for exact copies')
  const toggle = card.getByRole('checkbox', { name: 'Find and quarantine spare copies every day' })
  await expect(toggle).not.toBeChecked()
  await toggle.check()

  await expect(card).toContainText('It has not run yet. The first run starts within 15 minutes.')
  expect(calls).toContain('schedule:true')
})

test('automatic cleanup says what its last run did and when the next one is', async ({ page }) => {
  await mock(page, { schedule: { ...offSchedule, enabled: true, lastRunAt: '2026-11-07T03:00:00Z', lastOutcome: 'Cleaned', lastMoved: 2, lastBytes: 2048, nextRunAfter: '2026-11-08T03:00:00Z' } })
  await page.goto('/#/libraries/1/duplicates')

  const card = page.getByTestId('duplicate-schedule')
  await expect(card).toContainText('moved 2 copies holding 2.0 KB to Quarantine')
  await expect(card).toContainText('Next run after')
})

test('automatic cleanup warns when dry-run mode stops it, and is not offered outside film and TV', async ({ page }) => {
  await mock(page, { schedule: { ...offSchedule, enabled: true, dryRun: true, lastRunAt: '2026-11-07T03:00:00Z', lastOutcome: 'DryRun' } })
  await page.goto('/#/libraries/1/duplicates')
  await expect(page.getByTestId('duplicate-schedule')).toContainText('Dry-run mode is on, so the daily cleanup moves nothing.')

  await page.unrouteAll()
  await mock(page, { schedule: { ...offSchedule, supported: false } })
  await page.reload()
  const card = page.getByTestId('duplicate-schedule')
  await expect(card).toContainText('Only film and TV libraries can be cleaned automatically.')
  await expect(card.getByRole('checkbox')).toHaveCount(0)
})

test('the automatic cleanup card shows a run that finishes while the page is open', async ({ page }) => {
  await page.clock.install()
  let finished = false
  await mock(page, {
    schedule: { ...offSchedule, enabled: true },
    nextSchedule: () => finished ? { ...offSchedule, enabled: true, lastRunAt: '2026-11-08T03:00:00Z', lastOutcome: 'NothingToClean', nextRunAfter: '2026-11-09T03:00:00Z' } : null,
  })
  await page.goto('/#/libraries/1/duplicates')
  const card = page.getByTestId('duplicate-schedule')
  await expect(card).toContainText('It has not run yet.')

  finished = true
  await page.clock.runFor(61_000)

  await expect(card).toContainText('nothing could move')
})
