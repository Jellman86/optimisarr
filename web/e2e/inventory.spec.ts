import { expect, test, type Page, type Route } from '@playwright/test'

export const inventoryFiles = [
  { id: 7, libraryId: 1, relativePath: 'Films/Orbit (2025)/Orbit.2025.2160p.mkv', sizeBytes: 42_800_000_000, status: 'Probed', mediaKind: 'Video', container: 'matroska', videoCodec: 'h264', width: 3840, height: 2160, durationSeconds: 9330, audioCodecs: 'truehd, aac', audioLanguages: 'eng, fra', audioTrackCount: 2, subtitleTrackCount: 4, probedAt: '2026-09-16T12:00:00Z', probeError: null, optimisedMarker: null },
  { id: 8, libraryId: 1, relativePath: 'Films/Arrival/Arrival.2016.1080p.HEVC.mkv', sizeBytes: 8_200_000_000, status: 'Probed', mediaKind: 'Video', container: 'matroska', videoCodec: 'hevc', width: 1920, height: 1080, durationSeconds: 6960, audioCodecs: 'dts', audioLanguages: 'eng', audioTrackCount: 1, subtitleTrackCount: 2, probedAt: '2026-09-16T12:00:00Z', probeError: null, optimisedMarker: null },
  { id: 9, libraryId: 2, relativePath: 'Music/An extraordinarily long album name/01 - A very long track title with no artwork available.flac', sizeBytes: 28_400_000, status: 'Discovered', mediaKind: 'Audio', container: null, videoCodec: null, width: null, height: null, durationSeconds: null, audioCodecs: null, audioLanguages: null, audioTrackCount: null, subtitleTrackCount: null, probedAt: null, probeError: null, optimisedMarker: null },
]

export const poster = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><defs><linearGradient id="sky" x2="1" y2="1"><stop stop-color="#153756"/><stop offset="1" stop-color="#050915"/></linearGradient><radialGradient id="star"><stop stop-color="#ffdfa0"/><stop offset=".5" stop-color="#b57745"/><stop offset="1" stop-color="#543249"/></radialGradient></defs><path fill="url(#sky)" d="M0 0h400v600H0z"/><circle cx="268" cy="205" r="170" fill="url(#star)"/><circle cx="315" cy="168" r="166" fill="#111c32"/><path d="M0 430L170 320l230 130v150H0" fill="#17283e"/><text x="34" y="516" font-family="sans-serif" font-size="57" fill="#f0e3c9" letter-spacing="10">ORBIT</text><text x="38" y="550" font-family="sans-serif" font-size="13" fill="#bdc7d5" letter-spacing="5">ARTWORK FIXTURE</text></svg>'

// Opening Orbit's plan needs a whole plan: an empty object leaves the page without a source to show.
const orbitPlan = {
  mediaFileId: 7, libraryId: 1, eligible: true, reason: 'Video codec differs from the library’s HEVC target.',
  kind: 'Video', action: 'Encode',
  source: {
    relativePath: inventoryFiles[0].relativePath, sizeBytes: inventoryFiles[0].sizeBytes, container: 'matroska',
    videoCodec: 'h264', width: 3840, height: 2160, durationSeconds: 9330, isHdr: false, isDolbyVision: false,
    interlaced: false, audioCodecs: 'truehd, aac', maxAudioChannels: 8, audioTrackCount: 2, subtitleTrackCount: 4,
  },
  picture: { codec: 'hevc', crf: 24, width: null, height: null, shrinkToHeight: null, frameRateCap: null, tonemapToSdr: false, deinterlace: false, cropBlackBars: false, noiseReduction: 'Off', upscales: false },
  image: null,
  sound: { codec: 'aac', bitrateKbps: 160, downmixToStereo: false },
  audioTracks: [{ index: 0, language: 'eng', kept: true }, { index: 1, language: 'fra', kept: false }],
  subtitleTracks: [],
  file: { container: 'mkv', fileName: 'Orbit.2025.2160p.mkv', renamed: false, mayBecomeMatroska: false },
}

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
    if (path === '/api/media/7/plan') return json(route, orbitPlan)
    if (path === '/api/library-options') return json(route, { ruleProfileSpecs: [] })
    if (path === '/api/settings') return json(route, { dryRunMode: false, replacementQuarantineRetentionDays: 30 })
    if (path === '/api/inventory') {
      const library = url.searchParams.get('libraryId'), show = url.searchParams.get('show')
      const search = url.searchParams.get('search')?.toLowerCase(), sort = url.searchParams.get('sort')
      const scoped = inventoryFiles.filter(f => (!library || f.libraryId === Number(library)) && (!search || f.relativePath.toLowerCase().includes(search)))
      const items = scoped.filter(f => !show || (show === 'eligible' ? f.id === 7 : show === 'skipped' ? f.id === 8 : f.id === 9))
      // Eligible, then not yet probed, then skipped: the server's verdict order.
      const verdictRank = (f: typeof inventoryFiles[number]) => f.id === 7 ? 0 : f.id === 9 ? 1 : 2
      const fileNameOf = (p: string) => p.split(/[\\/]/).pop() || p
      const byPath = (a: typeof inventoryFiles[number], b: typeof inventoryFiles[number]) => a.relativePath.localeCompare(b.relativePath)
      const byName = (a: typeof inventoryFiles[number], b: typeof inventoryFiles[number]) => fileNameOf(a.relativePath).localeCompare(fileNameOf(b.relativePath))
      if (sort) {
        const [column, direction] = sort.split('_')
        const sign = direction === 'desc' ? -1 : 1
        items.sort((a, b) => (column === 'size' ? sign * (a.sizeBytes - b.sizeBytes)
          : column === 'verdict' ? sign * (verdictRank(a) - verdictRank(b))
          : sign * byName(a, b)) || byPath(a, b))
      }
      return json(route, { items: items.map(file => ({ file, eligible: file.id === 9 ? null : file.id === 7, reason: file.id === 9 ? null : file.id === 7 ? 'Video codec differs from the library’s HEVC target.' : 'Already uses the target codec.' })), total: items.length, counts: { all: scoped.length, eligible: scoped.filter(f => f.id === 7).length, skipped: scoped.filter(f => f.id === 8).length, unprobed: scoped.filter(f => f.id === 9).length } })
    }
    return json(route, {})
  })
}

test('Index opens an artwork-led modal by keyboard and restores focus on close', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  await expect(page.getByRole('columnheader')).toHaveText(['', 'File', 'Size', 'Format', 'Rule verdict'])
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

test('an eligible file answers the question and opens its plan instead of queueing', async ({ page }) => {
  await mockInventory(page)
  const enqueues: string[] = []
  page.on('request', request => { if (request.url().includes('/enqueue')) enqueues.push(request.url()) })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /Orbit.2025.2160p.mkv/ }).click()
  const dialog = page.getByRole('dialog')

  const verdict = dialog.getByRole('region', { name: 'Can it be optimised?' })
  await expect(verdict).toContainText('Yes')
  await expect(verdict).toContainText('Video codec differs')
  await dialog.getByRole('link', { name: 'Optimise this file…', exact: true }).click()

  await expect(page).toHaveURL(/#\/inventory\/optimise\/7$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Optimise this file' })).toBeVisible()
  await expect(page.locator('[data-one-file-plan]')).toContainText('Orbit.2025.2160p.mkv')
  expect(enqueues).toEqual([])
})

test('a file the rules skip keeps the button, off, with the reason and where to change it', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /Arrival.2016.1080p.HEVC.mkv/ }).click()
  const dialog = page.getByRole('dialog')

  const verdict = dialog.getByRole('region', { name: 'Can it be optimised?' })
  await expect(verdict).toContainText('No')
  await expect(verdict).toContainText('Already uses the target codec.')
  await expect(verdict.getByRole('link', { name: 'Films → Which files' })).toHaveAttribute('href', '#/libraries/1/configure/files')
  const optimise = dialog.getByRole('button', { name: 'Optimise this file…', exact: true })
  await expect(optimise).toBeDisabled()
  await expect(optimise).toHaveAccessibleDescription('Already uses the target codec.')
  await expect(dialog.getByRole('button', { name: 'Preview', exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Try again', exact: true })).toHaveCount(0)
})

test('a file held back by its last failure can be tried again from its details', async ({ page }) => {
  await mockInventory(page)
  let retriedOnce = false
  const failedReason = 'Failed last time, so it isn’t tried again by itself'
  await page.route('**/api/inventory?*', route => {
    const file = inventoryFiles[1]
    const item = retriedOnce
      ? { file, eligible: true, reason: 'Video codec differs from the library’s HEVC target.', retryJobId: null }
      : { file, eligible: false, reason: failedReason, retryJobId: 55 }
    return route.fulfill({ json: { items: [item], total: 1, counts: { all: 1, eligible: retriedOnce ? 1 : 0, skipped: retriedOnce ? 0 : 1, unprobed: 0 } } })
  })
  const retries: string[] = []
  await page.route('**/api/jobs/55/retry*', route => {
    retries.push(new URL(route.request().url()).pathname)
    retriedOnce = true
    return route.fulfill({ json: { id: 55, status: 'Queued' } })
  })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /Arrival.2016.1080p.HEVC.mkv/ }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('region', { name: 'Can it be optimised?' })).toContainText(failedReason)
  const tryAgain = dialog.getByRole('button', { name: 'Try again', exact: true })
  await expect(tryAgain).toHaveAccessibleDescription(failedReason)

  await tryAgain.click()

  await expect(dialog.locator('[data-inventory-retried]')).toContainText('Added to the queue')
  await expect(dialog.locator('[data-inventory-retried]').getByRole('link', { name: 'Open the queue' })).toHaveAttribute('href', '#/queue')
  await expect(tryAgain).toHaveCount(0)
  expect(retries).toEqual(['/api/jobs/55/retry'])
})

test('trying again from the Skipped filter keeps the details open to say it was queued', async ({ page }) => {
  await mockInventory(page)
  let retried = false
  const failedReason = 'Failed last time, so it isn’t tried again by itself'
  // As the server does: once retried, the file is no longer skipped, so the Skipped page loses it.
  await page.route('**/api/inventory?*', route => {
    const show = new URL(route.request().url()).searchParams.get('show')
    const item = { file: inventoryFiles[1], eligible: false, reason: failedReason, retryJobId: 55 }
    const items = retried && show === 'skipped' ? [] : [retried ? { ...item, eligible: true, reason: 'Video codec differs from the library’s HEVC target.', retryJobId: null } : item]
    return route.fulfill({ json: { items, total: items.length, counts: { all: 1, eligible: retried ? 1 : 0, skipped: retried ? 0 : 1, unprobed: 0 } } })
  })
  let refuse = true
  await page.route('**/api/jobs/55/retry*', route => {
    if (refuse) return route.fulfill({ status: 409, json: { code: 'job.retry.alreadyActive', error: 'This file already has an active job. Wait for it to finish, or cancel it, before retrying this one.' } })
    retried = true
    return route.fulfill({ json: { id: 55, status: 'Queued' } })
  })
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /^Skipped/ }).click()
  await page.getByRole('button', { name: /Arrival.2016.1080p.HEVC.mkv/ }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('link', { name: 'Films → Which files' })).toHaveCount(0)
  const tryAgain = dialog.getByRole('button', { name: 'Try again', exact: true })

  await tryAgain.click()
  await expect(dialog.getByRole('alert')).toContainText('already has an active job')
  await expect(tryAgain).toBeEnabled()

  refuse = false
  await tryAgain.click()

  await expect(dialog).toBeVisible()
  await expect(dialog.locator('[data-inventory-retried]')).toContainText('Added to the queue')
})

test('a file that has not been probed says to probe it before it can be optimised', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /01 - A very long/ }).click()
  const optimise = page.getByRole('dialog').getByRole('button', { name: 'Optimise this file…', exact: true })

  await expect(optimise).toBeDisabled()
  await expect(optimise).toHaveAccessibleDescription('Probe this file to evaluate it against the library rules.')
})

test('the detail actions wrap instead of overflowing on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 })
  await mockInventory(page)
  await page.goto('/#/inventory')
  await page.getByRole('button', { name: /Orbit.2025.2160p.mkv/ }).click()
  const dialog = page.getByRole('dialog')
  const footer = dialog.locator('.detail-actions')

  for (const name of ['Re-probe', 'Preview']) await expect(footer.getByRole('button', { name, exact: true })).toBeInViewport({ ratio: 1 })
  await expect(footer.getByRole('link', { name: 'Optimise this file…', exact: true })).toBeInViewport({ ratio: 1 })
  expect(await footer.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1)
  expect(await dialog.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(2)
})

// The order the rows are shown in, read by file name.
async function rowNames(page: Page) {
  return page.locator('[id^="inventory-file-"]').evaluateAll(buttons => buttons.map(button => button.querySelector('.text-label')?.textContent ?? ''))
}

function recordInventoryRequests(page: Page) {
  const requests: URLSearchParams[] = []
  page.on('request', request => {
    const url = new URL(request.url())
    if (url.pathname === '/api/inventory') requests.push(url.searchParams)
  })
  return requests
}

test('search waits for a pause in typing, asks the server, and says when nothing matches', async ({ page }) => {
  await mockInventory(page)
  const requests = recordInventoryRequests(page)
  await page.goto('/#/inventory')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(3)
  await page.getByRole('checkbox', { name: /Select Orbit/ }).check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()

  const search = page.getByRole('searchbox', { name: 'Search files' })
  await expect(search).toHaveAttribute('placeholder', 'Filter by file or folder name…')
  await search.pressSequentially('orbit', { delay: 20 })
  await expect.poll(() => requests.map(params => params.get('search')).filter(Boolean)).toEqual(['orbit'])
  expect(requests.at(-1)!.get('page')).toBe('1')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(1)
  await expect(page.getByRole('button', { name: /Orbit.2025.2160p.mkv/ })).toBeVisible()
  // A pick made among the rows before the search does not carry into the new set.
  await expect(page.getByText(/^\d+ (of \d+ )?selected$/)).toHaveCount(0)

  await search.fill('nothing-here')
  await search.press('Enter')
  await expect(page.getByText('No files match “nothing-here”.')).toBeVisible()
  await page.getByRole('main').getByRole('button', { name: 'Clear search' }).last().click()
  await expect(search).toHaveValue('')
  await expect(search).toBeFocused()
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(3)
  expect(requests.at(-1)!.has('search')).toBe(false)
})

test('the clear button and Escape both empty the search field', async ({ page }) => {
  await mockInventory(page)
  await page.goto('/#/inventory')
  const search = page.getByRole('searchbox', { name: 'Search files' })
  const clear = page.getByRole('search').getByRole('button', { name: 'Clear search' })
  await expect(clear).toHaveCount(0)

  await search.fill('arrival')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(1)
  await clear.click()
  await expect(search).toHaveValue('')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(3)

  await search.fill('music')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(1)
  await search.press('Escape')
  await expect(search).toHaveValue('')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(3)
})

test('size sorts largest first, then smallest, then back to path order', async ({ page }) => {
  await mockInventory(page)
  const requests = recordInventoryRequests(page)
  await page.goto('/#/inventory')
  const header = page.getByRole('columnheader', { name: 'Size' })
  const sortButton = header.getByRole('button', { name: 'Size' })
  await expect(header).toHaveAttribute('aria-sort', 'none')

  await sortButton.click()
  await expect(header).toHaveAttribute('aria-sort', 'descending')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('size_desc')
  await expect.poll(() => rowNames(page)).toEqual(['Orbit.2025.2160p.mkv', 'Arrival.2016.1080p.HEVC.mkv', '01 - A very long track title with no artwork available.flac'])

  await sortButton.click()
  await expect(header).toHaveAttribute('aria-sort', 'ascending')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('size_asc')
  await expect.poll(() => rowNames(page)).toEqual(['01 - A very long track title with no artwork available.flac', 'Arrival.2016.1080p.HEVC.mkv', 'Orbit.2025.2160p.mkv'])

  await sortButton.click()
  await expect(header).toHaveAttribute('aria-sort', 'none')
  await expect.poll(() => requests.at(-1)?.has('sort')).toBe(false)
})

test('the verdict and file columns sort from the top of their order and only one column sorts at a time', async ({ page }) => {
  await mockInventory(page)
  const requests = recordInventoryRequests(page)
  await page.goto('/#/inventory')
  const verdict = page.getByRole('columnheader', { name: 'Rule verdict' })
  const file = page.getByRole('columnheader', { name: 'File' })

  await verdict.getByRole('button').click()
  await expect(verdict).toHaveAttribute('aria-sort', 'ascending')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('verdict_asc')
  await expect.poll(() => rowNames(page)).toEqual(['Orbit.2025.2160p.mkv', '01 - A very long track title with no artwork available.flac', 'Arrival.2016.1080p.HEVC.mkv'])

  await verdict.getByRole('button').click()
  await expect(verdict).toHaveAttribute('aria-sort', 'descending')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('verdict_desc')

  await file.getByRole('button').click()
  await expect(file).toHaveAttribute('aria-sort', 'ascending')
  await expect(verdict).toHaveAttribute('aria-sort', 'none')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('name_asc')
  await expect.poll(() => rowNames(page)).toEqual(['01 - A very long track title with no artwork available.flac', 'Arrival.2016.1080p.HEVC.mkv', 'Orbit.2025.2160p.mkv'])
  expect(requests.at(-1)!.get('page')).toBe('1')
  await file.getByRole('button').click()
  await expect(file).toHaveAttribute('aria-sort', 'descending')
  await expect.poll(() => requests.at(-1)?.get('sort')).toBe('name_desc')
  await expect.poll(() => rowNames(page)).toEqual(['Orbit.2025.2160p.mkv', 'Arrival.2016.1080p.HEVC.mkv', '01 - A very long track title with no artwork available.flac'])
})

test('rows are compact on a desktop and keep a finger-sized target on a phone', async ({ page }) => {
  await mockInventory(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/#/inventory')
  await expect(page.locator('[id^="inventory-file-"]')).toHaveCount(3)
  for (const height of await page.locator('tbody tr').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))) {
    expect(height).toBeGreaterThanOrEqual(48)
    expect(height).toBeLessThanOrEqual(60)
  }

  await page.setViewportSize({ width: 375, height: 667 })
  for (const height of await page.locator('[id^="inventory-file-"]').evaluateAll(buttons => buttons.map(button => button.getBoundingClientRect().height))) {
    expect(height).toBeGreaterThanOrEqual(44)
  }
})

for (const width of [320, 375]) {
  test(`the search field and sortable headers fit a ${width}px phone without sideways scrolling`, async ({ page }) => {
    await mockInventory(page)
    await page.setViewportSize({ width, height: 700 })
    await page.goto('/#/inventory')
    const search = page.getByRole('searchbox', { name: 'Search files' })
    await search.fill('a very long search that should never push the page sideways')
    const box = (await search.boundingBox())!
    expect(box.x).toBeGreaterThanOrEqual(0)
    expect(box.x + box.width).toBeLessThanOrEqual(width)
    const clear = (await page.getByRole('search').getByRole('button', { name: 'Clear search' }).boundingBox())!
    expect(clear.x + clear.width).toBeLessThanOrEqual(box.x + box.width + 1)
    expect(await page.locator('main').evaluate(main => main.scrollWidth - main.clientWidth)).toBeLessThanOrEqual(1)
    await search.fill('')
    for (const name of ['File', 'Rule verdict']) {
      const sort = (await page.getByRole('columnheader', { name }).getByRole('button').boundingBox())!
      expect(sort.height).toBeGreaterThanOrEqual(44)
    }
    expect(await page.locator('main').evaluate(main => main.scrollWidth - main.clientWidth)).toBeLessThanOrEqual(1)
  })
}
