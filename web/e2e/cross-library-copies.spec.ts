import { expect, test } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { artwork } from '../scripts/docs-fixtures.mjs'

for (const width of [375, 812, 1440, 2048]) for (const theme of ['light', 'dark']) {
  test(`cross-library artwork and preferences fit ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
    let decision: { choice: string; keepIds: number[]; savedAt: string } | null = null
    let includeArchive = true
    const writes: unknown[] = []
    await page.route('**/api/**', route => {
      const url = new URL(route.request().url()), path = url.pathname
      if (path.endsWith('/thumbnail')) return route.fulfill({ contentType: 'image/svg+xml', body: artwork(1) })
      if (path.endsWith('/duplicates/review')) {
        const body = route.request().postDataJSON(); writes.push(body)
        decision = body.choice === 'Review' ? null : { choice: body.choice, keepIds: body.keepIds, savedAt: '2026-10-11T18:00:00Z' }
        return route.fulfill({ status: 204 })
      }
      if (path.endsWith('/duplicates')) {
        if (route.request().method() === 'POST') includeArchive = url.searchParams.getAll('libraryIds').includes('2')
        return route.fulfill({ json: { libraryId: 1, status: 'Completed', progress: { checked: 2, skipped: 0, total: 2, bytesRead: 0, reused: 2 }, error: null,
          libraries: [{ id: 1, name: 'Films', root: '/media/films', mediaType: 'Film' }, ...(includeArchive ? [{ id: 2, name: 'Archive', root: '/media/archive', mediaType: 'Film' }] : [])],
          result: { page: 0, hasMore: false, totalGroups: 1, truncated: false, groups: [{ sha256: 'a'.repeat(64), sizeBytes: 1024, extraCopyBytes: 1024,
            totalCopies: 2, reviewKey: 'b'.repeat(64), canDecide: true, decision,
            copies: [{ id: 1, libraryId: 1, libraryName: 'Films', libraryType: 'Film', relativePath: 'Lumen Coast (2026)/Lumen Coast.mkv', hardLinkCount: 1, checkedAt: '2026-10-01T12:00:00Z', reused: true,
              aliases: [{ id: 9, libraryId: 2, libraryName: 'Archive', relativePath: 'Linked/Lumen Coast.mkv' }] },
              { id: 2, libraryId: 2, libraryName: 'Archive', libraryType: 'Film', relativePath: 'Archive/Lumen Coast copy.mkv', hardLinkCount: 1, checkedAt: '2026-10-01T12:00:00Z', reused: true }] }] }
        } })
      }
      return route.fulfill({ json: path === '/api/auth/status' ? { required: false }
        : path === '/api/setup' ? { version: 1, completed: true }
        : path === '/api/libraries' ? [{ id: 1, name: 'Films' }, { id: 2, name: 'Archive' }, { id: 3, name: 'Photos' }]
        : path === '/api/jobs' ? [] : {} })
    })
    await page.goto('/#/inventory/duplicates')
    await page.getByLabel('Library', { exact: true }).selectOption('1')
    const group = page.locator('.duplicate-group')
    await expect(page.getByLabel('Archive', { exact: true })).toBeChecked()
    await expect(group.getByText('Other references to this file', { exact: true })).toBeVisible()
    await expect(group.locator('img')).toHaveCount(3)
    await expect(group.getByRole('button', { name: 'Save keep choices', exact: true })).toBeDisabled()
    const keepers = group.getByRole('checkbox', { name: /Keep this copy/ })
    await expect(keepers.nth(0)).not.toBeChecked()
    const preset = group.getByLabel('Choose which copies to keep', { exact: true })
    await preset.selectOption('all')
    await expect(keepers.nth(0)).toBeChecked(); await expect(keepers.nth(1)).toBeChecked()
    await preset.selectOption('library:1')
    await expect(keepers.nth(0)).toBeChecked(); await expect(keepers.nth(1)).not.toBeChecked()
    await preset.selectOption('manual')
    await keepers.nth(0).check(); await keepers.nth(1).check()
    await group.getByRole('button', { name: 'Save keep choices', exact: true }).click()
    await expect(group.getByText('Keep choices saved', { exact: true })).toBeVisible()
    expect(writes).toContainEqual({ reviewKey: 'b'.repeat(64), choice: 'Keep', keepIds: [1, 2] })
    await page.reload(); await page.getByLabel('Library', { exact: true }).selectOption('1')
    await expect(keepers.nth(0)).toBeChecked(); await expect(keepers.nth(1)).toBeChecked()
    await page.getByText('Compare with other libraries (1)', { exact: true }).click()
    await page.getByRole('checkbox', { name: 'Photos', exact: true }).check()
    await expect(page.getByText(/Your library selection differs/)).toBeVisible()
    await expect(group.getByRole('button', { name: 'Save keep choices', exact: true })).toBeDisabled()
    await page.getByRole('checkbox', { name: 'Photos', exact: true }).uncheck()
    await expect(group.getByRole('button', { name: 'Save keep choices', exact: true })).toBeEnabled()
    await page.getByText('Compare with other libraries (1)', { exact: true }).click()
    const violations = (await new AxeBuilder({ page }).include('.duplicate-view').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze()).violations
    expect(violations).toEqual([])
    expect(await page.locator('main').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    await page.setViewportSize({ width, height: 2600 })
    await group.scrollIntoViewIfNeeded()
    await group.screenshot({ path: testInfo.outputPath(`cross-library-${width}-${theme}.png`) })
    await page.setViewportSize({ width, height: 1000 })
    await group.getByRole('button', { name: 'Ignore this group', exact: true }).click()
    await expect(group.getByText('Ignored', { exact: true })).toBeVisible()
    await group.locator('summary').click()
    await group.getByRole('button', { name: 'Review again', exact: true }).click()
    await expect(group.getByText('Needs review', { exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Find exact copies', exact: true }).click()
    await expect(page.getByText(/Your library selection differs/)).toHaveCount(0)
  })
}

test('review failure preserves chosen keepers and broken artwork uses a stable placeholder', async ({ page }) => {
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path.endsWith('/duplicates/review')) return route.fulfill({ status: 409, json: { error: 'Files changed. Scan again.' } })
    return route.fulfill({ json: path === '/api/auth/status' ? { required: false } : path === '/api/setup' ? { completed: true }
      : path === '/api/libraries' ? [{ id: 1, name: 'Photos' }]
      : path.endsWith('/duplicates') ? { libraryId: 1, status: 'Completed', error: null, progress: { checked: 2, total: 2, skipped: 0, bytesRead: 0 },
          result: { groups: [{ sha256: 'a'.repeat(64), sizeBytes: 100, extraCopyBytes: null, reviewKey: 'b'.repeat(64), canDecide: true,
            copies: [{ id: 1, relativePath: 'a.jpg', libraryName: 'Photos', libraryType: 'Photo', hardLinkCount: null }, { id: 2, relativePath: 'b.jpg', libraryName: 'Photos', libraryType: 'Photo', hardLinkCount: null }] }] } }
      : path === '/api/jobs' ? [] : {} })
  })
  await page.goto('/#/inventory/duplicates'); await page.getByLabel('Library', { exact: true }).selectOption('1')
  const keeper = page.getByRole('checkbox', { name: /Keep this copy: Photos a.jpg/ })
  await keeper.focus(); await page.keyboard.press('Space'); await expect(keeper).toBeChecked()
  await page.getByRole('button', { name: 'Save keep choices', exact: true }).click()
  await expect(page.getByText('Files changed. Scan again.', { exact: true })).toBeVisible()
  await expect(keeper).toBeChecked()
  await expect(page.locator('.duplicate-group img')).toHaveCount(0)
  await expect(page.locator('.duplicate-group [data-thumbnail][data-shape="square"]')).toHaveCount(3)
})

test('a deleted comparison library stays explicit until removed and rescanned', async ({ page }) => {
  const starts: string[][] = []
  await page.setViewportSize({ width: 812, height: 1100 })
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url()), path = url.pathname
    if (path.endsWith('/duplicates')) {
      if (route.request().method() === 'POST') starts.push(url.searchParams.getAll('libraryIds'))
      return route.fulfill({ json: { libraryId: 1, status: 'Completed', isStale: true,
        libraries: [{ id: 1, name: 'Films', root: '/films', mediaType: 'Film' }, { id: 2, name: 'Deleted archive', root: '/archive', mediaType: 'Film' }],
        progress: { checked: 2, total: 2, skipped: 0, bytesRead: 0 }, result: { groups: [] } } })
    }
    return route.fulfill({ json: path === '/api/auth/status' ? { required: false } : path === '/api/setup' ? { completed: true }
      : path === '/api/libraries' ? [{ id: 1, name: 'Films' }] : path === '/api/jobs' ? [] : {} })
  })
  await page.goto('/#/libraries/1/duplicates')
  await page.getByText('Compare with other libraries (1)', { exact: true }).click()
  const removed = page.getByRole('checkbox', { name: /Deleted archive/ })
  await expect(removed).toBeChecked(); await expect(removed).toBeEnabled()
  await expect(page.getByText('Libraries in this report', { exact: true })).toBeVisible()
  await removed.uncheck(); await expect(removed).toBeDisabled()
  await expect(page.getByText(/Your library selection differs/)).toBeVisible()
  await page.evaluate(() => document.documentElement.style.fontSize = '200%')
  const overflow = await page.locator('main').evaluate(main => [...main.querySelectorAll('*')].filter(el =>
    el.getBoundingClientRect().right > main.getBoundingClientRect().right + 1).map(el => `${el.tagName}.${el.className}`))
  expect(await page.locator('main').evaluate(el => el.scrollWidth <= el.clientWidth), JSON.stringify(overflow)).toBe(true)
  await page.getByRole('button', { name: 'Find exact copies', exact: true }).click()
  expect(starts).toEqual([[]])
})
