import { expect, test, type Page, type Route } from '@playwright/test'

const replacement = {
  id: 1, jobId: 10, mediaFileId: 7,
  originalPath: '/media/Slow Horses/Season 6/Slow Horses - S06E01.mkv',
  finalPath: '/media/Slow Horses/Season 6/Slow Horses - S06E01.mp4',
  quarantinePath: '/trash/job-10/Slow Horses - S06E01.mkv',
  originalSizeBytes: 3_600_000_000, newSizeBytes: 269_000_000,
  crossFilesystem: false, status: 'Replaced', replacedAt: '2026-09-16T03:19:47Z', rolledBackAt: null, purgedAt: null,
}
const detail = { ...replacement, mediaKind: 'Video', verificationPassed: true, verificationReportJson: JSON.stringify({ checks: [{ name: 'Duration', outcome: 'Passed', detail: 'Duration retained.' }] }) }
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
async function mockQuarantine(page: Page, items = [replacement]) {
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { completed: true })
    if (path === '/api/replacements') return json(route, items)
    if (/^\/api\/replacements\/\d+$/.test(path)) {
      const item = items.find(item => item.id === Number(path.split('/').pop()))
      return item ? json(route, { ...detail, ...item }) : json(route, { error: 'Not found' }, 404)
    }
    return route.fulfill({ status: 404 })
  })
}

test('quarantine opens a full-width review page and returns to the same list position', async ({ page }) => {
  await mockQuarantine(page, Array.from({ length: 40 }, (_, index) => ({ ...replacement, id: index + 1, finalPath: `/media/Film ${index + 1}.mp4` })))
  await page.goto('/#/quarantine')
  const item = page.getByRole('link', { name: 'Film 35.mp4', exact: true })
  await item.scrollIntoViewIfNeeded()
  const scrollTop = await page.locator('[data-quarantine-list]').evaluate(element => element.scrollTop)
  await item.focus()
  await item.press('Enter')
  await expect(page).toHaveURL(/#\/quarantine\/35$/)
  await expect(page.getByRole('heading', { name: 'Film 35.mp4', level: 1 })).toBeFocused()
  await expect(page.locator('[data-quarantine-review]')).toBeVisible()
  await expect(page.locator('video')).toHaveCount(2)
  await expect(page.locator('[role="dialog"], .bottom-sheet')).toHaveCount(0)
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Quarantine' }).click()
  await expect(item).toBeFocused()
  expect(await page.locator('[data-quarantine-list]').evaluate(element => element.scrollTop)).toBeCloseTo(scrollTop, 0)
  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Film 35.mp4', level: 1 })).toBeVisible()
})

test('a direct review link loads comparison and verification after refresh', async ({ page }) => {
  await mockQuarantine(page)
  await page.goto('/#/quarantine/1')
  await expect(page.getByRole('heading', { name: 'Slow Horses - S06E01.mp4', level: 1 })).toBeVisible()
  await expect(page.getByText('Duration retained.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Download' })).toHaveCount(2)
  await page.reload()
  await expect(page.locator('video')).toHaveCount(2)
})

for (const action of ['approve', 'rollback']) {
  test(`${action} retains its confirmation and returns to the list on success`, async ({ page }) => {
    const items = [{ ...replacement }]
    await mockQuarantine(page, items)
    let requests = 0
    await page.route(`**/api/replacements/1/${action}`, route => {
      requests++
      items[0].status = action === 'approve' ? 'Purged' : 'RolledBack'
      return json(route, items[0])
    })
    await page.goto('/#/quarantine/1')
    const button = page.getByRole('button', { name: action === 'approve' ? 'Approve & free space' : 'Reject (roll back)', exact: true })
    page.once('dialog', dialog => dialog.dismiss())
    await button.click()
    expect(requests).toBe(0)
    page.once('dialog', async dialog => {
      expect(dialog.message()).toContain(action === 'approve' ? 'permanently deletes' : 'restore the original')
      await dialog.accept()
    })
    await button.click()
    await expect(page).toHaveURL(/#\/quarantine$/)
    expect(requests).toBe(1)
    await page.getByRole('link', { name: 'Slow Horses - S06E01.mp4', exact: true }).click()
    await expect(page.getByText('This entry is finished.', { exact: false })).toBeVisible()
    await expect(page.locator('video')).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Approve & free space' })).toHaveCount(0)
  })
}

test('failed decisions remain visible beside the controls and cannot be double submitted', async ({ page }) => {
  await mockQuarantine(page)
  let release: () => void = () => {}
  const held = new Promise<void>(resolve => { release = resolve })
  let requests = 0
  await page.route('**/api/replacements/1/approve', async route => { requests++; await held; return json(route, { error: 'Original is locked' }, 409) })
  await page.goto('/#/quarantine/1')
  page.on('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Approve & free space' }).click()
  await expect(page.getByRole('button', { name: 'Working', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Reject (roll back)', exact: true })).toBeDisabled()
  release()
  const decisions = page.getByRole('region', { name: 'Review decision' })
  await expect(decisions).toContainText('Original is locked')
  expect(requests).toBe(1)
  await expect(page).toHaveURL(/#\/quarantine\/1$/)
})

test('failed detail loading retries and stale responses cannot replace another review', async ({ page }) => {
  await mockQuarantine(page, [replacement, { ...replacement, id: 2, finalPath: '/media/Second.mp4' }])
  let held: Route | undefined
  await page.route('**/api/replacements/1', route => { held = route })
  await page.goto('/#/quarantine/1')
  await expect(page.getByRole('status')).toHaveText('Loading comparison…')
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Quarantine' }).click()
  await page.getByRole('link', { name: 'Second.mp4', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Second.mp4', level: 1 })).toBeVisible()
  await json(held!, { error: 'Old request failed' }, 500)
  await expect(page.getByText('Old request failed')).toHaveCount(0)
  await page.goto('/#/quarantine/99')
  await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible()
  await page.route('**/api/replacements/99', route => json(route, { ...detail, id: 99 }))
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(page.locator('video')).toHaveCount(2)
})

for (const colorScheme of ['dark', 'light'] as const) {
  test(`review fits desktop and narrow layouts in ${colorScheme} mode`, async ({ page }) => {
    await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' })
    await mockQuarantine(page)
    await page.goto('/#/quarantine/1')
    await expect(page.locator('video')).toHaveCount(2)
    for (const viewport of [{ width: 1600, height: 1000 }, { width: 375, height: 812 }, { width: 812, height: 375 }]) {
      await page.setViewportSize(viewport)
      const review = (await page.locator('[data-quarantine-review]').boundingBox())!
      if (viewport.width === 1600) expect(review.width).toBe(1152)
      expect(review.x).toBeGreaterThanOrEqual(0)
      expect(review.x + review.width).toBeLessThanOrEqual(viewport.width)
      expect(await page.locator('main').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
      await page.getByRole('button', { name: 'Approve & free space' }).scrollIntoViewIfNeeded()
      await expect(page.getByRole('button', { name: 'Approve & free space' })).toBeInViewport()
    }
  })
}

test('partial bulk failure stays visible after the list refresh and only affects active entries', async ({ page }) => {
  await mockQuarantine(page, [replacement, { ...replacement, id: 2, status: 'Purged' }])
  const ids: string[] = []
  await page.route('**/api/replacements/*/approve', route => { ids.push(route.request().url()); return json(route, { error: 'Locked' }, 409) })
  await page.goto('/#/quarantine')
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Approve all (1)', exact: true }).click()
  await expect(page.getByText('1 replacement could not be approved. Review the remaining rows.')).toBeVisible()
  expect(ids).toHaveLength(1)
  expect(ids[0]).toContain('/1/approve')
})

for (const mediaKind of ['Audio', 'Image']) {
  test(`${mediaKind} comparisons use the correct viewer`, async ({ page }) => {
    await mockQuarantine(page)
    await page.route('**/api/replacements/1', route => json(route, { ...detail, mediaKind }))
    await page.goto('/#/quarantine/1')
    await expect(page.getByRole('region', { name: 'Compare files' }).locator(mediaKind === 'Audio' ? 'audio' : 'img')).toHaveCount(2)
    await expect(page.locator('video')).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Approve & free space' })).toBeVisible()
  })
}

test('long names and verification evidence reflow with enlarged translated text', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.addInitScript(() => localStorage.setItem('optimisarr:locale', 'de'))
  await mockQuarantine(page)
  await page.route('**/api/replacements/1', route => json(route, { ...detail, finalPath: '/media/' + 'VeryLongUnbrokenFilename'.repeat(8) + '.mp4', verificationReportJson: JSON.stringify({ checks: [{ name: 'ExtendedCheck'.repeat(5), outcome: 'Passed', detail: 'LongEvidence'.repeat(30) }] }) }))
  await page.goto('/#/quarantine/1')
  await expect(page.locator('video')).toHaveCount(2)
  await page.locator('html').evaluate(element => { element.style.fontSize = '125%' })
  expect(await page.locator('main').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  for (const element of await page.locator('[data-quarantine-review] video, [data-quarantine-review] button, [data-quarantine-review] h1').all()) {
    const bounds = (await element.boundingBox())!
    expect(bounds.x).toBeGreaterThanOrEqual(0)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(375)
  }
})
