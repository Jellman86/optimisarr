import { expect, test, type Page } from '@playwright/test'

async function mockInventory(page: Page) {
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/auth/status' ? { required: false }
      : path === '/api/setup' ? { version: 1, completed: true }
      : path === '/api/libraries' ? [{ id: 1, name: 'Films' }, { id: 2, name: 'Music' }]
      : path === '/api/inventory' ? { items: [], total: 0, counts: { all: 0, eligible: 0, skipped: 0, unprobed: 0 } }
      : path === '/api/jobs' ? [] : {}
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
}

for (const width of [375, 1440]) for (const theme of ['dark', 'light']) {
  test(`exact copy review fits ${width}px in ${theme} and remains read-only`, async ({ page }) => {
    await mockInventory(page)
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
    await page.setViewportSize({ width, height: 900 })
    let status = 'NotStarted'
    const requests: string[] = []
    await page.route('**/api/libraries/1/duplicates', route => {
      requests.push(route.request().method())
      if (route.request().method() === 'POST') status = 'Completed'
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ libraryId: 1, status,
        startedAt: '2026-10-02T10:00:00Z', finishedAt: '2026-10-02T10:00:01Z', error: null,
        progress: { checked: 2, skipped: 0, total: 2, bytesRead: 1000 },
        result: status === 'Completed' ? { checked: 2, skipped: 0, total: 2, bytesRead: 1000, truncated: false,
          groups: [{ sha256: 'a'.repeat(64), sizeBytes: 500, extraCopyBytes: null, copies: [
            { id: 7, relativePath: 'An extremely long folder/'.repeat(5) + 'Orbit.mkv', hardLinkCount: 2 },
            { id: 8, relativePath: 'Copies/Orbit.mkv', hardLinkCount: 2 }] }] } : null }) })
    })
    await page.goto('/#/inventory/duplicates')
    await expect(page.getByRole('button', { name: 'Find exact copies' })).toBeDisabled()
    await page.getByLabel('Library', { exact: true }).selectOption('1')
    await page.getByRole('button', { name: 'Find exact copies' }).click()
    await expect(page.getByText('Copies/Orbit.mkv', { exact: true })).toBeVisible()
    await expect(page.getByText('Extra disk space is unknown', { exact: true })).toBeVisible()
    await expect(page.getByText('Nothing is removed or changed.')).toBeVisible()
    await expect(page.getByRole('button', { name: /delete|remove|keep this/i })).toHaveCount(0)
    expect(await page.locator('main').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    await page.getByRole('link', { name: 'Inventory', exact: true }).last().click()
    await expect(page).toHaveURL(/#\/inventory$/)
    expect(requests).toContain('POST')
  })
}

test('scan cancellation and server failures keep actions reachable', async ({ page }) => {
  await mockInventory(page)
  let cancelled = false
  await page.route('**/api/libraries/1/duplicates', route => {
    if (route.request().method() === 'DELETE') cancelled = true
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ libraryId: 1,
      status: cancelled ? 'Cancelled' : 'Running', startedAt: null, finishedAt: null, result: null,
      progress: { checked: 1, skipped: 0, total: 5, bytesRead: 100 }, error: null }) })
  })
  await page.goto('/#/inventory/duplicates')
  await page.getByLabel('Library', { exact: true }).selectOption('1')
  await page.getByRole('button', { name: 'Cancel scan' }).click()
  await expect(page.getByText('Cancelled', { exact: true })).toBeVisible()
  await page.route('**/api/libraries/1/duplicates', route => route.fulfill({ status: 409,
    contentType: 'application/json', body: JSON.stringify({ error: 'A duplicate scan is already running.' }) }))
  await page.getByRole('button', { name: 'Find exact copies' }).click()
  await expect(page.getByText('A duplicate scan is already running.')).toBeVisible()
})

test('a delayed poll cannot restore running after cancellation', async ({ page }) => {
  await mockInventory(page)
  let calls = 0, cancelled = false
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route('**/api/libraries/1/duplicates', async route => {
    if (route.request().method() === 'DELETE') cancelled = true
    const status = cancelled ? 'Cancelled' : 'Running'
    if (route.request().method() === 'GET' && ++calls === 2) await gate
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ libraryId: 1,
      status, startedAt: null, finishedAt: null, result: null, error: null,
      progress: { checked: 0, skipped: 0, total: 4, bytesRead: 0 } }) })
  })
  await page.goto('/#/inventory/duplicates')
  await page.getByLabel('Library', { exact: true }).selectOption('1')
  await expect.poll(() => calls).toBe(2)
  await page.getByRole('button', { name: 'Cancel scan' }).click()
  await expect(page.getByText('Cancelled', { exact: true })).toBeVisible()
  const delayedResponse = page.waitForResponse(response => response.url().endsWith('/api/libraries/1/duplicates') && response.request().method() === 'GET')
  release()
  await delayedResponse
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  expect(await page.getByRole('button', { name: 'Cancel scan' }).count()).toBe(0)
  await expect(page.getByRole('button', { name: 'Find exact copies' })).toBeVisible()
})

test('scan progress follows bytes read inside a large file and never claims completion early', async ({ page }) => {
  await mockInventory(page)
  let progress: Record<string, number> = { checked: 0, skipped: 0, total: 0, bytesRead: 0 }
  let status = 'Queued'
  await page.route('**/api/libraries/1/duplicates', route => route.fulfill({ json: { libraryId: 1, status,
    startedAt: null, finishedAt: null, result: null, error: null, progress } }))
  await page.goto('/#/inventory/duplicates')
  await page.getByLabel('Library', { exact: true }).selectOption('1')
  const bar = page.getByRole('progressbar', { name: 'Reading files' })
  // Queued work has no measurable fraction yet, so it sweeps rather than showing a misleading 0%.
  await expect(bar).toBeVisible()
  await expect(bar).not.toHaveAttribute('aria-valuenow')
  await expect(page.getByText(/candidates checked/)).toHaveCount(0)

  // One 4 GB film is 40% read while zero whole files have finished: the bar must already show it.
  status = 'Running'
  progress = { checked: 0, skipped: 0, total: 2, bytesRead: 4_000_000_000, totalBytes: 10_000_000_000, processedBytes: 4_000_000_000 }
  await expect(bar).toHaveAttribute('aria-valuenow', '40')
  await expect(page.getByText('40%', { exact: true })).toBeVisible()
  await expect(page.locator('progress')).toHaveCount(0)

  progress = { ...progress, checked: 2, processedBytes: 10_000_000_000 }
  await expect(bar).toHaveAttribute('aria-valuenow', '99')

  // A server without byte totals still moves the bar by settled files.
  progress = { checked: 1, skipped: 1, total: 4, bytesRead: 10 }
  await expect(bar).toHaveAttribute('aria-valuenow', '50')

  status = 'Completed'
  await expect(page.getByText('Scan complete', { exact: true })).toBeVisible()
  await expect(bar).toHaveCount(0)
})
