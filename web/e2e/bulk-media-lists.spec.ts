import { expect, test, type Page, type Route } from '@playwright/test'

// The bulk actions of the Excluded tab and the Inventory page. These tests cover what the
// operator sees: which rows are picked, what the confirmation says, what the request carries,
// and what the result reports — including the rows a batch could not handle.

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Tv', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  normalizeOpusChannelLayout: true,
  keepAudioLanguages: null, keepSubtitleLanguages: null, reencodeLossyAudio: false,
  targetImageFormat: null, imageQuality: null, reencodeLossyImages: false,
  imageDownscaleMode: 'None', imageDownscaleValue: 0, moveOnComplete: false,
  targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: null, minVmafMin: null,
  vmafQualityGateEnabled: false, minVmafCatastrophicMin: null, clipVmafEnabled: null,
  vmafFrameSubsample: null, durationTolerancePercent: 1, requireAudioRetained: true,
  requireSubtitlesRetained: false, requireSizeReduction: true,
  minimumSizeSavingPercent: null, maximumSizeSavingPercent: null,
  audioLoudnessGateEnabled: false, maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false, maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true, minimumImageSsim: 0.95, imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, videoQualityStrategy: 'Fixed',
  lastAutoEnqueueAt: null, fileCount: 1,
}

function exclusion(id: number, relativePath: string, libraryId = 1) {
  return {
    id,
    path: `/media/films/${relativePath}`,
    libraryId,
    relativePath,
    reason: null,
    source: 'Manual',
    createdAt: '2026-07-23T11:00:00Z',
  }
}

function mediaFile(id: number, relativePath: string) {
  return {
    id,
    libraryId: 1,
    relativePath,
    sizeBytes: 2_000_000_000,
    status: 'Probed',
    mediaKind: 'Video',
    container: 'matroska',
    videoCodec: 'h264',
    width: 1920,
    height: 1080,
    durationSeconds: 5_400,
    audioCodecs: 'aac',
    audioLanguages: 'und',
    audioTrackCount: 2,
    subtitleTrackCount: 0,
    subtitleLanguages: null,
    probedAt: '2026-07-23T11:00:00Z',
    probeError: null,
    optimisedMarker: null,
  }
}

function inventoryRow(id: number, relativePath: string) {
  return { file: mediaFile(id, relativePath), eligible: true, reason: null }
}

async function routeExcludedTab(page: Page, requests: unknown[], exclusions: unknown[]) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/libraries/1') return json(route, library)
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [],
      hdrHandlings: ['Exclude', 'TonemapToSdr'],
      videoCodecs: ['h264', 'hevc', 'av1'], containers: ['mp4', 'mkv'],
      encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: ['veryslow'],
      imageFormats: ['webp'],
    })
    if (path === '/api/candidates') return json(route, [])
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 0, skipped: 0 }])
    if (path === '/api/exclusions') return json(route, exclusions)
    if (path === '/api/exclusions/batch/remove') {
      requests.push(route.request().postDataJSON())
      return json(route, {
        requested: 2,
        succeeded: [exclusions[0].id, exclusions[1].id],
        failures: [],
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

test('the excluded tab picks exclusions and lifts them in one request', async ({ page }) => {
  const requests: unknown[] = []
  const exclusions = [exclusion(7, 'a.mkv'), exclusion(8, 'b.mkv'), exclusion(9, 'c.mkv')]
  await routeExcludedTab(page, requests, exclusions)

  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await expect(page.getByText('a.mkv', { exact: true })).toBeVisible()

  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
  await expect(page.getByText('2 of 3 selected', { exact: true })).toBeVisible()

  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Remove exclusions' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Remove the exclusions for 2 files?')
  // The confirmation says plainly that the original file is not touched.
  await expect(dialog).toContainText('Nothing is deleted, moved, or changed.')
  await dialog.getByRole('button', { name: 'Remove exclusions' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ ids: [7, 8] })
  await expect(page.getByText('2 succeeded')).toBeVisible()
  // The lifted rows leave the list, and the ones that are still excluded stay.
  await expect(page.getByText('a.mkv', { exact: true })).toBeHidden()
  await expect(page.getByText('b.mkv', { exact: true })).toBeHidden()
  await expect(page.getByText('c.mkv', { exact: true })).toBeVisible()
})

test('the header checkbox of the excluded tab picks every shown exclusion', async ({ page }) => {
  const requests: unknown[] = []
  const exclusions = [exclusion(7, 'a.mkv'), exclusion(8, 'b.mkv'), exclusion(9, 'c.mkv')]
  await routeExcludedTab(page, requests, exclusions)

  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await page.getByRole('checkbox', { name: 'Select every row' }).check()
  await expect(page.getByText('3 selected', { exact: true })).toBeVisible()
})

test('the excluded tab reports an exclusion it could not lift and keeps it picked', async ({ page }) => {
  const requests: unknown[] = []
  const exclusions = [exclusion(7, 'a.mkv'), exclusion(8, 'b.mkv')]
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/libraries/1') return json(route, library)
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV'], ruleProfiles: ['ConservativeHevc'], ruleProfileSpecs: [],
      hdrHandlings: ['Exclude'], videoCodecs: ['h264'], containers: ['mkv'],
      encoderPresets: ['quick'], legacyEncoderPresets: ['veryslow'], imageFormats: ['webp'],
    })
    if (path === '/api/candidates') return json(route, [])
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 0, skipped: 0 }])
    if (path === '/api/exclusions') return json(route, exclusions)
    if (path === '/api/exclusions/batch/remove') {
      requests.push(route.request().postDataJSON())
      return json(route, {
        requested: 2,
        succeeded: [7],
        failures: [{ id: 8, code: 'exclusion.notFound', message: 'The exclusion is no longer known.' }],
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Remove exclusions' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Remove exclusions' }).click()

  await expect.poll(() => requests.length).toBe(1)
  await expect(page.getByText('1 succeeded')).toBeVisible()
  await expect(page.getByText('1 could not be handled')).toBeVisible()
  // The row that failed stays picked, so it can be tried again; the lifted row left the list.
  await expect(page.getByText('1 selected', { exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Select b.mkv' })).toBeChecked()
})

test('the excluded tab drops the selection when another library is opened', async ({ page }) => {
  const requests: unknown[] = []
  const second = { ...library, id: 2, name: 'Shows', path: '/media/shows' }
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library, second])
    if (path === '/api/libraries/1') return json(route, library)
    if (path === '/api/libraries/2') return json(route, second)
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV'], ruleProfiles: ['ConservativeHevc'], ruleProfileSpecs: [],
      hdrHandlings: ['Exclude'], videoCodecs: ['h264'], containers: ['mkv'],
      encoderPresets: ['quick'], legacyEncoderPresets: ['veryslow'], imageFormats: ['webp'],
    })
    if (path === '/api/candidates') return json(route, [])
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 0, skipped: 0 }])
    if (path === '/api/exclusions') {
      // The exclusions belong to the library that is being edited.
      const libraryId = Number(url.searchParams.get('libraryId') ?? '1')
      return json(route, libraryId === 2 ? [exclusion(21, 'z.mkv', 2)] : [exclusion(7, 'a.mkv')])
    }
    if (path === '/api/exclusions/batch/remove') {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 1, succeeded: [7], failures: [] })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await expect(page.getByText('1 selected', { exact: true })).toBeVisible()

  // Another library shows other exclusions, so the pick made under the first one is dropped.
  await page.goto('/#/libraries/2/configure/source')
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await expect(page.getByText('z.mkv', { exact: true })).toBeVisible()
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()
  await expect(page.getByRole('checkbox', { name: 'Select z.mkv' })).not.toBeChecked()
})

async function routeInventory(page: Page, requests: unknown[], overrides: { total?: number; items?: unknown[] } = {}) {
  const items = overrides.items ?? [inventoryRow(1, 'a.mkv'), inventoryRow(2, 'b.mkv'), inventoryRow(3, 'c.mkv')]
  const total = overrides.total ?? items.length
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/inventory') {
      return json(route, {
        items,
        total,
        counts: { all: total, eligible: total, skipped: 0, unprobed: 0 },
      })
    }
    if (path === '/api/media/batch/exclude') {
      requests.push(route.request().postDataJSON())
      return json(route, {
        requested: 2,
        succeeded: [1, 2],
        failures: [],
      })
    }
    if (path === '/api/media/batch/probe') {
      requests.push(route.request().postDataJSON())
      return json(route, {
        id: 42, total: 2, completed: 0, succeeded: 0, failed: 0, skipped: 0,
        state: 'Queued', items: [],
      })
    }
    if (path === '/api/media/batch/probe/42') {
      return json(route, {
        id: 42, total: 2, completed: 2, succeeded: 1, failed: 1, skipped: 0,
        state: 'Completed',
        items: [
          { mediaFileId: 1, outcome: 'Succeeded', error: null },
          { mediaFileId: 2, outcome: 'Failed', error: 'ffprobe could not read the file.' },
        ],
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

test('the inventory picks the rows on the page and excludes them in one request', async ({ page }) => {
  const requests: unknown[] = []
  await routeInventory(page, requests)

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()

  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
  await expect(page.getByText('2 of 3 selected', { exact: true })).toBeVisible()

  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Exclude' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Exclude 2 files?')
  await expect(dialog).toContainText('Nothing on disk is changed.')
  await dialog.getByRole('button', { name: 'Exclude' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ mediaFileIds: [1, 2] })
  await expect(page.getByText('2 succeeded')).toBeVisible()
})

test('the inventory re-probes the picked rows in the background and reports what happened', async ({ page }) => {
  const requests: unknown[] = []
  await routeInventory(page, requests)

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()

  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Re-probe' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Inspect 2 files again?')
  await dialog.getByRole('button', { name: 'Re-probe' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ ids: [1, 2] })
  await expect(page.getByText('1 succeeded')).toBeVisible()
  await expect(page.getByText('1 could not be handled')).toBeVisible()
  await expect(page.getByText('#2')).toBeVisible()
})

test('the inventory drops the selection when another page is shown', async ({ page }) => {
  const requests: unknown[] = []
  await routeInventory(page, requests, { total: 60 })

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Next page' }).click()
  // Another page shows other files, so the bar goes and the pick is gone.
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()
  await expect(page.getByRole('checkbox', { name: 'Select a.mkv' })).not.toBeChecked()
})

test('the inventory header checkbox picks every row on the page and no more', async ({ page }) => {
  const requests: unknown[] = []
  await routeInventory(page, requests, { total: 60 })

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()
  await page.getByRole('checkbox', { name: 'Select every row on this page' }).check()
  // The page holds three rows, though the inventory holds sixty — the pick is the page.
  await expect(page.getByText('3 selected', { exact: true })).toBeVisible()
})

test('the inventory drops the selection when the filter or the library changes', async ({ page }) => {
  const requests: unknown[] = []
  const second = { ...library, id: 2, name: 'Shows', path: '/media/shows' }
  const seen: string[] = []
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library, second])
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/inventory') {
      seen.push(url.search)
      return json(route, {
        items: [inventoryRow(1, 'a.mkv'), inventoryRow(2, 'b.mkv'), inventoryRow(3, 'c.mkv')],
        total: 3,
        counts: { all: 3, eligible: 3, skipped: 0, unprobed: 0 },
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()

  // Another filter shows other files, so the pick made under this one is dropped.
  await page.getByRole('button', { name: /Eligible \(/ }).click()
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()
  await expect(page.getByRole('checkbox', { name: 'Select a.mkv' })).not.toBeChecked()
  await expect.poll(() => seen.at(-1)).toContain('show=eligible')

  // The same goes for another library.
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()
  await page.selectOption('#lib-filter', '2')
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()
  await expect(page.getByRole('checkbox', { name: 'Select a.mkv' })).not.toBeChecked()
  await expect.poll(() => seen.at(-1)).toContain('libraryId=2')
})

test('picking and clearing rows asks the server for nothing', async ({ page }) => {
  // Only the requests the selection could cause are counted: the list itself, a probe, or a
  // batch. The app's background polling of the queue is none of the selection's business.
  const caused: string[] = []
  const selectionRequest = (path: string) =>
    path === '/api/inventory'
    || path.startsWith('/api/media/batch')
    || (path.startsWith('/api/media/') && !path.endsWith('/thumbnail'))
    || path.startsWith('/api/exclusions')
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (selectionRequest(path)) caused.push(path)
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/inventory') {
      return json(route, {
        items: [inventoryRow(1, 'a.mkv'), inventoryRow(2, 'b.mkv'), inventoryRow(3, 'c.mkv')],
        total: 3,
        counts: { all: 3, eligible: 3, skipped: 0, unprobed: 0 },
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()
  // The list itself is fetched once, to show the page.
  await expect.poll(() => caused.length).toBe(1)

  // Picking, un-picking, picking again and clearing are all local decisions.
  const row = page.getByRole('checkbox', { name: 'Select a.mkv' })
  await row.check()
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()
  await row.uncheck()
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()
  await row.check()
  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Clear selection' }).click()
  await expect(page.locator('[data-selection-toolbar]')).toBeHidden()

  expect(caused).toEqual(['/api/inventory'])
})

test('a batch that fails says so and keeps the rows picked', async ({ page }) => {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/inventory') {
      return json(route, {
        items: [inventoryRow(1, 'a.mkv'), inventoryRow(2, 'b.mkv')],
        total: 2,
        counts: { all: 2, eligible: 2, skipped: 0, unprobed: 0 },
      })
    }
    if (path === '/api/media/batch/exclude') {
      // The server refused the whole batch.
      return route.fulfill({
        status: 500,
        contentType: 'application/json',
        body: JSON.stringify({ code: 'batch.failed', error: 'The batch could not be started.' }),
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/inventory')
  await expect(page.getByRole('button', { name: /a\.mkv/ })).toBeVisible()
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Exclude' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Exclude' }).click()

  // The failure is reported, nothing is claimed to have succeeded, and the rows stay picked so
  // the operator can try again.
  await expect(page.getByText('The batch could not be started.')).toBeVisible()
  await expect(page.getByText(/succeeded/)).toHaveCount(0)
  await expect(page.getByText('1 of 2 selected', { exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Select a.mkv' })).toBeChecked()
})

for (const terminal of ['Failed', 'Skipped', 'Cancelled'] as const) {
  test(`probe ${terminal} results stay honest and leave unfinished files selected`, async ({ page }) => {
    const requests: unknown[] = []
    await routeInventory(page, requests)
    await page.route('**/api/media/batch/probe/42', route => json(route, {
      id: 42, total: 2, completed: terminal === 'Skipped' ? 1 : 0,
      succeeded: 0, failed: 0, skipped: terminal === 'Skipped' ? 1 : 0,
      state: terminal === 'Skipped' ? 'Completed' : terminal,
      items: terminal === 'Skipped' ? [{ mediaFileId: 1, outcome: 'Skipped', error: 'No longer present.' }] : [],
    }))
    await page.goto('/#/inventory')
    await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
    await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
    await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Re-probe' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Re-probe' }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    if (terminal === 'Failed') await expect(page.getByText(/Re-probing did not finish/)).toBeVisible()
    if (terminal === 'Cancelled') await expect(page.getByText('The re-probe was stopped.')).toBeVisible()
    if (terminal === 'Skipped') {
      await expect(page.getByText('1 skipped', { exact: true })).toBeVisible()
      await expect(page.getByText(/could not be handled/)).toHaveCount(0)
    } else await expect(page.locator('[data-batch-result]')).toHaveCount(0)
    await expect(page.getByRole('checkbox', { name: 'Select b.mkv' })).toBeChecked()
  })
}

test('a probe batch closes confirmation, supports cancellation, and retries monitoring without another batch', async ({ page }) => {
  const requests: unknown[] = []
  await routeInventory(page, requests)
  let polls = 0
  let stops = 0
  await page.route('**/api/media/batch/probe/42', async route => {
    if (route.request().method() === 'DELETE') {
      stops++
      return route.fulfill({ status: 204 })
    }
    if (++polls === 1) return route.fulfill({ status: 503, json: { error: 'Connection interrupted.' } })
    return json(route, { id: 42, total: 1, completed: 0, succeeded: 0, failed: 0, skipped: 0,
      state: stops ? 'Cancelled' : 'Running', items: [] })
  })
  await page.goto('/#/inventory')
  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Re-probe' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Re-probe' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByText('Connection interrupted.')).toBeVisible()
  await expect(page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Re-probe' })).toBeDisabled()
  await page.getByRole('button', { name: 'Try again', exact: true }).click()
  await expect.poll(() => polls).toBeGreaterThan(1)
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page.getByText('The re-probe was stopped.')).toBeVisible()
  expect(requests).toHaveLength(1)
  expect(stops).toBe(1)
})
