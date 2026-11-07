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
const preview = { ready: true, groups: 1, copies: 1, bytesToFree: 1024, leftAlone: { SharedExtra: 2, ManagerUnavailable: 1 } }

async function mock(page: Page, options: { preview?: unknown; status?: unknown; report?: unknown } = {}) {
  let status = options.status ?? idle
  const calls: string[] = []
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url()), path = url.pathname, method = route.request().method()
    if (path === '/api/auth/status') return route.fulfill({ json: { required: false } })
    if (path === '/api/setup') return route.fulfill({ json: { version: 1, completed: true } })
    if (path === '/api/libraries') return route.fulfill({ json: [{ id: 1, name: 'Films' }] })
    if (path === '/api/libraries/1/duplicates') return route.fulfill({ json: options.report ?? report })
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
  await expect(card).toContainText('Spare copies that can move: 1, in 1 groups')
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
  await mock(page, { status: { libraryId: 1, running: false, total: 2, done: 2, moved: 1, bytesHeld: 1024, failures: [{ path: '/media/films/B/Title.mkv', reason: 'TrackedByManager' }] } })
  await page.goto('/#/libraries/1/duplicates')

  await expect(page.getByTestId('duplicate-cleanup')).toContainText('/media/films/B/Title.mkv: TrackedByManager')
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
