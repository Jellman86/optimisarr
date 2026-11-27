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
    if (path === '/api/quarantine/duplicates') return json(route, [])
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

test('a quarantined file that grew says so, in bytes and in percent alike', async ({ page }) => {
  // The bytes and the percent share one direction: growth reads positive in both, a saving
  // negative in both, instead of a bare "191 MB" beside a "+20%" that contradicts it.
  const grew = { ...replacement, id: 2, originalPath: '/media/Grown Film.mkv', finalPath: '/media/Grown Film.mp4', originalSizeBytes: 1_000_000_000, newSizeBytes: 1_200_000_000 }
  const shrank = { ...replacement, id: 3, originalSizeBytes: 3_600_000_000, newSizeBytes: 269_000_000 }
  await mockQuarantine(page, [grew, shrank])

  await page.goto('/#/quarantine/2')
  const review = page.locator('[data-quarantine-review]')
  await expect(review.getByText('+191 MB')).toBeVisible()
  await expect(review.getByText('+20%')).toBeVisible()
  await expect(review.getByText('−191 MB')).toHaveCount(0)

  await page.goto('/#/quarantine/3')
  await expect(review.getByText('−3.1 GB')).toBeVisible()
  await expect(review.getByText('−93%')).toBeVisible()
})

test('the quarantine list and the review read the same size change', async ({ page }) => {
  const shrank = { ...replacement, id: 3, originalSizeBytes: 3_600_000_000, newSizeBytes: 269_000_000 }
  await mockQuarantine(page, [shrank])

  await page.goto('/#/quarantine')
  await expect(page.getByRole('cell', { name: /−93%/ })).toContainText('3.4 GB → 257 MB')

  await page.goto('/#/quarantine/3')
  await expect(page.locator('[data-quarantine-review]').getByText('−93%')).toBeVisible()
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
      // The comparison is a media page: on a wide screen it uses the width instead of a form's measure.
      if (viewport.width === 1600) expect(review.width).toBeGreaterThan(1152)
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

test('the decision is in view on arrival, before the comparison is scrolled, and the players show the title', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 720 })
  await mockQuarantine(page)
  await page.goto('/#/quarantine/1')
  const decision = page.getByRole('region', { name: 'Review decision' })
  await expect(page.getByRole('heading', { name: 'Compare files', level: 2 })).toBeVisible()
  await expect(decision).toBeInViewport({ ratio: 1 })
  await expect(decision.getByRole('button', { name: 'Approve & free space' })).toBeInViewport({ ratio: 1 })
  await expect(decision).toContainText('Approve deletes the quarantined original now; rolling back is then no longer possible.')
  // The backdrop belongs to this replacement's job; a missing one leaves the player plain.
  await expect(page.locator('video').first()).toHaveAttribute('poster', '/api/jobs/10/artwork')
})

const quarantinedSpare = {
  id: '11111111-1111-1111-1111-111111111111',
  state: 'Quarantined',
  heldSince: '2026-11-07T10:00:00Z',
  attentionReason: null,
  keptPaths: ['/data/Film/Title/Title.mkv'],
  copies: [{ path: '/data/Film/Backup/Title.mkv', quarantinePath: '/data/Film/.optimisarr-dedupe/1/Backup/Title.mkv', sizeBytes: 1_000_000 }],
}

test('duplicates and optimisation originals are labelled apart and can be filtered', async ({ page }) => {
  await mockQuarantine(page, [])
  await page.route('**/api/replacements', route => route.fulfill({ json: [{
    id: 1, jobId: 1, mediaFileId: 1, originalPath: '/data/Film/A.mkv', quarantinePath: '/trash/A.mkv', finalPath: '/data/Film/A.mp4',
    originalSizeBytes: 2000, newSizeBytes: 1000, crossFilesystem: false, status: 'Replaced', replacedAt: '2026-11-07T09:00:00Z', rolledBackAt: null, purgedAt: null,
  }] }))
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: [quarantinedSpare] }))
  await page.goto('/#/quarantine')

  const duplicates = page.getByTestId('quarantined-duplicates')
  await expect(duplicates).toContainText('Duplicate')
  await expect(duplicates).toContainText('Kept: /data/Film/Title/Title.mkv')
  await expect(page.locator('.quarantine-table')).toContainText('Optimisation')

  await page.getByRole('button', { name: 'Duplicate (1)' }).click()
  await expect(page.locator('.quarantine-table')).toHaveCount(0)
  await expect(duplicates).toBeVisible()

  await page.getByRole('button', { name: 'Optimisation (1)' }).click()
  await expect(duplicates).toHaveCount(0)
  await expect(page.locator('.quarantine-table')).toBeVisible()
})

test('a spare copy can be restored, and deleting it asks first', async ({ page }) => {
  await mockQuarantine(page, [])
  let held = [quarantinedSpare]
  const calls: string[] = []
  await page.route('**/api/replacements', route => route.fulfill({ json: [] }))
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: held }))
  await page.route(`**/api/quarantine/duplicates/${quarantinedSpare.id}/restore`, route => {
    calls.push('restore')
    held = []
    return route.fulfill({ status: 204 })
  })
  await page.route(`**/api/quarantine/duplicates/${quarantinedSpare.id}`, route => {
    calls.push(route.request().method())
    held = []
    return route.fulfill({ status: 204 })
  })
  await page.goto('/#/quarantine')

  page.once('dialog', dialog => dialog.dismiss())
  await page.getByRole('button', { name: 'Delete copy' }).click()
  expect(calls).toEqual([])

  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Delete copy' }).click()
  await expect(page.getByTestId('quarantined-duplicates')).toHaveCount(0)
  expect(calls).toEqual(['DELETE'])

  held = [quarantinedSpare]
  await page.goto('/#/settings')
  await page.goto('/#/quarantine')
  await page.getByRole('button', { name: 'Restore copy' }).click()
  await expect(page.getByTestId('quarantined-duplicates')).toHaveCount(0)
  expect(calls).toEqual(['DELETE', 'restore'])
})

test('a refused delete says why and the copy stays', async ({ page }) => {
  await mockQuarantine(page, [])
  await page.route('**/api/replacements', route => route.fulfill({ json: [] }))
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: [quarantinedSpare] }))
  await page.route(`**/api/quarantine/duplicates/${quarantinedSpare.id}`, route => route.fulfill({
    status: 409, contentType: 'application/json',
    body: JSON.stringify({ code: 'duplicates.quarantineRefused', error: 'The kept copy changed, so the quarantined copies stay.' }),
  }))
  await page.goto('/#/quarantine')

  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Delete copy' }).click()

  await expect(page.getByText('The kept copy changed, so the quarantined copies stay.')).toBeVisible()
  await expect(page.getByTestId('quarantined-duplicate')).toBeVisible()
})

test('a page with nothing held shows the empty state', async ({ page }) => {
  await mockQuarantine(page, [])
  await page.route('**/api/replacements', route => route.fulfill({ json: [] }))
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: [] }))
  await page.goto('/#/quarantine')

  await expect(page.getByText(/^Nothing|No /).first()).toBeVisible()
  await expect(page.getByTestId('quarantined-duplicates')).toHaveCount(0)
})

test('removing the last of a chosen kind shows what is left', async ({ page }) => {
  let held = [quarantinedSpare]
  await mockQuarantine(page)
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: held }))
  await page.route(`**/api/quarantine/duplicates/${quarantinedSpare.id}/restore`, route => { held = []; return route.fulfill({ status: 204 }) })
  await page.goto('/#/quarantine')

  await page.getByRole('button', { name: 'Duplicate (1)' }).click()
  await expect(page.locator('.quarantine-table')).toHaveCount(0)
  await page.getByRole('button', { name: 'Restore copy' }).click()

  await expect(page.locator('.quarantine-table')).toBeVisible()
})

test('a delete that stopped part-way explains itself and can be retried', async ({ page }) => {
  await mockQuarantine(page, [])
  await page.route('**/api/quarantine/duplicates', route => route.fulfill({ json: [{ ...quarantinedSpare, state: 'Purging', attentionReason: 'A folder is redirected.' }] }))
  await page.goto('/#/quarantine')

  const row = page.getByTestId('quarantined-duplicate')
  await expect(row).toContainText('A folder is redirected.')
  await expect(row.getByRole('button', { name: 'Delete copy' })).toBeVisible()
  await expect(row.getByRole('button', { name: 'Restore copy' })).toHaveCount(0)
})


// Enough evidence that the review is several screens long.
async function mockLongReview(page: Page) {
  await mockQuarantine(page)
  const checks = Array.from({ length: 40 }, (_, index) => ({ name: `Check ${index + 1}`, outcome: 'Passed', detail: `Evidence line ${index + 1}.` }))
  await page.route('**/api/replacements/1', route => json(route, { ...detail, verificationReportJson: JSON.stringify({ checks }) }))
}

test('the decision bar stays in view while the comparison and checks scroll by, and settles clear of the end', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 720 })
  await mockLongReview(page)
  await page.goto('/#/quarantine/1')
  const bar = page.locator('[data-decision-bar]')
  await expect(page.getByText('Evidence line 40.')).toBeAttached()
  await expect(bar).toBeInViewport({ ratio: 1 })

  for (const target of [page.getByRole('heading', { name: 'Compare files', level: 2 }), page.getByRole('heading', { name: 'Verification', level: 2 }), page.getByText('Evidence line 20.')]) {
    await target.scrollIntoViewIfNeeded()
    await expect(bar).toBeInViewport({ ratio: 1 })
    await expect(bar.getByRole('button', { name: 'Approve & free space' })).toBeInViewport({ ratio: 1 })
    await expect(bar.getByRole('button', { name: 'Reject (roll back)' })).toBeInViewport({ ratio: 1 })
  }

  // At the end of the page the bar sits in its own place: the last check and the way back are not under it.
  await page.locator('main').evaluate(main => main.scrollTo({ top: main.scrollHeight }))
  const barBox = (await bar.boundingBox())!
  const lastCheck = (await page.getByText('Evidence line 40.').boundingBox())!
  const back = (await page.locator('[data-quarantine-review] > a.btn').boundingBox())!
  expect(lastCheck.y + lastCheck.height).toBeLessThanOrEqual(barBox.y)
  expect(barBox.y + barBox.height).toBeLessThanOrEqual(back.y)
  await expect(page.locator('[data-quarantine-review] > a.btn')).toBeInViewport({ ratio: 1 })
})

for (const width of [320, 375]) {
  test(`the decision bar wraps inside a ${width}px phone without taking over the screen`, async ({ page }) => {
    await page.setViewportSize({ width, height: 667 })
    await mockLongReview(page)
    await page.goto('/#/quarantine/1')
    const bar = page.locator('[data-decision-bar]')
    await expect(bar).toBeInViewport({ ratio: 1 })
    await page.getByText('Evidence line 20.').scrollIntoViewIfNeeded()
    await expect(bar).toBeInViewport({ ratio: 1 })
    const box = (await bar.boundingBox())!
    expect(box.x).toBeGreaterThanOrEqual(0)
    expect(box.x + box.width).toBeLessThanOrEqual(width)
    expect(box.height).toBeLessThanOrEqual(667 * 0.45)
    for (const button of await bar.getByRole('button').all()) {
      const bounds = (await button.boundingBox())!
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(box.x + box.width)
      expect(bounds.height).toBeGreaterThanOrEqual(44)
      // A label squeezed onto several lines would mean the buttons shrank instead of wrapping.
      expect(bounds.height).toBeLessThanOrEqual(52)
    }
    expect(await bar.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1)
    expect(await page.locator('main').evaluate(main => main.scrollWidth - main.clientWidth)).toBeLessThanOrEqual(1)
  })
}

test('on a short landscape screen the decision bar scrolls with the page instead of covering it', async ({ page }) => {
  await page.setViewportSize({ width: 740, height: 360 })
  await mockLongReview(page)
  await page.goto('/#/quarantine/1')
  const bar = page.locator('[data-decision-bar]')
  await expect(bar).toBeAttached()
  expect(await bar.evaluate(element => getComputedStyle(element).position)).toBe('static')
  await expect(bar).not.toBeInViewport()
})

test('recent replacements read relative and older ones as a date to the minute', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 720 })
  const recent = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString()
  const older = new Date(Date.now() - 10 * 24 * 60 * 60 * 1000)
  // Minutes pinned away from 47, so ":47" can only come from the seconds, whatever the clock says.
  older.setUTCMinutes(12, 47)
  await mockQuarantine(page, [
    { ...replacement, id: 1, finalPath: '/media/Recent.mp4', replacedAt: recent },
    { ...replacement, id: 2, finalPath: '/media/Older.mp4', replacedAt: older.toISOString() },
  ])
  await page.goto('/#/quarantine')
  const recentRow = page.getByRole('row').filter({ hasText: 'Recent.mp4' })
  const olderRow = page.getByRole('row').filter({ hasText: 'Older.mp4' })
  await expect(recentRow.locator('time')).toHaveText('2 hours ago')
  await expect(recentRow.locator('time')).toHaveAttribute('datetime', recent)
  const olderTime = olderRow.locator('time')
  await expect(olderTime).toContainText(String(older.getFullYear()))
  await expect(olderTime).not.toContainText(':47')
  await expect(olderTime).not.toContainText('ago')

  await page.goto('/#/quarantine/2')
  const reviewTime = page.locator('[data-quarantine-review] header time')
  await expect(reviewTime).toHaveAttribute('datetime', older.toISOString())
  await expect(reviewTime).not.toContainText(':47')
})
