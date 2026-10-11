import { expect, test, type Page } from '@playwright/test'

async function mockInventory(page: Page) {
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/auth/status' ? { required: false }
      : path === '/api/setup' ? { version: 1, completed: true }
      : path === '/api/libraries' ? [{ id: 1, name: 'Films' }]
      : path === '/api/jobs' ? [] : {}
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
}

for (const width of [375, 2048]) for (const theme of ['dark', 'light']) {
  test(`saved exact results support refresh and paging at ${width}px in ${theme}`, async ({ page }) => {
    await mockInventory(page)
    await page.setViewportSize({ width, height: 900 })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
    const posts: string[] = []
    await page.route('**/api/libraries/1/duplicates*', route => {
      const url = new URL(route.request().url())
      if (route.request().method() === 'POST') posts.push(url.search)
      const index = Number(url.searchParams.get('page') ?? 0)
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify({
        libraryId: 1, status: 'Completed', startedAt: '2026-10-11T10:00:00Z', finishedAt: '2026-10-11T10:00:01Z',
        isStale: true, error: null, progress: { checked: 4, skipped: 0, total: 4, bytesRead: 0, reused: 4 },
        result: { checked: 4, skipped: 0, total: 4, bytesRead: 0, reused: 4, page: index, totalGroups: 21,
          hasMore: index === 0, truncated: false, groups: [{ sha256: (index ? 'b' : 'a').repeat(64), sizeBytes: 500,
            extraCopyBytes: null, totalCopies: 2, copies: [
              { id: index ? 9 : 7, relativePath: index ? 'Second-page.wav' : 'First-page.wav', hardLinkCount: 1,
                checkedAt: '2026-10-01T10:00:00Z', reused: true },
              { id: index ? 10 : 8, relativePath: 'Copy.wav', hardLinkCount: 1,
                checkedAt: '2026-10-01T10:00:00Z', reused: true }] }] }
      }) })
    })
    await page.goto('/#/inventory/duplicates')
    await page.getByLabel('Library', { exact: true }).selectOption('1')
    await expect(page.getByText('First-page.wav', { exact: true })).toBeVisible()
    await expect(page.getByText('4 cached hashes reused', { exact: true })).toBeVisible()
    await expect(page.getByText(/Inventory has changed since this report/)).toBeVisible()
    await expect(page.getByText(/Last content read:/).first()).toBeVisible()
    await page.getByRole('button', { name: 'Next groups', exact: true }).click()
    await expect(page.getByText('Second-page.wav', { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Next groups', exact: true })).toBeDisabled()
    await page.getByRole('button', { name: 'Previous groups', exact: true }).click()
    await expect(page.getByText('First-page.wav', { exact: true })).toBeVisible()
    await page.getByLabel('Read every candidate again', { exact: true }).check()
    await page.getByRole('button', { name: 'Find exact copies', exact: true }).click()
    await expect.poll(() => posts).toEqual(['?forceRefresh=true'])
    expect(await page.locator('main').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    await page.reload()
    await page.getByLabel('Library', { exact: true }).selectOption('1')
    await expect(page.getByText('First-page.wav', { exact: true })).toBeVisible()
  })
}

test('interrupted work explains recovery and remains restartable', async ({ page }) => {
  await mockInventory(page)
  await page.route('**/api/libraries/1/duplicates', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({
    libraryId: 1, status: 'Interrupted', startedAt: null, finishedAt: null, result: null,
    progress: { checked: 1, skipped: 0, total: 3, bytesRead: 10, reused: 0 },
    error: 'The server stopped before this scan finished. Scan again to reuse completed file reads.'
  }) }))
  await page.goto('/#/inventory/duplicates')
  await page.getByLabel('Library', { exact: true }).selectOption('1')
  await expect(page.getByText('Scan interrupted', { exact: true })).toBeVisible()
  await expect(page.getByText(/reuse completed file reads/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Find exact copies' })).toBeEnabled()
})
