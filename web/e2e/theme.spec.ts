import { expect, test, type Page, type Route } from '@playwright/test'

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

const QUEUE_CLEAR = {
  canStart: true,
  blockedReason: null,
  manuallyPaused: false,
  manualPauseMode: 'inactive',
  runningEncodesSuspended: false,
  suspendedEncodeCount: 0,
  hardwareAccelerated: false,
  freeDiskBytes: 3_171_405_266_944,
  workRoot: '/work',
  waitingReason: null,
}

async function mockApp(page: Page) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') {
      return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    }
    if (path === '/api/health') return json(route, { status: 'healthy' })
    if (path === '/api/settings') return json(route, { remoteWorkersAvailable: false, remoteWorkersEnabled: false })
    if (path === '/api/stats') {
      return json(route, {
        bytesSaved: 0,
        originalBytes: 0,
        optimisedBytes: 0,
        filesOptimised: 0,
        averageSavingPercent: 0,
        inQuarantine: 0,
        quarantineReclaimableBytes: 0,
        queued: 0,
        running: 0,
        readyToReplace: 0,
        failed: 0,
        libraries: 0,
        enabledLibraries: 0,
        discoveredFiles: 0,
      })
    }
    if (path === '/api/queue/status') return json(route, QUEUE_CLEAR)
    if (path === '/api/jobs') return json(route, [])
    if (path === '/api/jobs/failures') return json(route, [])
    if (path === '/api/workers') return json(route, [])
    if (path === '/api/results') return json(route, [])
    if (path === '/api/results/daily') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function ground(page: Page): Promise<string> {
  return page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--ground').trim())
}

test('the page follows the system, and follows it changing, with no reload', async ({ page }) => {
  await mockApp(page)
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.goto('/#/')

  const root = page.locator('html')
  await expect(root).toHaveClass(/\bdark\b/)
  const dark = await ground(page)

  await page.emulateMedia({ colorScheme: 'light' })
  await expect(root).not.toHaveClass(/\bdark\b/)
  expect(await ground(page)).not.toBe(dark)
})

test('a pinned scheme is kept while the system changes underneath it', async ({ page }) => {
  await mockApp(page)
  await page.addInitScript(() => localStorage.setItem('optimisarr.theme', 'light'))
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.goto('/#/')

  const root = page.locator('html')
  await expect(root).not.toHaveClass(/\bdark\b/)

  await page.emulateMedia({ colorScheme: 'light' })
  await expect(root).not.toHaveClass(/\bdark\b/)
})

test('the theme control walks the three settings and hands the page back to the system', async ({ page }) => {
  await mockApp(page)
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto('/#/')

  const root = page.locator('html')
  const control = page.getByRole('button', { name: 'Toggle theme', exact: true }).first()
  const stored = () => page.evaluate(() => localStorage.getItem('optimisarr.theme'))

  // It starts by following the system, and says so with the half circle.
  await expect(root).not.toHaveClass(/\bdark\b/)
  await expect(control.locator('path')).toHaveAttribute('d', /M12 3v18/)

  await control.click()
  await expect(root).toHaveClass(/\bdark\b/)
  await expect.poll(stored).toBe('dark')

  await control.click()
  await expect(root).not.toHaveClass(/\bdark\b/)
  await expect.poll(stored).toBe('light')

  await control.click()
  await expect.poll(stored).toBe('system')
  await expect(control.locator('path')).toHaveAttribute('d', /M12 3v18/)

  // Following the system again means the system decides once more.
  await page.emulateMedia({ colorScheme: 'dark' })
  await expect(root).toHaveClass(/\bdark\b/)
})

test('the scheme is settled before the app boots, so the page never flashes the wrong one', async ({ page }) => {
  await mockApp(page)
  // Hold the app back: the scheme has to be right while nothing has been rendered yet.
  await page.route('**/src/main.ts*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 2000))
    await route.continue()
  })
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.goto('/#/', { waitUntil: 'commit' })

  await expect(page.locator('html')).toHaveClass(/\bdark\b/)
  await expect(page.locator('#app')).toBeEmpty()
})

for (const denied of ['storage', 'media'] as const) {
  test(`theme remains usable when the browser refuses ${denied}`, async ({ page }) => {
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    await mockApp(page)
    await page.emulateMedia({ colorScheme: 'dark' })
    await page.addInitScript(denied => {
      if (denied === 'storage') {
        const get = Storage.prototype.getItem
        const set = Storage.prototype.setItem
        Storage.prototype.getItem = function(key) {
          if (key === 'optimisarr.theme') throw new DOMException('Storage refused', 'SecurityError')
          return get.call(this, key)
        }
        Storage.prototype.setItem = function(key, value) {
          if (key === 'optimisarr.theme') throw new DOMException('Storage refused', 'SecurityError')
          return set.call(this, key, value)
        }
      } else {
        const match = window.matchMedia.bind(window)
        window.matchMedia = query => {
          if (query.includes('prefers-color-scheme')) throw new Error('Media queries refused')
          return match(query)
        }
      }
    }, denied)
    await page.goto('/#/')
    const control = page.getByRole('button', { name: 'Toggle theme', exact: true }).filter({ visible: true }).first()
    await expect(control).toBeVisible()
    await expect(control).toHaveAccessibleDescription('Follow system theme')
    if (denied === 'storage') await expect(page.locator('html')).toHaveClass(/dark/)
    else await expect(page.locator('html')).not.toHaveClass(/dark/)
    await control.click()
    await expect(control).toHaveAccessibleDescription(denied === 'storage' ? 'Light theme' : 'Dark theme')
    expect(errors).toEqual([])
  })
}

test('the theme control explains its current mode through a full keyboard cycle', async ({ page }) => {
  await mockApp(page)
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto('/#/')
  const control = page.getByRole('button', { name: 'Toggle theme', exact: true }).filter({ visible: true }).first()
  await expect(control).toHaveAccessibleDescription('Follow system theme')
  await control.focus()
  for (const mode of ['Dark theme', 'Light theme', 'Follow system theme']) {
    await page.keyboard.press('Enter')
    await expect(control).toHaveAccessibleDescription(mode)
    await expect(control).toBeFocused()
  }
})
