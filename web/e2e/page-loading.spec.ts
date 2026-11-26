import { expect, test, type Page, type Route } from '@playwright/test'

// Each page is fetched the first time it is visited. These prove the shell holds the page's place
// while it arrives, hands focus to its heading once it has, and says so when it cannot arrive.

function json(route: Route, body: unknown) {
  return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockApp(page: Page) {
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/queue/status') return json(route, { canStart: true, manuallyPaused: false, blockedReason: null, waitingReason: null, runningJobs: 0, maxConcurrentJobs: 1, freeDiskBytes: 500 * 1024 ** 3, workloadLanes: [] })
    if (path === '/api/libraries' || path === '/api/jobs' || path === '/api/results' || path === '/api/results/daily') return json(route, [])
    if (path === '/api/settings') return json(route, { libraryScanIntervalHours: 6 })
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

// The dev server serves each page as its own module; a production build serves it as its own chunk.
const schedulePage = '**/src/lib/pages/Schedule.svelte*'

test('a page visited for the first time takes focus on its heading once it has arrived', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/')
  await expect(page.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeVisible()

  let release!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  await page.route(schedulePage, async route => { await held; await route.continue() })
  await page.getByRole('navigation').getByRole('button', { name: 'Schedule', exact: true }).click()

  // While it is on its way the page's room is held and, once the wait is noticeable, labelled.
  const pending = page.getByRole('status').filter({ hasText: 'Loading…' })
  await expect(pending).toBeVisible()
  expect((await pending.boundingBox())?.height).toBeGreaterThan(300)
  release()

  const heading = page.getByRole('heading', { name: 'Schedule', level: 1 })
  await expect(heading).toBeFocused()
  await expect(pending).toHaveCount(0)
  await expect(page).toHaveTitle('Schedule · Optimisarr')
})

test('a page that cannot be fetched says so and offers a reload, and the rest of the app still works', async ({ page }) => {
  await mockApp(page)
  await page.route(schedulePage, route => route.abort())
  await page.goto('/#/schedule')

  const failure = page.getByRole('alert').filter({ hasText: 'This page couldn\'t load.' })
  await expect(failure).toBeVisible()
  await expect(failure.getByRole('button', { name: 'Reload', exact: true })).toBeVisible()
  await expect(page).toHaveTitle('Schedule · Optimisarr')

  await page.getByRole('navigation').getByRole('button', { name: 'Dashboard', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeVisible()
  await expect(failure).toHaveCount(0)
})
