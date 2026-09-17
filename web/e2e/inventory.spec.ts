import { expect, test, type Page, type Route } from '@playwright/test'

export const inventoryFiles = [
  { id: 7, libraryId: 1, relativePath: 'Films/Orbit (2025)/Orbit.2025.2160p.mkv', sizeBytes: 42_800_000_000, status: 'Probed', mediaKind: 'Video', container: 'matroska', videoCodec: 'h264', width: 3840, height: 2160, durationSeconds: 9330, audioCodecs: 'truehd, aac', audioLanguages: 'eng, fra', audioTrackCount: 2, subtitleTrackCount: 4, probedAt: '2026-09-16T12:00:00Z', probeError: null, optimisedMarker: null },
  { id: 8, libraryId: 1, relativePath: 'Films/Arrival/Arrival.2016.1080p.HEVC.mkv', sizeBytes: 8_200_000_000, status: 'Probed', mediaKind: 'Video', container: 'matroska', videoCodec: 'hevc', width: 1920, height: 1080, durationSeconds: 6960, audioCodecs: 'dts', audioLanguages: 'eng', audioTrackCount: 1, subtitleTrackCount: 2, probedAt: '2026-09-16T12:00:00Z', probeError: null, optimisedMarker: null },
  { id: 9, libraryId: 2, relativePath: 'Music/An extraordinarily long album name/01 - A very long track title with no artwork available.flac', sizeBytes: 28_400_000, status: 'Discovered', mediaKind: 'Audio', container: null, videoCodec: null, width: null, height: null, durationSeconds: null, audioCodecs: null, audioLanguages: null, audioTrackCount: null, subtitleTrackCount: null, probedAt: null, probeError: null, optimisedMarker: null },
]

export const poster = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><defs><linearGradient id="sky" x2="1" y2="1"><stop stop-color="#153756"/><stop offset="1" stop-color="#050915"/></linearGradient><radialGradient id="star"><stop stop-color="#ffdfa0"/><stop offset=".5" stop-color="#b57745"/><stop offset="1" stop-color="#543249"/></radialGradient></defs><path fill="url(#sky)" d="M0 0h400v600H0z"/><circle cx="268" cy="205" r="170" fill="url(#star)"/><circle cx="315" cy="168" r="166" fill="#111c32"/><path d="M0 430L170 320l230 130v150H0" fill="#17283e"/><text x="34" y="516" font-family="sans-serif" font-size="57" fill="#f0e3c9" letter-spacing="10">ORBIT</text><text x="38" y="550" font-family="sans-serif" font-size="13" fill="#bdc7d5" letter-spacing="5">ARTWORK FIXTURE</text></svg>'

export async function mockInventory(page: Page) {
  const json = (route: Route, body: unknown) => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [{ id: 1, name: 'Films', mediaType: 'Film', enabled: true }, { id: 2, name: 'Music', mediaType: 'Music', enabled: true }])
    if (path === '/api/jobs') return json(route, [])
    if (path === '/api/queue/status') return json(route, { runningJobs: 0 })
    if (path === '/api/health') return json(route, { status: 'healthy', version: 'test' })
    if (path === '/api/media/7/thumbnail') return route.fulfill({ contentType: 'image/svg+xml', body: poster })
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/inventory') {
      const library = url.searchParams.get('libraryId'), show = url.searchParams.get('show')
      const scoped = inventoryFiles.filter(f => !library || f.libraryId === Number(library))
      const items = scoped.filter(f => !show || (show === 'eligible' ? f.id === 7 : show === 'skipped' ? f.id === 8 : f.id === 9))
      return json(route, { items: items.map(file => ({ file, eligible: file.id === 9 ? null : file.id === 7, reason: file.id === 9 ? null : file.id === 7 ? 'Video codec differs from the library’s HEVC target.' : 'Already uses the target codec.' })), total: items.length, counts: { all: scoped.length, eligible: scoped.filter(f => f.id === 7).length, skipped: scoped.filter(f => f.id === 8).length, unprobed: scoped.filter(f => f.id === 9).length } })
    }
    return json(route, {})
  })
}

test('Index opens an artwork-led modal by keyboard and restores focus on close', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  await expect(page.getByRole('columnheader')).toHaveText(['File', 'Size', 'Format', 'Rule verdict'])
  const file = page.getByRole('button', { name: /Orbit.2025.2160p.mkv/ })
  await file.focus()
  await page.keyboard.press('Enter')
  const dialog = page.getByRole('dialog', { name: 'Orbit 2025 2160p' })
  await expect(dialog).toBeVisible()
  await expect(dialog.locator('[data-thumbnail] img')).toHaveJSProperty('naturalWidth', 400)
  await expect(dialog).toContainText('Video codec differs')
  await expect(dialog).toContainText('truehd, aac')
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(file).toBeFocused()
})

test('missing artwork and long names retain readable details and reachable actions on phones', async ({ page }) => {
  await mockInventory(page)
  await page.setViewportSize({ width: 375, height: 667 })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /01 - A very long/ }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.locator('[data-thumbnail]')).toBeVisible()
  await expect(dialog.locator('[data-thumbnail] img')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Probe', exact: true })).toBeInViewport()
  await expect(dialog.getByRole('button', { name: 'Close detail panel' })).toBeInViewport()
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await expect(dialog.getByRole('button', { name: 'Preview', exact: true })).toHaveCount(0)
})

test('an empty filter preserves the filters and explains how to return to the inventory', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  await page.getByLabel('Library', { exact: true }).selectOption('2')
  await page.getByRole('button', { name: 'Eligible (0)', exact: true }).click()
  await expect(page.getByText('No files match this filter.')).toBeVisible()
  await page.getByRole('button', { name: 'All (1)', exact: true }).click()
  await expect(page.getByRole('button', { name: /01 - A very long/ })).toBeVisible()
})

test('a slower filter response cannot replace the latest selection', async ({ page }) => {
  await mockInventory(page)
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route('**/api/inventory?**', async route => {
    if (new URL(route.request().url()).searchParams.get('show') !== 'eligible') return route.fallback()
    await gate
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ items: [{ file: inventoryFiles[0], eligible: true, reason: 'Eligible' }], total: 1, counts: { all: 3, eligible: 1, skipped: 1, unprobed: 1 } }) })
  })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: 'Eligible (1)', exact: true }).click()
  await page.getByRole('button', { name: 'Skipped (1)', exact: true }).click()
  await expect(page.getByRole('button', { name: /Arrival.2016/ })).toBeVisible()
  release()
  await expect(page.getByRole('button', { name: 'Skipped (1)', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.waitForTimeout(150)
  await expect(page.getByRole('button', { name: /Orbit.2025/ })).toHaveCount(0)
})

test('probe failures are shown inside the open modal without losing the file', async ({ page }) => {
  await mockInventory(page)
  await page.route('**/api/media/9/probe', route => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'Probe unavailable' }) }))
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /01 - A very long/ }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Probe', exact: true }).click()
  await expect(dialog.getByRole('alert')).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Probe', exact: true })).toBeEnabled()
})

test('a successful probe refreshes the open file and its preview eligibility', async ({ page }) => {
  await mockInventory(page)
  const updated = { ...inventoryFiles[2], status: 'Probed', audioCodecs: 'flac', audioTrackCount: 1, container: 'flac', durationSeconds: 251 }
  let probed = false
  await page.route('**/api/media/9/probe', route => {
    probed = true
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(updated) })
  })
  await page.route('**/api/inventory?**', route => {
    if (!probed) return route.fallback()
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ items: [{ file: updated, eligible: true, reason: 'Audio qualifies for the library’s Opus target.' }], total: 1, counts: { all: 1, eligible: 1, skipped: 0, unprobed: 0 } }) })
  })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /01 - A very long/ }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Probe', exact: true }).click()
  await expect(dialog).toContainText('Audio qualifies')
  await expect(dialog.getByRole('button', { name: 'Preview', exact: true })).toBeEnabled()
  await expect(dialog.getByRole('button', { name: 'Re-probe', exact: true })).toBeEnabled()
})
