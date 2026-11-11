import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

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

const SKIP_EFFICIENT = /^Skip already-efficient sources/

async function mockLibraries(page: Page, configuredLibrary = library, superResolutionOn = false, extraOptions: Record<string, unknown> = {}) {
  let currentLibrary = { ...configuredLibrary }
  const { settings: settingOverrides, ...optionOverrides } = extraOptions
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [currentLibrary])
    if (path === '/api/settings') return json(route, {
      remoteWorkersAvailable: false, remoteWorkersEnabled: false,
      dryRunMode: false, replacementQuarantineRetentionDays: 30,
      superResolution: { enabled: superResolutionOn, preset: 'mixed', scale: 2, noiseLevel: 1, device: 'auto', binaryPath: '', modelsPath: '' },
      ...(settingOverrides as Record<string, unknown> | undefined),
    })
    if (path === '/api/fs/browse') {
      const folder = new URL(route.request().url()).searchParams.get('path') || '/'
      return json(route, folder === '/'
        ? { path: '/', parent: null, directories: [{ name: 'media', path: '/media' }, { name: 'optimised', path: '/optimised' }] }
        : folder === '/media'
          ? { path: '/media', parent: '/', directories: [{ name: 'films', path: '/media/films' }] }
          : { path: folder, parent: '/', directories: [] })
    }
    // The same answers the server gives: a folder inside the library is refused by name.
    if (path === '/api/libraries/target-folder-check') {
      const folder = String(route.request().postDataJSON().path)
      return json(route, folder.startsWith('/media/films')
        ? { path: folder, allowed: false, reason: 'insideLibrary', libraryName: 'Films', message: 'inside', availableBytes: null }
        : { path: folder, allowed: true, reason: 'none', libraryName: null, message: 'ok', availableBytes: 1_500_000_000_000 })
    }
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [
        { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ScottsSettings', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'TonemapToSdr', videoAudioCodec: 'aac', videoAudioBitrateKbps: 96, audioTargetCodec: 'aac', audioBitrateKbps: 96, downmixToStereo: true },
        { profile: 'RemuxCleanup', codec: null, container: 'mkv', crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'TrackCleanup', codec: null, container: null, crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
      ],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'],
      videoCodecs: ['h264', 'hevc', 'av1'], containers: ['mp4', 'mkv'],
      encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: ['veryslow'],
      imageFormats: ['webp'],
      ...optionOverrides,
    })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 0, skipped: 1 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, {
      path: configuredLibrary.path, exists: true, readable: true, writable: true, ok: true,
      message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/',
      fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000,
      atomicWithWork: true, atomicWithQuarantine: true,
    })
    if (path === '/api/libraries/1' && route.request().method() === 'PUT') {
      currentLibrary = { ...currentLibrary, ...route.request().postDataJSON() }
      return json(route, currentLibrary)
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

test('library cards and overview open exact-copy review with that library selected', async ({ page }) => {
  await mockLibraries(page)
  const scanPosts: string[] = []
  await page.route('**/api/libraries/1/duplicates', route => {
    if (route.request().method() === 'POST') scanPosts.push(route.request().method())
    return json(route, { libraryId: 1, status: 'NotStarted', result: null, error: null,
      progress: { checked: 0, skipped: 0, total: 0, bytesRead: 0 } })
  })
  await page.goto('/#/libraries')
  await page.locator('[data-library-card="1"]').getByRole('link', { name: 'Find duplicates', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/duplicates$/)
  await expect(page.getByLabel('Library', { exact: true })).toHaveValue('1')
  await page.getByRole('link', { name: 'Films', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/configure$/)
  await page.getByRole('button', { name: 'Find duplicates', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/duplicates$/)
  await page.reload()
  await expect(page.getByLabel('Library', { exact: true })).toHaveValue('1')
  expect(scanPosts).toEqual([])
})

const filesChoice = (page: Page, name: 'ask' | 'auto' | 'elsewhere') => page.getByRole('radio', {
  name: {
    ask: 'Replace the original after I press Replace',
    auto: 'Replace the original automatically',
    elsewhere: 'Save optimised files in another folder',
  }[name],
  exact: true,
})

for (const mediaType of ['Film', 'TV', 'Music', 'Photo', 'Other']) {
  test(`${mediaType} replacing automatically needs risk acknowledgement and confirmation before saving`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType })
    await page.goto('/#/libraries/1/configure/automate')
    const setting = filesChoice(page, 'auto')
    await expect(filesChoice(page, 'ask')).toBeChecked()
    await expect(setting).not.toBeChecked()
    await expect(page.getByText(/asks you to confirm first, and explains the risk/)).toBeVisible()
    await setting.click()
    const dialog = page.getByRole('dialog', { name: 'Do you really, really mean it?' })
    await expect(dialog).toBeVisible()
    await expect(setting).not.toBeChecked()
    await expect(dialog.getByText(/Misconfigured settings can leave you with broken files/)).toBeVisible()
    await expect(dialog.getByText(/Originals stay in Quarantine/)).toBeVisible()
    const enable = dialog.getByRole('button', { name: 'Yes, really. Replace automatically' })
    await expect(enable).toBeDisabled()
    await expect(dialog.getByRole('button', { name: 'Keep it off' })).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(dialog).not.toBeVisible()
    await expect(setting).not.toBeChecked()
    await expect(filesChoice(page, 'ask')).toBeChecked()
    await expect(setting).toBeFocused()
    await setting.click()
    await expect(enable).toBeDisabled()
    await dialog.getByRole('checkbox', { name: 'I understand that incorrect settings can damage my media.' }).check()
    await enable.click()
    await expect(dialog).not.toBeVisible()
    await expect(setting).toBeChecked()
    const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).postDataJSON()).toMatchObject({ autoReplace: true, moveOnComplete: false, mediaType })
  })
}

test('replacing automatically can be switched back to asking without confirmation and stays off', async ({ page }) => {
  await mockLibraries(page, { ...library, autoReplace: true })
  await page.goto('/#/libraries/1/configure/automate')
  await expect(filesChoice(page, 'auto')).toBeChecked()
  await filesChoice(page, 'ask').check()
  await expect(page.getByRole('dialog')).not.toBeVisible()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ autoReplace: false, moveOnComplete: false })
  await expect(page.getByText('Updated library "Films".', { exact: true })).toBeVisible()
  await expect(page.locator('[data-library-workflow]')).toBeEnabled()
  await expect(filesChoice(page, 'ask')).toBeChecked()
})

test('new libraries ask before replacing even when an existing library replaces automatically', async ({ page }) => {
  await mockLibraries(page, { ...library, autoReplace: true })
  await page.goto('/#/libraries/new/automate')
  await expect(page).toHaveURL(/#\/libraries\/new\/automate$/)
  await expect(filesChoice(page, 'ask')).toBeChecked()
})

test('what happens to files is one choice of three, and each says where both files end up', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/automate')
  const choice = page.locator('[data-files-choice]')
  await expect(choice.getByRole('radio')).toHaveCount(3)
  await expect(choice.getByText('A file that fails a check is never used. Your original stays exactly as it was.')).toBeVisible()
  await expect(choice.getByText(/Moves to Quarantine. It still uses its space and can be put back. After 30 days it is deleted. The space is freed and this can't be undone./)).toBeVisible()
  await expect(choice.getByText('Takes its place in your library, in the same folder.')).toBeVisible()
  await expect(choice.getByText(/one setting for every library, in Settings → Files & safety. Quarantine is not a backup./)).toBeVisible()
  await expect(choice.locator('[data-files-choice-dry-run]')).toHaveCount(0)

  await filesChoice(page, 'elsewhere').check()
  await expect(choice.getByText('Stays in your library. It is never moved or deleted.')).toBeVisible()
  await expect(choice.getByText('You end up with two files. Your library is not changed. You save no space.')).toBeVisible()
  await expect(choice.getByText('Plex, Jellyfin, Emby, Sonarr and Radarr are not told about the copies.')).toBeVisible()
  await expect(choice.getByText(/Moves to Quarantine/)).toHaveCount(0)
})

test('a library kept until approved and locked by dry-run says both beside the choice', async ({ page }) => {
  await mockLibraries(page, library, false, { settings: { dryRunMode: true, replacementQuarantineRetentionDays: 0 } })
  await page.goto('/#/libraries/1/configure/automate')
  const choice = page.locator('[data-files-choice]')
  await expect(choice.getByText(/It stays there until you approve it, so you free the space yourself./)).toBeVisible()
  await expect(choice.locator('[data-files-choice-dry-run]')).toContainText('Replacing is locked (dry-run) for every library.')
})

test('saving elsewhere needs a folder, refuses one inside a library with the reason, and saves an allowed one', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/automate')
  await filesChoice(page, 'elsewhere').check()
  const save = page.getByRole('button', { name: 'Save', exact: true })
  const folder = page.getByLabel('Folder for optimised files', { exact: true })
  await expect(page.getByText('Choose a folder before you save.').first()).toBeVisible()
  await expect(save).toBeDisabled()

  await page.getByRole('button', { name: 'Browse', exact: true }).click()
  const picker = page.getByRole('dialog', { name: 'Choose a folder' })
  await picker.getByRole('button', { name: 'media', exact: true }).click()
  await picker.getByRole('button', { name: 'films', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()
  await expect(folder).toHaveValue('/media/films')
  await expect(page.locator('[data-target-folder-state="insideLibrary"]')).toContainText('This is inside the Films library. Optimisarr would find its own output on the next scan')
  await expect(folder).toHaveAttribute('aria-invalid', 'true')
  await expect(save).toBeDisabled()

  await page.getByRole('button', { name: 'Browse', exact: true }).click()
  await picker.getByRole('button', { name: 'Up one level', exact: true }).click()
  await picker.getByRole('button', { name: 'optimised', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()
  await expect(page.locator('[data-target-folder-state="none"]')).toContainText('Not inside any library.')
  await expect(page.locator('[data-target-folder-state="none"]')).toContainText('Optimisarr can write here.')
  await expect(save).toBeEnabled()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await save.click()
  expect((await saved).postDataJSON()).toMatchObject({ moveOnComplete: true, targetFolder: '/optimised', moveOverwrite: false, autoReplace: false })
})

test('a library saved with both switches on shows saving elsewhere, says so, and saves replace automatically off', async ({ page }) => {
  await mockLibraries(page, { ...library, autoReplace: true, moveOnComplete: true, targetFolder: '/optimised' })
  await page.goto('/#/libraries')
  await expect(page.locator('[data-library-card="1"]')).toContainText('saves to another folder')
  await expect(page.locator('[data-library-card="1"]')).not.toContainText('auto-replace')
  await page.goto('/#/libraries/1/configure/automate')
  await expect(filesChoice(page, 'elsewhere')).toBeChecked()
  await expect(filesChoice(page, 'auto')).not.toBeChecked()
  await expect(page.locator('[data-files-choice-both]')).toContainText('Saving in another folder won, so nothing was replaced.')
  await page.getByRole('checkbox', { name: /Overwrite a file that is already there/ }).check()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ moveOnComplete: true, targetFolder: '/optimised', moveOverwrite: true, autoReplace: false })
  await expect(page.locator('[data-files-choice-both]')).toHaveCount(0)
})

test('the choice of three fits a phone without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 667 })
  await mockLibraries(page, { ...library, moveOnComplete: true, targetFolder: '/media/films/optimised' })
  await page.goto('/#/libraries/1/configure/automate')
  await expect(page.locator('[data-target-folder-state="insideLibrary"]')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
  const accessibility = await new AxeBuilder({ page }).include('[data-files-choice]').analyze()
  expect(accessibility.violations).toEqual([])
})

test('the replace automatically confirmation fits a phone and stops pulsing with reduced motion', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 667 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/automate')
  await filesChoice(page, 'auto').click()
  const dialog = page.getByRole('dialog', { name: 'Do you really, really mean it?' })
  await expect(dialog).toBeVisible()
  expect(await dialog.locator('[data-auto-accept-warning]').evaluate(element => getComputedStyle(element).animationName)).toBe('none')
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await expect(dialog.getByRole('button', { name: 'Keep it off' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Keep it off' }).click()
  await expect(filesChoice(page, 'auto')).not.toBeChecked()
})

for (const [theme, viewport] of [
  ['light', { width: 1440, height: 900 }],
  ['dark', { width: 667, height: 375 }],
] as const) {
  test(`replace automatically ${theme} confirmation stays accessible and cancellation never saves`, async ({ page }, testInfo) => {
    await page.setViewportSize(viewport)
    await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), theme)
    await mockLibraries(page, { ...library, name: 'A long library name '.repeat(6) })
    const writes: string[] = []
    page.on('request', request => { if (request.method() === 'PUT') writes.push(request.url()) })
    await page.goto('/#/libraries/1/configure/automate')
    const setting = filesChoice(page, 'auto')
    await setting.click()
    const dialog = page.getByRole('dialog', { name: 'Do you really, really mean it?' })
    await expect(dialog).toBeVisible()
    const box = await dialog.boundingBox()
    expect(box).not.toBeNull()
    expect(box!.height).toBeLessThanOrEqual(viewport.height)
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
    const accessibility = await new AxeBuilder({ page }).include('dialog').analyze()
    expect(accessibility.violations).toEqual([])
    await page.screenshot({ path: testInfo.outputPath(`auto-accept-${theme}.png`), animations: 'disabled' })
    await dialog.getByRole('checkbox', { name: 'I understand that incorrect settings can damage my media.' }).check()
    await page.mouse.click(2, 2)
    await expect(dialog).not.toBeVisible()
    await expect(setting).not.toBeChecked()
    expect(writes).toEqual([])
    await setting.click()
    await expect(dialog.getByRole('button', { name: 'Yes, really. Replace automatically' })).toBeDisabled()
    await dialog.getByRole('button', { name: 'Keep it off' }).click()
    expect(writes).toEqual([])
  })
}

test('the library list leads with the count, one Scan button, and the rest in a menu', async ({ page }) => {
  await mockLibraries(page, { ...library, autoEnqueueEnabled: true, autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '09:00', autoReplace: true })
  await page.goto('/#/libraries')

  const card = page.locator('[data-library-card="1"]')
  await expect(card).toBeVisible()
  // The API serialises the media type as "Tv"; the badge must still read the translated label.
  await expect(card.getByText('TV', { exact: true })).toBeVisible()
  await expect(card.getByText('1', { exact: true })).toBeVisible()
  await expect(card.getByText('All already optimal')).toBeVisible()
  await expect(card.getByText('Auto-optimise 00:00–09:00 · auto-replace')).toBeVisible()
  await expect(card.getByText('access ok')).toHaveCount(0)

  // One primary action on the card; the secondary and destructive ones are one click away.
  await expect(card.getByRole('button')).toHaveCount(2)
  await expect(card.getByRole('button', { name: 'Scan' })).toBeVisible()
  await expect(card.getByRole('button', { name: 'Delete' })).toHaveCount(0)

  await card.getByRole('button', { name: 'More actions for Films' }).click()
  const menu = page.getByRole('menu', { name: 'More actions for Films' })
  await expect(menu.getByRole('menuitem')).toHaveText(['Enqueue', 'Configure', 'Delete'])
  await menu.getByRole('menuitem', { name: 'Configure' }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/configure$/)
})

test('the library list says how many files are ready when some are', async ({ page }) => {
  await mockLibraries(page)
  await page.route('**/api/candidates/summary', (route) => json(route, [{ libraryId: 1, eligible: 12, skipped: 3 }]))
  await page.goto('/#/libraries')

  const card = page.locator('[data-library-card="1"]')
  await expect(card.getByText('12 ready to optimise')).toBeVisible()
  await expect(card.getByText('All already optimal')).toHaveCount(0)
})

test('the library list stacks on a phone without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockLibraries(page)
  await page.goto('/#/libraries')
  await page.locator('[data-library-card="1"]').waitFor()

  const fit = await page.locator('main').evaluate((main) => ({
    scrollWidth: main.scrollWidth,
    clientWidth: main.clientWidth,
  }))
  expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth)
})

const pageTab = (page: Page, name: string) => page.getByRole('navigation', { name: 'Library settings pages' }).getByRole('button', { name, exact: true })
const savedRequest = (page: Page) => page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
const row = (page: Page, label: string) => page.locator(`[data-setting-row="${label}"]`)

test('the overview says what the library does in sentences, with a tag for anything it removes', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'TV', keepAudioLanguages: 'eng', targetContainer: 'source', minFileSizeBytes: 209_715_200,
    vmafQualityGateEnabled: true, minVmafHarmonicMean: 93, minVmafMin: 80, minVmafCatastrophicMin: 50, videoQualityStrategy: 'AdaptiveVmaf' })
  await page.route('**/api/candidates/summary', (route) => json(route, [{ libraryId: 1, eligible: 12, skipped: 3 }]))
  await page.goto('/#/libraries/1/configure')
  const summary = page.locator('[data-library-summary]')
  await expect(summary.getByRole('heading', { name: 'What this library does' })).toBeVisible()
  await expect(summary.locator('[data-summary-line]')).toHaveCount(5)
  await expect(summary.locator('[data-summary-line="files"]')).toContainText('Only video files over 200 MB. Skips files already in HEVC (H.265)')
  await expect(summary.locator('[data-summary-line="files"] [data-summary-detail]')).toHaveText('12 picked · 3 skipped, each with its reason in Candidates')
  await expect(summary.locator('[data-summary-line="video"]')).toContainText('Each file gets the smallest size that still scores 93 (Visually lossless) against the original.')
  await expect(summary.locator('[data-summary-line="video"] [data-summary-tag="warn"]')).toHaveText(['Renames some files'])
  await expect(summary.locator('[data-summary-line="audio"] [data-summary-tag="warn"]')).toHaveText('Removes tracks')
  await expect(summary.locator('[data-summary-line="checks"]')).toContainText('A new file must play from start to end, be the same length')
  await expect(summary.locator('[data-summary-line="schedule"]')).toContainText('You queue files yourself. A file that passes waits for you, then replaces the original. Originals stay in Quarantine for 30 days, then they are deleted.')
  await expect(summary.locator('[data-summary-line="schedule"] [data-summary-tag="warn"]')).toHaveText('Deletes originals after 30 days')
  await expect(page.locator('[data-library-facts]')).toHaveText('/media/films · 1 file · 12 can be optimised')
  const accessibility = await new AxeBuilder({ page }).include('[data-library-summary]').analyze()
  expect(accessibility.violations).toEqual([])
})

test('Edit on a summary line opens the page with every control for it, and the summary follows unsaved edits', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('button', { name: 'Edit Video', exact: true }).click()
  await expect(page).toHaveURL(/\/configure\/video$/)
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Video')
  await page.getByRole('checkbox', { name: 'Remove black bars', exact: true }).check()
  await expect(row(page, 'Remove black bars').locator('[data-setting-tag]')).toHaveText('Changes the picture')
  await pageTab(page, 'Overview').click()
  const video = page.locator('[data-summary-line="video"]')
  await expect(video).toContainText('Removes black bars.')
  await expect(video.locator('[data-summary-tag="warn"]').first()).toHaveText('Changes the picture')
  await expect(page.locator('[data-summary-unsaved]')).toHaveText('Includes changes you have not saved yet.')
  // The counts describe the saved rules, so they step aside while something is unsaved.
  await expect(page.locator('[data-summary-line="files"] [data-summary-detail]')).toHaveCount(0)
})

test('a film library has an overview and five pages, a music library has no Video page and a photo library has Images', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  const tabs = page.getByRole('navigation', { name: 'Library settings pages' }).getByRole('button')
  await expect(tabs).toHaveText(['Overview', 'Which files', 'Video', 'Audio & subtitles', 'Checks', 'Schedule & replace'])
  await expect(pageTab(page, 'Overview')).toHaveAttribute('aria-current', 'page')
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await expect(tabs).toHaveText(['Overview', 'Which files', 'Audio', 'Checks', 'Schedule & replace'])
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await expect(tabs).toHaveText(['Overview', 'Which files', 'Images', 'Checks', 'Schedule & replace'])
  await page.getByLabel('Media type', { exact: true }).selectOption('Other')
  await expect(tabs).toHaveText(['Overview', 'Which files', 'Video', 'Audio & subtitles', 'Images', 'Checks', 'Schedule & replace'])
  // Nothing is called Advanced, on any page.
  for (const name of ['Which files', 'Video', 'Audio & subtitles', 'Images', 'Checks', 'Schedule & replace']) {
    await pageTab(page, name).click()
    await expect(page.locator('[data-library-workflow]').getByText(/Advanced/)).toHaveCount(0)
  }
})

test('pages preserve drafts across the page list, the breadcrumb and browser history', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await pageTab(page, 'Which files').click()
  await expect(page.getByLabel('Media type', { exact: true })).toHaveValue('TV')
  await page.getByLabel('Name', { exact: true }).fill('Film archive')
  await pageTab(page, 'Video').click()
  await page.getByLabel('Encoder effort', { exact: true }).selectOption('efficient')
  await page.goBack()
  await expect(page).toHaveURL(/\/configure\/files$/)
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Film archive' }).click()
  await expect(page).toHaveURL(/\/configure$/)
  await pageTab(page, 'Which files').click()
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Film archive')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ name: 'Film archive', encoderPreset: 'efficient' })
})

test('bookmarks to the rooms the editor used to have land on the page that took them in', async ({ page }) => {
  await mockLibraries(page)
  for (const [room, heading] of [
    ['source/advanced', 'Which files'], ['encode', 'Video'], ['encode/quality', 'Video'], ['encode/video/advanced', 'Video'],
    ['encode/audio/advanced', 'Audio & subtitles'], ['verify/advanced', 'Checks'], ['automate', 'Schedule & replace'],
  ]) {
    await page.goto(`/#/libraries/1/configure/${room}`)
    await expect(page.getByRole('heading', { level: 1 }), room).toHaveText(heading)
  }
  // A page this kind of library does not have shows the one it does.
  await page.goto('/#/libraries/1/configure/encode/images')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Video')
})

test('unknown bookmarked pages return to the overview instead of rendering an empty editor', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/constructor')
  await expect(page.locator('[data-library-summary]')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Films', exact: true, level: 1 })).toBeVisible()
})

test('track cleanup keeps the language choices and leaves out the encoder pages', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.getByRole('group', { name: 'What to do' }).getByRole('radio')).toHaveCount(3)
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).not.toBeChecked()
  await expect(page.locator('#lib-vmaf-policy')).toHaveCount(0)
  await expect(page.locator('#lib-codec')).toHaveCount(0)
  await expect(page.getByText('This only removes tracks. Set the languages to keep, or every file is skipped.')).toBeVisible()
  await page.locator('.callout').getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(page.getByLabel('Keep audio languages', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Keep subtitle languages', { exact: true })).toBeVisible()
  await expect(page.locator('#lib-video-audio-codec')).toHaveCount(0)
  await pageTab(page, 'Which files').click()
  await expect(page.locator('#lib-minsize')).toHaveCount(0)
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await expect(pageTab(page, 'Video')).toHaveCount(0)
})

test('new video libraries pick the smallest file that meets the target, with one named target', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/new/video')
  await expect(page.getByLabel('How quality is chosen', { exact: true })).toHaveValue('target')
  await expect(page.getByLabel('How quality is chosen', { exact: true }).locator('option')).toHaveText([
    'Smallest file that meets the target', 'Fixed setting, checked against the target', 'Fixed setting, no picture check'])
  await expect(page.locator('#lib-vmaf-policy')).toHaveValue('lossless')
  await expect(page.locator('#lib-vmaf-policy').locator('option')).toHaveText([
    'Space-saver · 80', 'Balanced · 85', 'High · 90', 'Visually lossless · 93', 'Archival · 96', 'Custom'])
  await expect(page.locator('[data-vmaf-floors]')).toHaveText('Whole file 93 Weakest scenes 80 Worst frame 50')
  await expect(row(page, 'How quality is chosen').locator('[data-setting-tag]')).toHaveText('Experimental')
  await expect(page.getByRole('group', { name: 'Preset' }).getByRole('button', { pressed: true })).toHaveCount(1)
})

test('how quality is chosen is one control: the target, a checked fixed setting, or no picture check', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const how = page.getByLabel('How quality is chosen', { exact: true })
  const target = page.locator('#lib-vmaf-policy')
  const sampling = page.locator('#lib-vmaf-sampling')
  // This library was saved with a fixed setting and no VMAF gate.
  await expect(how).toHaveValue('fixed-unchecked')
  await expect(target).toBeDisabled()
  await expect(sampling).toBeDisabled()
  await expect(row(page, 'Quality target').locator('[data-setting-reason]')).toHaveText('Not used. This library does not check the picture.')

  await how.selectOption('target')
  await expect(target).toBeEnabled()
  await expect(target).toHaveValue('lossless')
  await expect(sampling).toHaveValue('samples')
  await expect(row(page, 'How quality is chosen').locator('details')).toContainText('More about this')
  await target.selectOption('high')
  await sampling.selectOption('full-3')
  let saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoQualityStrategy: 'AdaptiveVmaf', vmafQualityGateEnabled: true,
    minVmafHarmonicMean: 90, minVmafMin: 75, minVmafCatastrophicMin: 45, clipVmafEnabled: false, vmafFrameSubsample: 3 })

  await how.selectOption('fixed-checked')
  saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoQualityStrategy: 'Fixed', vmafQualityGateEnabled: true, minVmafHarmonicMean: 90 })

  await how.selectOption('fixed-unchecked')
  saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoQualityStrategy: 'Fixed', vmafQualityGateEnabled: false, minVmafHarmonicMean: null })
})

test('custom quality numbers are edited in place and a saved sampling pair outside the three choices is kept', async ({ page }) => {
  await mockLibraries(page, { ...library, vmafQualityGateEnabled: true, minVmafHarmonicMean: 91, minVmafMin: 78, minVmafCatastrophicMin: 44,
    clipVmafEnabled: true, vmafFrameSubsample: 5 })
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#lib-vmaf-policy')).toHaveValue('custom')
  await expect(page.getByLabel('Whole file', { exact: true })).toHaveValue('91')
  await expect(page.locator('#lib-vmaf-sampling')).toHaveValue('custom')
  await expect(page.locator('#lib-vmaf-sampling option:checked')).toHaveText('Custom (three samples, every 5 frames)')
  await page.getByLabel('Weakest scenes', { exact: true }).fill('95')
  await expect(page.locator('#lib-vmaf-error')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await pageTab(page, 'Checks').click()
  await page.getByRole('alert').getByRole('button').click()
  await expect(page).toHaveURL(/\/configure\/video$/)
  await page.getByLabel('Weakest scenes', { exact: true }).fill('80')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ minVmafMin: 80, clipVmafEnabled: true, vmafFrameSubsample: 5 })
})

test("choosing a preset fills in its whole bundle, and switching away from Scott's restores the next one's", async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const presets = page.getByRole('group', { name: 'Preset' })
  await presets.getByRole('button', { name: /Scott's/ }).click()
  await expect(page.getByText(/HEVC, with HDR converted to SDR and audio as AAC 96 kb\/s stereo/)).toBeVisible()
  await expect(page.locator('#lib-hdr')).toHaveValue('TonemapToSdr')
  await expect(row(page, 'HDR files').locator('[data-setting-tag]')).toHaveText('Changes the picture')
  await pageTab(page, 'Audio & subtitles').click()
  await expect(page.locator('#lib-video-audio-codec')).toHaveValue('aac')
  await expect(page.locator('#lib-surround')).toHaveValue('downmix')
  await expect(page.locator('[data-audio-encoding-preset]')).toContainText('AAC · 96 kbps')
  await pageTab(page, 'Video').click()
  await presets.getByRole('button', { name: /Efficiency/ }).click()
  await expect(page.locator('#lib-hdr')).toHaveValue('Preserve')
  await pageTab(page, 'Audio & subtitles').click()
  await expect(page.locator('#lib-video-audio-codec')).toHaveValue('copy')
  await expect(page.locator('#lib-surround')).toHaveValue('keep')
  await expect(page.locator('[data-audio-encoding-preset]')).toHaveCount(0)
  await expect(row(page, 'Audio bitrate').locator('[data-setting-reason]')).toHaveText('Not used. Audio is left untouched.')
})

test('the presets can be seen into: each one lists what it sets, and says what it leaves alone', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const table = page.locator('[data-preset-table]')
  await expect(table.locator('table')).toBeHidden()
  await table.locator('summary').click()
  await expect(table.getByRole('columnheader')).toHaveText(['Compatibility', 'Balanced', 'Efficiency', "Scott's"])
  await expect(table.getByRole('row', { name: /^Video codec/ }).getByRole('cell')).toHaveText(['H.264', 'HEVC (H.265)', 'AV1', 'HEVC (H.265)'])
  await expect(table.getByRole('row', { name: /^HDR files/ }).getByRole('cell')).toHaveText(['Exclude (skip HDR)', 'Exclude (skip HDR)', 'Preserve HDR', 'Tonemap to SDR'])
  await expect(table.getByRole('row', { name: /^Audio in the new file/ }).getByRole('cell')).toHaveText(['Re-encode to AAC', 'Re-encode to AAC', 'Leave audio untouched', 'Re-encode to AAC'])
  await expect(table.getByRole('row', { name: /^Audio bitrate/ }).getByRole('cell')).toHaveText(['160 kbps', '160 kbps', '—', '96 kbps'])
  await expect(table.getByRole('row', { name: /^Surround sound/ }).getByRole('cell')).toHaveText(['Keep surround', 'Keep surround', 'Keep surround', 'Mix down to stereo'])
  await expect(table).toContainText('Everything else is left as it is: the quality target, the picture settings, languages, checks and the schedule.')
  const accessibility = await new AxeBuilder({ page }).include('[data-preset-table]').analyze()
  expect(accessibility.violations).toEqual([])
})

test('a value changed from its preset says so, says what the preset uses, and can be reset on its own', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const state = page.locator('[data-preset-state]')
  const codec = row(page, 'Video codec')
  await expect(state).toHaveText('Balanced preset')
  await expect(codec.locator('[data-setting-origin="preset"]')).toHaveText('From Balanced')
  await expect(page.locator('#lib-codec')).toHaveValue('hevc')
  await expect(page.locator('#lib-codec option')).toHaveText(['H.264', 'HEVC (H.265)', 'AV1'])
  await expect(page.getByText('Profile default')).toHaveCount(0)

  await page.locator('#lib-codec').selectOption('av1')
  await page.locator('#lib-crf').fill('20')
  await expect(state).toHaveText('Balanced preset · 2 changes')
  await expect(codec.locator('[data-setting-origin="changed"]')).toContainText('Changed by you')
  await expect(codec.locator('[data-setting-origin="changed"]')).toContainText('Balanced uses HEVC (H.265)')
  await codec.getByRole('button', { name: 'Reset', exact: true }).click()
  await expect(page.locator('#lib-codec')).toHaveValue('hevc')
  await expect(state).toHaveText('Balanced preset · 1 change')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ targetVideoCodec: null, qualityCrf: 20, ruleProfile: 'ConservativeHevc' })

  // Choosing a preset again is a deliberate choice of that preset: the change on top of it goes.
  await page.getByRole('group', { name: 'Preset' }).getByRole('button', { name: /Compatibility/ }).click()
  await expect(state).toHaveText('Compatibility preset')
  await expect(page.locator('[data-crf-value]')).toHaveText('20')
  await expect(row(page, 'Quality (CRF)').locator('[data-setting-origin="preset"]')).toHaveText('From Compatibility')
})

test('the checks page lists the checks that are always on, then every optional one for this media type', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/checks')
  await expect(page.locator('[data-core-checks] li')).toHaveText([
    'It opens and plays from start to end without errors.',
    'It is the same length as the original, within the tolerance below.',
    'It still has its picture, with the same colour information, and nothing is cut off at the end.',
  ])
  await expect(page.locator('#lib-duration-tolerance')).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toBeChecked()
  await expect(row(page, 'Picture quality').locator('[data-check-value]')).toHaveText('Not checked')
  await row(page, 'Picture quality').getByRole('button', { name: 'Video', exact: true }).click()
  await expect(page).toHaveURL(/\/configure\/video$/)
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await pageTab(page, 'Checks').click()
  await expect(page.getByRole('checkbox', { name: 'Require all subtitle tracks to be retained' })).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })).toBeVisible()
  await expect(row(page, 'Picture quality')).toHaveCount(0)
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await pageTab(page, 'Checks').click()
  await expect(page.locator('[data-core-checks] li')).toHaveText(['It opens without errors and is in the format you asked for.'])
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Preserve image EXIF/ICC metadata' })).toBeVisible()
  await expect(page.locator('#lib-image-ssim')).toBeVisible()
  await expect(page.locator('#lib-duration-tolerance')).toHaveCount(0)
})

test('every control has one home, on the page for its topic, for every media kind', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/files')
  await expect(page.locator('#lib-minsize')).toBeVisible()
  await expect(page.locator('#lib-priority')).toHaveCount(0)
  await pageTab(page, 'Video').click()
  // Shrinking tall video and skipping it are one row, because people confuse them.
  await expect(row(page, 'Tall video').locator('#lib-downscale')).toBeVisible()
  await expect(row(page, 'Tall video').locator('#lib-maxheight')).toBeVisible()
  await pageTab(page, 'Schedule & replace').click()
  await expect(page.locator('#lib-priority')).toHaveJSProperty('tagName', 'SELECT')
  await expect(filesChoice(page, 'elsewhere')).toBeVisible()
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await pageTab(page, 'Audio').click()
  await expect(page.locator('#lib-audio-codec')).toBeVisible()
  await expect(page.locator('#lib-audio-bitrate')).toHaveCount(0)
  await page.locator('[data-audio-encoding-preset]').getByRole('button', { name: 'Custom', exact: true }).click()
  await expect(page.locator('#lib-audio-bitrate')).toBeVisible()
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await pageTab(page, 'Images').click()
  await expect(page.getByLabel('Target format', { exact: true }).locator('option')).toHaveText(['JPEG', 'WebP'])
  await expect(page.locator('#lib-image-downscale')).toBeVisible()
  await expect(page.locator('#lib-image-quality')).toBeVisible()
})

test('the Opus surround-layout conversion shows only for Opus, is on by default and can be turned off', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioTargetCodec: 'opus' })
  await page.goto('/#/libraries/1/configure/audio')
  const toggle = page.getByRole('checkbox', { name: 'Normalise Opus surround layout', exact: true })
  await expect(toggle).toBeChecked()

  await toggle.uncheck()
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ normalizeOpusChannelLayout: false })

  await toggle.check()
  const resaved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await resaved).postDataJSON()).toMatchObject({ normalizeOpusChannelLayout: true })

  // It only means something for Opus, so another codec leaves it out, and keeps the saved value.
  await page.getByLabel('Audio codec', { exact: true }).selectOption('mp3')
  await expect(toggle).toHaveCount(0)
  const kept = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await kept).postDataJSON()).toMatchObject({ audioTargetCodec: 'mp3', normalizeOpusChannelLayout: true })
})

test('audio encoding presets change the codec budget while leaving the quality check unchanged', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.005 })
  await page.goto('/#/libraries/1/configure/audio')
  const control = page.locator('[data-audio-encoding-preset]')
  await control.getByRole('button', { name: 'High', exact: true }).click()
  await expect(control).toContainText('192 kbps')
  await page.getByLabel('Audio codec', { exact: true }).selectOption('opus')
  await expect(control).toContainText('160 kbps')
  await page.getByLabel('Audio codec', { exact: true }).selectOption('mp3')
  await expect(control).toContainText('256 kbps')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioTargetCodec: 'mp3', audioBitrateKbps: 256, audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.005 })
})

test('existing audio budgets stay unchanged when a library is opened or its format changes', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioBitrateKbps: 173 })
  await page.goto('/#/libraries/1/configure/audio')
  await expect(page.locator('[data-audio-encoding-preset]')).toContainText('173 kbps')
  await page.getByLabel('Audio codec', { exact: true }).selectOption('mp3')
  await expect(page.locator('[data-audio-encoding-preset]')).toContainText('173 kbps')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: 173 })
})

test('video soundtrack presets follow the codec and step aside when audio is left untouched', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/audio')
  const codec = page.getByLabel('Audio in the new file', { exact: true })
  // Balanced converts to AAC, so the row shows what the preset does instead of "Profile default".
  await expect(codec).toHaveValue('aac')
  await expect(codec.locator('option')).toHaveText(['Leave audio untouched', 'Re-encode to AAC', 'Re-encode to Opus', 'Re-encode to MP3'])
  await expect(row(page, 'Audio in the new file').locator('[data-setting-origin="preset"]')).toHaveText('From Balanced')
  await codec.selectOption('mp3')
  const control = page.locator('[data-audio-encoding-preset]')
  await control.getByRole('button', { name: 'Very high', exact: true }).click()
  await expect(control).toContainText('320 kbps')
  await codec.selectOption('opus')
  await expect(control).toContainText('192 kbps')
  await codec.selectOption('copy')
  await expect(control).toHaveCount(0)
  await expect(page.locator('#lib-surround')).toBeDisabled()
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoAudioCodec: 'copy', videoAudioBitrateKbps: 192 })
})

test('optional check thresholds follow their switches on the same page', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/checks')
  const loudness = page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })
  await expect(page.locator('#lib-loudness-drift')).toHaveCount(0)
  await loudness.check()
  await expect(page.locator('#lib-loudness-drift')).toBeVisible()
  await loudness.uncheck()
  await expect(page.locator('#lib-loudness-drift')).toHaveCount(0)
})

test('must be smaller and by how much are one row; the maximum saving stays last, ordered, and saved', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/checks')
  const smaller = row(page, 'Require output to be smaller than the original')
  const minimum = smaller.getByLabel('by at least', { exact: true })
  await expect(minimum).toHaveValue('')
  await minimum.fill('0')
  await expect(page.locator('#lib-verification-error')).toHaveText('Minimum useful saving must be above 0% and no more than 99%.')
  await minimum.fill('10')
  const maximum = page.getByLabel('Maximum allowed saving', { exact: true })
  await expect(page.locator('[data-setting-row]').last()).toHaveAttribute('data-setting-row', 'Maximum allowed saving')
  await expect(maximum).toHaveValue('')
  await maximum.fill('5')
  await expect(page.locator('#lib-verification-error')).toHaveText('Minimum useful saving cannot exceed maximum allowed saving.')
  await maximum.fill('65')
  await maximum.evaluate(element => element.scrollIntoView({ block: 'center' }))
  const inputBounds = await maximum.boundingBox()
  const actionsBounds = await page.locator('[data-library-actions]').boundingBox()
  expect(inputBounds && actionsBounds && inputBounds.y + inputBounds.height < actionsBounds.y).toBe(true)
  await page.setViewportSize({ width: 390, height: 844 })
  await maximum.evaluate(element => element.scrollIntoView({ block: 'center' }))
  const mobileInput = await maximum.boundingBox()
  const mobileActions = await page.locator('[data-library-actions]').boundingBox()
  expect(mobileInput && mobileActions && mobileInput.y + mobileInput.height < mobileActions.y).toBe(true)
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ minimumSizeSavingPercent: 10, maximumSizeSavingPercent: 65 })

  // Switched off, both numbers stay in view, disabled, and the maximum says why.
  await smaller.getByRole('checkbox', { name: 'Require output to be smaller than the original' }).uncheck()
  await expect(minimum).toBeDisabled()
  await expect(maximum).toBeDisabled()
  await expect(row(page, 'Maximum allowed saving').locator('[data-setting-reason]')).toHaveText('Only used when the file must be smaller.')
})

test('invalid settings stay discoverable after navigating away from their field', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/audio')
  await page.getByLabel('Keep subtitle languages', { exact: true }).fill('english')
  await pageTab(page, 'Schedule & replace').click()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Use comma-separated 2- or 3-letter language codes only.' }).click()
  await expect(page.getByLabel('Keep subtitle languages', { exact: true })).toHaveValue('english')
  await page.getByLabel('Keep subtitle languages', { exact: true }).fill('eng')
  await expect(row(page, 'Keep subtitle languages').locator('[data-setting-tag]')).toHaveText('Removes tracks')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled()
})

test('the same-codec size limit can be under 1 GB and a saved fraction stays as it was', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Film' })
  await page.goto('/#/libraries/1/configure/files')
  await page.getByRole('checkbox', { name: /Re-encode large files already in the target codec/ }).check()
  const size = page.getByRole('spinbutton', { name: 'Re-encode when larger than', exact: true })
  await size.fill('0.5')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ reencodeSameCodecAboveBytes: 536_870_912 })
  await page.reload()
  await expect(size).toHaveValue('0.5')
})

test('reopening a same-codec size limit does not round it or count as an edit', async ({ page }) => {
  // 0.3 GB is what the editor saves for "0.3", and it does not divide back to exactly 0.3.
  await mockLibraries(page, { ...library, mediaType: 'Film', reencodeSameCodecAboveBytes: Math.round(0.3 * 1024 ** 3) })
  await page.goto('/#/libraries/1/configure/files')
  await expect(page.getByRole('spinbutton', { name: 'Re-encode when larger than', exact: true })).toHaveValue('0.3')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
})

for (const [typed, bytes] of [['0.123', 132_070_244], ['0.001', 1_073_742]] as const) {
  test(`a same-codec limit of ${typed} GB survives saving something else`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType: 'Film', reencodeSameCodecAboveBytes: bytes })
    await page.goto('/#/libraries/1/configure/files')
    await expect(page.getByRole('spinbutton', { name: 'Re-encode when larger than', exact: true })).toHaveValue(typed)
    await page.getByRole('checkbox', { name: SKIP_EFFICIENT }).uncheck()
    const saved = savedRequest(page)
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).postDataJSON()).toMatchObject({ reencodeSameCodecAboveBytes: bytes, skipEfficientSources: false })
  })
}

test('the quality number says what a hardware encoder really uses, for a preset value and a changed one', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Film' }, false, { hardwareQualityHeadroom: 4 })
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('[data-crf-value]')).toHaveText('24')
  await expect(page.getByText(/Software encoders start at this number\. Hardware encoders .* start at 20, 4 lower.*Adaptive VMAF and quality retries can move it/)).toBeVisible()
  await page.getByRole('slider', { name: 'Quality (CRF)', exact: true }).fill('28')
  await expect(page.getByText(/start at 24, 4 lower/)).toBeVisible()
  await row(page, 'Quality (CRF)').getByRole('button', { name: 'Reset', exact: true }).click()
  await expect(page.getByText(/start at 20, 4 lower/)).toBeVisible()
})

test('encoder effort uses portable choices and a changed value shows on the overview', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const effort = page.getByLabel('Encoder effort', { exact: true })
  await expect(effort.locator('option')).toHaveText(['Encoder default', 'Fast', 'Balanced', 'Efficient'])
  await effort.selectOption('efficient')
  await page.locator('#lib-codec').selectOption('av1')
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Films', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/configure$/)
  await expect(page.locator('[data-summary-line="video"] [data-summary-detail]')).toHaveText('Balanced preset · 1 change')
  await expect(page.locator('[data-summary-line="video"]')).toContainText('Re-encodes video to AV1.')
})

test('legacy effort values are preserved until deliberately changed', async ({ page }) => {
  await mockLibraries(page, { ...library, encoderPreset: 'veryslow' })
  await page.goto('/#/libraries/1/configure/video')
  const effort = page.getByLabel('Encoder effort', { exact: true })
  await expect(effort).toHaveValue('veryslow')
  await expect(effort.getByText('Legacy exact value: veryslow')).toHaveCount(1)
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Name', { exact: true }).fill('Films archive')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled()
})

test('deep links reload the same page and the breadcrumb is library then page', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  await page.reload()
  const crumbs = page.getByRole('navigation', { name: 'Breadcrumb' })
  await expect(crumbs.getByRole('button')).toHaveText(['Libraries', 'Films'])
  await expect(crumbs.locator('[aria-current=page]')).toHaveText('Video')
  await expect(page.locator('#lib-hdr')).toBeVisible()
  await expect(page.locator('#lib-preset')).toBeVisible()
})

test('leaving the library warns about unsaved work while moving between its pages does not', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/files')
  await page.getByLabel('Name', { exact: true }).fill('Keep this draft')
  await pageTab(page, 'Video').click()
  page.once('dialog', dialog => dialog.dismiss())
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Libraries', exact: true }).click()
  await expect(page).toHaveURL(/\/configure\/video$/)
  await pageTab(page, 'Which files').click()
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Keep this draft')
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries$/)
})

test('every page fits a phone without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await mockLibraries(page, { ...library, mediaType: 'Other' })
  for (const name of ['', '/files', '/video', '/audio', '/images', '/checks', '/schedule']) {
    await page.goto('/#/libraries/1/configure' + name)
    await page.locator('[data-library-workflow]').waitFor()
    const fit = await page.locator('main').evaluate(main => ({ scrollWidth: main.scrollWidth, clientWidth: main.clientWidth }))
    expect(fit.scrollWidth, name).toBeLessThanOrEqual(fit.clientWidth)
  }
})

test('a longer explanation opens in the page, stays open while the control is used, and fits a translated landscape phone', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await page.addInitScript(() => localStorage.setItem('optimisarr:locale', 'de'))
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  await page.locator('html').evaluate(element => { element.style.fontSize = '125%' })
  // No explanation on this page hides in a hover bubble.
  await expect(page.locator('main [data-library-workflow] [role="tooltip"]')).toHaveCount(0)
  const explanations = page.locator('[data-setting-row] details')
  expect(await explanations.count()).toBeGreaterThan(10)
  const crop = page.locator('[data-setting-row]').filter({ has: page.locator('#lib-noise') })
  const more = crop.locator('details')
  await more.locator('summary').focus()
  await page.keyboard.press('Enter')
  await expect(more).toHaveAttribute('open', '')
  await page.locator('#lib-noise').selectOption('Strong')
  await expect(more).toHaveAttribute('open', '')
  await expect(more.locator('p')).toBeVisible()
  expect(await page.locator('main').evaluate(main => main.scrollWidth <= main.clientWidth)).toBe(true)
  for (const summary of await explanations.locator('summary').all()) {
    expect((await summary.boundingBox())!.height).toBeGreaterThanOrEqual(44)
  }
})

test('library actions stay out of the way in a short landscape viewport', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/files')
  await page.getByLabel('Name', { exact: true }).click()
  await page.getByLabel('Name', { exact: true }).fill('Films archive')
  await expect(page.locator('[data-library-actions]')).toHaveCSS('position', 'static')
  await expect(page.getByLabel('Name', { exact: true })).toBeInViewport()
})

test('new library drafts keep required folder validation across pages, and can read their own summary first', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/new')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Which files')
  await page.getByLabel('Name', { exact: true }).fill('New films')
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'New films', exact: true }).click()
  await expect(page).toHaveURL(/\/new\/overview$/)
  await expect(page.locator('[data-summary-line="schedule"]')).toContainText('You queue files yourself.')
  await pageTab(page, 'Video').click()
  await expect(page.getByRole('group', { name: 'Preset' }).getByRole('button', { name: /Balanced/ })).toBeVisible()
  // The folder remains required; changing pages must not bypass validation.
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
})

test('saving prevents duplicate requests and edits that would be lost while awaiting the server', async ({ page }) => {
  await mockLibraries(page)
  let finishSave: () => void = () => {}
  const held = new Promise<void>(resolve => { finishSave = resolve })
  await page.route('**/api/libraries/1', async route => {
    await held
    return json(route, { ...library, ...route.request().postDataJSON() })
  })
  await page.goto('/#/libraries/1/configure/files')
  await page.getByLabel('Name', { exact: true }).fill('Saved films')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled()
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled()
  await pageTab(page, 'Video').click()
  await expect(page.getByRole('group', { name: 'Preset' }).getByRole('button', { name: /Balanced/ })).toBeDisabled()
  finishSave()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeVisible()
})

test('a page that stops applying after a media type change shows one that does', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#lib-codec')).toBeVisible()
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await page.goBack()
  await expect(page.locator('#lib-codec')).toHaveCount(0)
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Images')
})

for (const colorScheme of ['light', 'dark'] as const) {
  test(`every page keeps the shared width and themed surfaces in ${colorScheme} mode`, async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 })
    await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' })
    await mockLibraries(page, { ...library, mediaType: 'Other' })
    let referenceWidth = 0
    for (const name of ['', '/files', '/video', '/audio', '/images', '/checks', '/schedule']) {
      await page.goto('/#/libraries/1/configure' + name)
      const workflow = page.locator('[data-library-workflow]')
      await expect(workflow).toBeVisible()
      const width = (await workflow.boundingBox())!.width
      if (!referenceWidth) referenceWidth = width
      expect(width, name).toBe(referenceWidth)
      const availableWidth = await page.locator('main').evaluate(main => {
        const style = getComputedStyle(main)
        return main.clientWidth - Number.parseFloat(style.paddingLeft) - Number.parseFloat(style.paddingRight)
      })
      expect(width).toBe(availableWidth)
      const card = workflow.locator('[data-config-section]').first()
      await page.mouse.move(0, 0)
      const surface = await card.evaluate(element => ({
        fill: getComputedStyle(element).backgroundColor,
        panel: getComputedStyle(element).getPropertyValue('--panel').trim(),
        shadow: getComputedStyle(element).boxShadow,
      }))
      const panelRgb = surface.panel.slice(1).match(/.{2}/g)!.map(channel => parseInt(channel, 16)).join(', ')
      expect(surface.fill).toBe(`rgb(${panelRgb})`)
      expect(surface.shadow).not.toBe('none')
      await card.hover()
      await expect.poll(() => card.evaluate(element => getComputedStyle(element).boxShadow)).not.toBe(surface.shadow)
      for (const input of await workflow.locator('.input').all()) {
        if (await input.isVisible()) expect((await input.boundingBox())!.height).toBeGreaterThanOrEqual(44)
      }
      expect(await page.locator('main').evaluate(main => main.scrollWidth <= main.clientWidth), name).toBe(true)
    }
  })
}

test('library tab headings describe the displayed content and restore the page heading', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/files')
  await page.getByRole('button', { name: /Candidates \(/ }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Candidates')
  await expect(page.getByRole('navigation', { name: 'Library settings pages' })).toHaveCount(0)
  await page.getByRole('button', { name: /Excluded \(/ }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Excluded')
  await page.getByRole('navigation', { name: 'Configure' }).getByRole('button', { name: 'Settings', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Which files')
})

test('the page list fills the page being read and nothing else', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/checks')
  const current = pageTab(page, 'Checks')
  await expect(current).toHaveAttribute('aria-current', 'page')
  const fill = await page.evaluate(() => {
    const probe = document.createElement('span')
    probe.className = 'bg-accent-fill'
    document.body.append(probe)
    const value = getComputedStyle(probe).backgroundColor
    probe.remove()
    return value
  })
  await expect(current).toHaveCSS('background-color', fill)
  await expect(pageTab(page, 'Video')).not.toHaveCSS('background-color', fill)
  await pageTab(page, 'Video').click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Video')
  await expect(pageTab(page, 'Video')).toHaveAttribute('aria-current', 'page')
  await expect(pageTab(page, 'Checks')).not.toHaveAttribute('aria-current', 'page')
})

test('image quality is one choice of four, needs a score before it can be required, and keeps SSIM', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Photo', minimumImageSsim: 0.97 })
  await page.goto('/#/libraries/1/configure/images')
  const mode = page.locator('#lib-image-quality-mode')
  await expect(mode).toHaveValue('off')
  await expect(mode.locator('option')).toHaveText(['Off', 'Report only', 'Require a limit', 'Require a limit, and choose by measurement'])
  await expect(page.locator('[data-quality-limit="image"]')).toHaveCount(0)
  await mode.selectOption('require')
  await expect(page.getByText('Choose a target to finish setting this up.', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  const score = page.getByLabel('Minimum SSIMULACRA2 score', { exact: true })
  await expect(score.locator('option')).toHaveText(['Choose a limit…', 'Very high · 90', 'High · 70', 'Medium · 50', 'Low · 30', 'Custom'])
  await score.selectOption('custom')
  await page.getByRole('spinbutton', { name: 'Custom', exact: true }).fill('80')
  await pageTab(page, 'Checks').click()
  await expect(row(page, 'Image quality').locator('[data-check-value]')).toHaveText('Require a limit')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ imagePerceptualReportingEnabled: true, imagePerceptualGateEnabled: true,
    adaptiveImageQuality: false, minimumImagePerceptualScore: 80, minimumImageSsim: 0.97 })
})

test('choosing an image setting by measurement requires its score and is refused while images are resized', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Photo' })
  await page.goto('/#/libraries/1/configure/images')
  await page.locator('#lib-image-quality-mode').selectOption('target')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await page.getByLabel('Minimum SSIMULACRA2 score', { exact: true }).selectOption('90')
  await expect(page.getByText(/Your SSIM floor of 0.95 also applies/)).toBeVisible()
  await page.locator('#lib-image-downscale').selectOption('1080p')
  await expect(page.locator('#lib-verification-error')).toHaveText('Image quality first requires no resizing.')
  await pageTab(page, 'Checks').click()
  await page.getByRole('alert').getByRole('button', { name: 'Image quality first requires no resizing.' }).click()
  await expect(page).toHaveURL(/\/configure\/images$/)
  await page.locator('#lib-image-downscale').selectOption('none')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ adaptiveImageQuality: true, adaptiveAudioQuality: false,
    imagePerceptualReportingEnabled: true, imagePerceptualGateEnabled: true, minimumImagePerceptualScore: 90 })
})

test('audio quality reporting is opt-in and persists across pages', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-audio-quality-mode')
  await expect(mode).toHaveValue('off')
  await mode.selectOption('report')
  await expect(page.locator('[data-quality-limit="audio"]')).toHaveCount(0)
  await pageTab(page, 'Checks').click()
  await expect(row(page, 'Audio quality').locator('[data-check-value]')).toHaveText('Report only')
  await row(page, 'Audio quality').getByRole('button', { name: 'Audio', exact: true }).click()
  await expect(mode).toHaveValue('report')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioQualityReportingEnabled: true, audioQualityGateEnabled: false, adaptiveAudioQuality: false })
})

test('video libraries do not offer standalone audio quality', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/audio')
  await expect(page.locator('#lib-audio-quality-mode')).toHaveCount(0)
  await expect(page.locator('#lib-soundtrack-quality-mode')).toBeVisible()
  await pageTab(page, 'Checks').click()
  await expect(row(page, 'Audio quality')).toHaveCount(0)
  await expect(row(page, 'Soundtrack quality').locator('[data-check-value]')).toHaveText('Off')
})

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`requiring audio quality needs an explicit limit, from the stops or a custom number, at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1100 })
      await page.addInitScript(selected => localStorage.setItem('optimisarr.theme', selected), theme)
      await mockLibraries(page, { ...library, mediaType: 'Music' })
      await page.goto('/#/libraries/1/configure/audio')
      await page.locator('#lib-audio-quality-mode').selectOption('require')
      const control = page.locator('[data-quality-limit="audio"]')
      const stop = control.getByLabel('Maximum audio difference', { exact: true })
      const custom = control.getByRole('spinbutton', { name: 'Custom', exact: true })
      await expect(stop).toHaveValue('')
      await expect(stop.locator('option')).toHaveText(['Choose a limit…', '0 · no measured difference', '0.0001', '0.001', '0.01', '0.1', 'Custom'])
      await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
      await stop.selectOption('0.01')
      await expect(custom).toHaveCount(0)
      await stop.selectOption('custom')
      await expect(custom).toHaveValue('0.01')
      await custom.fill('0.005')
      await pageTab(page, 'Checks').click()
      await pageTab(page, 'Audio').click()
      await expect(custom).toHaveValue('0.005')
      await control.scrollIntoViewIfNeeded()
      expect(await control.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await test.info().attach('Audio difference control', { body: await control.screenshot(), contentType: 'image/png' })
      await stop.selectOption('0')
      const saved = savedRequest(page)
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ audioQualityGateEnabled: true, audioQualityReportingEnabled: true, maximumAudioQualityDistance: 0 })
    })
  }
}

test('a library saved with the audio check required but not reported keeps exactly that until the choice is changed', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioQualityGateEnabled: true, audioQualityReportingEnabled: false, maximumAudioQualityDistance: 0.005 })
  await page.goto('/#/libraries/1/configure/audio')
  await expect(page.locator('#lib-audio-quality-mode')).toHaveValue('require')
  await expect(page.getByLabel('Maximum audio difference', { exact: true })).toHaveValue('custom')
  await page.getByRole('checkbox', { name: 'Re-encode lossy audio too', exact: true }).check()
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioQualityGateEnabled: true, audioQualityReportingEnabled: false, maximumAudioQualityDistance: 0.005 })
})

for (const [codec, budgets] of [['aac', [96, 128, 192, 256]], ['opus', [96, 128, 160, 192]], ['mp3', [128, 192, 256, 320]]] as const) {
  for (const [index, label] of ['Space saver', 'Balanced', 'High', 'Very high'].entries()) {
    test(`${codec} ${label} saves its encoding budget with the audio check off`, async ({ page }) => {
      await mockLibraries(page, { ...library, mediaType: 'Music', audioTargetCodec: codec })
      await page.goto('/#/libraries/1/configure/audio')
      const control = page.locator('[data-audio-encoding-preset]')
      await control.getByRole('button', { name: label, exact: true }).click()
      await expect(control).toContainText(`${budgets[index]} kbps`)
      const saved = savedRequest(page)
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: budgets[index], audioQualityGateEnabled: false, maximumAudioQualityDistance: null })
    })
  }
}

test('Custom audio bitrate and Default remain distinct across formats, with the number on the same page', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/audio')
  const control = page.locator('[data-audio-encoding-preset]')
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await control.getByRole('button', { name: 'Custom', exact: true }).click()
  await expect(page).toHaveURL(/configure\/audio$/)
  await expect(page.locator('#lib-audio-bitrate')).toHaveValue('128')
  await page.locator('#lib-audio-bitrate').fill('173')
  await page.getByLabel('Audio codec', { exact: true }).selectOption('mp3')
  await expect(page.locator('#lib-audio-bitrate')).toHaveValue('173')
  await expect(control.getByRole('button', { name: 'Custom', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await control.getByRole('button', { name: 'Default', exact: true }).click()
  await expect(page.locator('#lib-audio-bitrate')).toHaveCount(0)
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioTargetCodec: 'mp3', audioBitrateKbps: null, maximumAudioQualityDistance: null })
})

for (const mediaType of ['Music', 'Other']) {
  test(`${mediaType} Default shows the server profile's standalone budget and saves no override`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType, ruleProfile: 'ScottsSettings' })
    await page.goto('/#/libraries/1/configure/audio')
    const control = page.locator('[data-audio-encoding-preset]').last()
    await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
    await expect(control).toContainText('AAC · 96 kbps')
    await control.getByRole('button', { name: 'High', exact: true }).click()
    await control.getByRole('button', { name: 'Default', exact: true }).click()
    // The codec already shows the preset's own, so choosing it again stores no choice either.
    await page.locator('#lib-audio-codec').selectOption('mp3')
    await page.locator('#lib-audio-codec').selectOption('aac')
    await page.getByRole('checkbox', { name: 'Re-encode lossy audio too', exact: true }).check()
    const saved = savedRequest(page)
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: null, audioTargetCodec: null })
  })
}

test('choosing a video preset shows its soundtrack baseline as Default', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const presets = page.getByRole('group', { name: 'Preset' })
  await presets.getByRole('button', { name: /Scott's/ }).click()
  await pageTab(page, 'Audio & subtitles').click()
  const control = page.locator('[data-audio-encoding-preset]')
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(control).toContainText('AAC · 96 kbps')
  await pageTab(page, 'Video').click()
  await presets.getByRole('button', { name: /Balanced/ }).click()
  await pageTab(page, 'Audio & subtitles').click()
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(control).toContainText('AAC · 160 kbps')
})

for (const bitrate of [null, 128]) {
  test(`mixed library ${bitrate == null ? 'inherited' : 'saved'} audio budget stays clear after a preset change`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType: 'Other', audioBitrateKbps: bitrate })
    await page.goto('/#/libraries/1/configure/audio')
    const control = page.locator('[data-audio-encoding-preset]').last()
    const note = control.getByText('Saved bitrate. Choose Default to follow the profile if it changes.', { exact: true })
    if (bitrate == null) await expect(note).toHaveCount(0)
    else await expect(note).toBeVisible()
    await pageTab(page, 'Video').click()
    await page.getByRole('group', { name: 'Preset' }).getByRole('button', { name: /Scott's/ }).click()
    await pageTab(page, 'Audio & subtitles').click()
    await expect(control).toContainText(`AAC · ${bitrate ?? 96} kbps`)
    await control.getByRole('button', { name: 'Default', exact: true }).click()
    await expect(control).toContainText('AAC · 96 kbps')
    await expect(note).toHaveCount(0)
  })
}

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`audio encoding presets fit at ${width}px in ${theme} and work by keyboard`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1100 })
      await page.addInitScript(selected => localStorage.setItem('optimisarr.theme', selected), theme)
      await mockLibraries(page, { ...library, mediaType: 'Music' })
      await page.goto('/#/libraries/1/configure/audio')
      const control = page.locator('[data-audio-encoding-preset]')
      await control.getByRole('button', { name: 'Balanced', exact: true }).click()
      const slider = control.getByRole('slider', { name: 'Audio encoding quality', exact: true })
      await slider.focus()
      await slider.press('ArrowRight')
      await expect(slider).toHaveAttribute('aria-valuetext', 'High · AAC · 192 kbps')
      expect(await control.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      for (const button of await control.getByRole('button').all()) {
        const bounds = (await button.boundingBox())!
        expect(bounds.height).toBeGreaterThanOrEqual(44)
        expect(bounds.width).toBeGreaterThanOrEqual(44)
      }
      await test.info().attach('Audio encoding presets', { body: await control.screenshot(), contentType: 'image/png' })
    })
  }
}

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`requiring soundtrack quality keeps its explicit limit and leaves standalone audio alone at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
      await mockLibraries(page)
      await page.goto('/#/libraries/1/configure/audio')
      const mode = page.locator('#lib-soundtrack-quality-mode')
      await expect(mode).toHaveValue('off')
      await mode.selectOption('require')
      await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
      const stop = page.getByLabel('Maximum soundtrack difference', { exact: true })
      await stop.selectOption('custom')
      await page.locator('#lib-soundtrack-quality-limit').fill('0.005')
      await pageTab(page, 'Checks').click()
      await expect(row(page, 'Soundtrack quality').locator('[data-check-value]')).toHaveText('Require a limit')
      const saved = savedRequest(page)
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ soundtrackQualityGateEnabled: true,
        maximumSoundtrackQualityDistance: 0.005, soundtrackQualityReportingEnabled: true, audioQualityGateEnabled: false })
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    })
  }
}

test('switching soundtrack quality off discards an invalid unsaved custom limit', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-soundtrack-quality-mode')
  await mode.selectOption('require')
  await page.getByLabel('Maximum soundtrack difference', { exact: true }).selectOption('custom')
  await page.locator('#lib-soundtrack-quality-limit').fill('2')
  await expect(page.locator('#lib-verification-error')).toHaveText('Enter a maximum soundtrack difference between 0 and 1.')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await mode.selectOption('off')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ soundtrackQualityGateEnabled: false, maximumSoundtrackQualityDistance: null })
})

test('choosing soundtrack bitrates by measurement keeps the limit required and saves for re-encoded audio', async ({ page }) => {
  await mockLibraries(page, { ...library, videoAudioCodec: 'aac', videoAudioBitrateKbps: 160,
    soundtrackQualityGateEnabled: true, maximumSoundtrackQualityDistance: 0.005 })
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-soundtrack-quality-mode')
  await expect(mode).toHaveValue('require')
  await expect(mode.locator('option[value="target"]')).toBeEnabled()
  await mode.selectOption('target')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ adaptiveSoundtrackQuality: true, soundtrackQualityGateEnabled: true,
    maximumSoundtrackQualityDistance: 0.005 })
})

test('choosing soundtrack bitrates by measurement names the codec a preset converts to, because the server needs it named', async ({ page }) => {
  await mockLibraries(page, { ...library, videoAudioCodec: null })
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-soundtrack-quality-mode')
  await expect(mode.locator('option[value="target"]')).toBeEnabled()
  await mode.selectOption('target')
  await page.getByLabel('Maximum soundtrack difference', { exact: true }).selectOption('0.001')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ adaptiveSoundtrackQuality: true, soundtrackQualityGateEnabled: true,
    maximumSoundtrackQualityDistance: 0.001, videoAudioCodec: 'aac' })
})

test('choosing soundtrack bitrates by measurement is unavailable, with the reason, while audio is left untouched', async ({ page }) => {
  await mockLibraries(page, { ...library, videoAudioCodec: 'copy', soundtrackQualityGateEnabled: true, maximumSoundtrackQualityDistance: 0.005 })
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-soundtrack-quality-mode')
  await expect(mode.locator('option[value="target"]')).toBeDisabled()
  await expect(mode.locator('option[value="require"]')).toBeEnabled()
  await expect(row(page, 'Soundtrack quality').locator('[data-setting-reason]')).toHaveText(
    'Choosing by measurement needs the audio to be converted to AAC, Opus or MP3. This library leaves audio untouched.')
})

test('choosing an audio bitrate by measurement keeps its limit across pages, and stepping back keeps the check', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/audio')
  const mode = page.locator('#lib-audio-quality-mode')
  await mode.selectOption('target')
  await page.getByLabel('Maximum audio difference', { exact: true }).selectOption('0.01')
  await pageTab(page, 'Checks').click()
  await expect(row(page, 'Audio quality').locator('[data-check-value]')).toHaveText('Require a limit, and choose by measurement')
  await pageTab(page, 'Audio').click()
  await expect(page.getByLabel('Maximum audio difference', { exact: true })).toHaveValue('0.01')
  let saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ adaptiveAudioQuality: true, adaptiveImageQuality: false,
    audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.01 })
  await mode.selectOption('require')
  saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ adaptiveAudioQuality: false,
    audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.01 })
})

test('library settings stay calm: labelled media type, codec names in capitals, no hover bubbles', async ({ page }) => {
  await mockLibraries(page, { ...library, videoAudioCodec: 'aac', videoAudioBitrateKbps: 128 })
  await page.goto('/#/libraries/1/configure/audio')
  await expect(page.locator('header .badge').first()).toHaveText('Media type: TV')
  await expect(page.locator('option', { hasText: 'Re-encode to AAC' })).toHaveCount(1)
  await expect(page.getByText('Re-encode to aac', { exact: true })).toHaveCount(0)
  // General guidance opens under "More about this"; what the bitrate means per channel pair stays visible.
  await expect(page.locator('[data-audio-encoding-preset] p.text-ink-3', { hasText: 'mono/stereo baseline' })).toBeVisible()
  const bitrate = row(page, 'Audio bitrate')
  await expect(bitrate.getByText(/Starting points for the chosen codec/)).toBeHidden()
  await bitrate.locator('summary').click()
  await expect(bitrate.getByText(/Starting points for the chosen codec/)).toBeVisible()
})

test('a setting the encoder ignores stays in view, disabled, with the reason and where to change it', async ({ page }) => {
  await mockLibraries(page, library, false, { settings: { encoderMode: 'IntelQsv' } })
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#library-encoder')).toContainText('This server encodes with Intel Quick Sync. Settings it ignores are shown, with the reason.')
  for (const [label, control] of [['Maximum bitrate (kbps)', '#lib-maxbitrate'], ['Content tune', '#lib-tune'], ['Minimum bitrate (kbps)', '#lib-minbitrate']]) {
    await expect(page.locator(control)).toBeDisabled()
    await expect(row(page, label).locator('[data-setting-reason]')).toContainText('Intel Quick Sync ignores this.')
  }
  await expect(page.getByRole('checkbox', { name: 'Stronger adaptive quantisation', exact: true })).toBeDisabled()
  await expect(page.locator('#lib-preset')).toBeEnabled()
  page.once('dialog', dialog => dialog.accept())
  await row(page, 'Content tune').getByRole('button', { name: 'Settings → Encoding', exact: true }).click()
  await expect(page).toHaveURL(/#\/settings\/encoding$/)
})

test('with the encoder picked per job every tuning setting can be set, and the floor waits for a cap', async ({ page }) => {
  await mockLibraries(page, library, false, { settings: { encoderMode: 'Auto' } })
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#library-encoder')).toContainText('The encoder is picked for each job. Not every encoder uses every setting here.')
  await expect(page.locator('#lib-tune')).toBeEnabled()
  await expect(page.locator('#lib-minbitrate')).toBeDisabled()
  await expect(row(page, 'Minimum bitrate (kbps)').locator('[data-setting-reason]')).toHaveText('Set a maximum bitrate first. A floor only works below a cap.')
  await page.locator('#lib-maxbitrate').fill('8000')
  await expect(page.locator('#lib-minbitrate')).toBeEnabled()
  await page.locator('#lib-minbitrate').fill('1500')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ maxBitrateKbps: 8000, minBitrateKbps: 1500 })
})

test('the schedule is any time or between two times, and stays in view with the reason while automation is off', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/schedule')
  const when = page.getByLabel('When', { exact: true })
  await expect(when).toBeDisabled()
  await expect(when).toHaveValue('any')
  await expect(row(page, 'When').locator('[data-setting-reason]')).toHaveText('Only used when “Optimise automatically” is on. Files you queue yourself start at any time.')
  await page.getByRole('checkbox', { name: 'Optimise automatically', exact: true }).check()
  await expect(when).toBeEnabled()
  await expect(page.locator('#lib-auto-start')).toHaveCount(0)
  await when.selectOption('between')
  await expect(page.locator('#lib-auto-start')).toHaveValue('01:00')
  await expect(page.locator('#lib-auto-end')).toHaveValue('06:00')
  await page.locator('#lib-auto-end').fill('07:30')
  let saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ autoEnqueueEnabled: true, autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '07:30' })
  // Any time is saved the way it always was: two equal times.
  await when.selectOption('any')
  saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '00:00' })
})

test('AI upscaling stays in view while the feature is off, disabled with where to turn it on, and keeps a saved choice', async ({ page }) => {
  const optedIn = { ...library, superResolutionEnabled: true }
  await mockLibraries(page, optedIn, false)
  await page.goto('/#/libraries/1/configure/video')
  const upscale = row(page, 'Super-resolution')
  await expect(upscale.getByRole('checkbox', { name: 'Super-resolution', exact: true })).toBeDisabled()
  await expect(upscale.locator('[data-setting-reason]')).toContainText('AI upscaling is switched off on this server.')
  await expect(upscale.getByRole('button', { name: 'Settings → AI super-resolution', exact: true })).toBeVisible()
  await expect(page.locator('#library-sr-model')).toHaveCount(0)
  // Saving while it cannot be used keeps the library's earlier choice for when the feature returns.
  await pageTab(page, 'Which files').click()
  await page.getByLabel('Name', { exact: true }).fill('Anime')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ superResolutionEnabled: true })
})

test('with the feature on, a library has its own AI super-resolution section', async ({ page }) => {
  await mockLibraries(page, library, true)
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.getByRole('heading', { name: 'AI super-resolution (experimental)' })).toBeVisible()
  await page.getByRole('checkbox', { name: 'Super-resolution', exact: true }).check()
  await expect(row(page, 'Super-resolution').locator('[data-setting-tag]')).toHaveText('Changes the picture')
  // Opting in offers how upscaled results are checked, recommending the broken-frame check, and
  // says plainly that every upscaled result waits for review.
  const check = page.locator('#library-sr-quality')
  await expect(check).toHaveValue('BrokenFramesOnly')
  await expect(page.getByText(/Upscaled results always wait for you to review them/)).toBeVisible()
  await check.selectOption('Off')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ superResolutionEnabled: true, superResolutionQualityCheck: 'Off' })
})

test('a library chooses the model that suits its content, and only a model that offers a scale asks for one', async ({ page }) => {
  await mockLibraries(page, { ...library, superResolutionEnabled: true }, true)
  await page.goto('/#/libraries/1/configure/video')
  const model = page.locator('#library-sr-model')
  const scale = page.locator('#library-sr-scale')
  await expect(model).toHaveValue('')
  await expect(scale).toHaveCount(0)

  await model.selectOption('realesr-animevideov3')
  await expect(scale).toHaveValue('2')
  await scale.selectOption('3')

  await model.selectOption('realesrgan-x4plus')
  await expect(scale).toHaveCount(0)
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({
    superResolutionEngine: 'realesrgan', superResolutionModel: 'realesrgan-x4plus', superResolutionScale: 4,
  })
})

test('going back to Real-CUGAN clears the Real-ESRGAN choice', async ({ page }) => {
  await mockLibraries(page, {
    ...library, superResolutionEnabled: true,
    superResolutionEngine: 'realesrgan', superResolutionModel: 'realesr-animevideov3', superResolutionScale: 3,
  }, true)
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#library-sr-scale')).toHaveValue('3')

  await page.locator('#library-sr-model').selectOption('')

  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({
    superResolutionEnabled: true, superResolutionEngine: 'realcugan', superResolutionModel: null, superResolutionScale: null,
  })
})

test('a library can choose how strongly to reduce noise, is off until it does, and is tagged once it changes the picture', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const level = page.getByLabel('Reduce noise', { exact: true })
  await expect(level).toHaveValue('Off')
  await expect(row(page, 'Reduce noise')).toContainText('Cleans grain and tape noise. Strong can soften faces.')
  await expect(row(page, 'Reduce noise').locator('[data-setting-tag]')).toHaveCount(0)

  await level.selectOption('Light')
  await expect(row(page, 'Reduce noise').locator('[data-setting-tag]')).toHaveText('Changes the picture')

  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ noiseReduction: 'Light' })
})

test('interlaced and Dolby Vision files are left alone until a library asks, in plain choices', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/video')
  const interlaced = page.getByLabel('Interlaced video', { exact: true })
  const dolbyVision = page.getByLabel('Dolby Vision files', { exact: true })
  await expect(interlaced).toHaveValue('leave')
  await expect(interlaced.locator('option')).toHaveText(['Leave these files alone', 'Deinterlace, then encode'])
  await expect(dolbyVision).toHaveValue('skip')
  await expect(dolbyVision.locator('option')).toHaveText(['Skip them', 'Re-encode and lose Dolby Vision'])

  await interlaced.selectOption('deinterlace')
  await dolbyVision.selectOption('reencode')
  await expect(row(page, 'Dolby Vision files').locator('[data-setting-tag]')).toHaveText('Changes the picture')

  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ deinterlaceVideo: true, optimiseDolbyVision: true })
})

test('a new library keeps each file container, and an existing one shows what its preset uses', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/new/video')
  await expect(page.locator('#lib-container')).toHaveValue('source')
  await expect(page.locator('#lib-container option:checked')).toHaveText('Same as source (keeps file names)')
  await expect(row(page, 'Container').locator('[data-setting-tag]')).toHaveText('Renames some files')

  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#lib-container')).toHaveValue('mp4')
  await expect(row(page, 'Container').locator('[data-setting-tag]')).toHaveText('Renames files')
  await expect(row(page, 'Container').locator('[data-setting-origin="preset"]')).toHaveText('From Balanced')
  await expect(page.locator('#lib-container option')).toHaveText(['Same as source (keeps file names)', 'MP4', 'MKV'])
})

test('choosing a preset keeps same as source, because keeping file names is not a preset override', async ({ page }) => {
  await mockLibraries(page, { ...library, targetContainer: 'source' })
  await page.goto('/#/libraries/1/configure/video')
  await page.getByRole('group', { name: 'Preset' }).getByRole('button', { name: /Compatibility/ }).click()
  await expect(page.locator('[data-preset-state]')).toHaveText('Compatibility preset')

  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ ruleProfile: 'CompatibilityH264', targetContainer: 'source' })
})

test('a refused save folder holds Save back on every page of the editor and the callout names the problem', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/schedule')
  await filesChoice(page, 'elsewhere').check()
  await page.getByRole('button', { name: 'Browse', exact: true }).click()
  const picker = page.getByRole('dialog', { name: 'Choose a folder' })
  await picker.getByRole('button', { name: 'media', exact: true }).click()
  await picker.getByRole('button', { name: 'films', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()
  await expect(page.locator('[data-target-folder-state="insideLibrary"]')).toBeVisible()

  // The folder control is no longer on screen, but the refusal still stands.
  await page.goto('/#/libraries/1/configure/checks')

  const problem = page.getByRole('alert').getByRole('button', { name: 'The folder for optimised files can’t be used', exact: true })
  await expect(problem).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await problem.click()
  await expect(page).toHaveURL(/configure\/schedule$/)
  await expect(page.locator('[data-target-folder-state="insideLibrary"]')).toBeVisible()
})

test('a saved library whose save folder can no longer be used is flagged on its card', async ({ page }) => {
  await mockLibraries(page, { ...library, moveOnComplete: true, targetFolder: '/media/films/optimised' })
  await page.goto('/#/libraries')
  const card = page.locator('[data-library-card="1"]')

  await expect(card.locator('[data-save-folder-refused]')).toHaveText('Save folder can’t be used')
  await expect(card.locator('[data-save-folder-hint]')).toContainText('Files from this library are not encoded until the folder is changed.')
})

test('a library saving to a usable folder is not flagged', async ({ page }) => {
  await mockLibraries(page, { ...library, moveOnComplete: true, targetFolder: '/optimised' })
  await page.goto('/#/libraries')

  await expect(page.locator('[data-library-card="1"]')).toBeVisible()
  await expect(page.locator('[data-save-folder-refused]')).toHaveCount(0)
})

test('the overwrite switch says what On does as well as Off', async ({ page }) => {
  await mockLibraries(page, { ...library, moveOnComplete: true, targetFolder: '/optimised' })
  await page.goto('/#/libraries/1/configure/schedule')

  await expect(page.locator('main')).toContainText('On: the file already there is replaced for good. It does not go to Quarantine and cannot be put back.')
})

const remuxLibrary = { ...library, mediaType: 'TV', ruleProfile: 'RemuxCleanup', targetContainer: 'source', hdrHandling: null, maxHeight: null }
const remuxMode = (page: Page) => page.getByRole('radio', { name: /Just clean up containers/ })

test('a clean-up library keeps the two skip rules the server still applies, and the overview says them', async ({ page }) => {
  await mockLibraries(page, remuxLibrary)
  await page.goto('/#/libraries/1/configure/video')

  await expect(remuxMode(page)).toBeChecked()
  const skips = page.locator('#library-remux-skips')
  await expect(skips).toContainText('A clean-up without re-encoding still skips these.')
  await expect(skips.locator('#lib-hdr option')).toHaveText(['Exclude (skip HDR)', 'Preserve HDR'])
  await skips.locator('#lib-hdr').selectOption('Exclude')
  await skips.locator('#lib-maxheight').selectOption('1080')
  await pageTab(page, 'Overview').click()
  await expect(page.locator('[data-summary-line="files"]')).toContainText('Skips HDR files and video taller than 1080p.')

  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ ruleProfile: 'RemuxCleanup', hdrHandling: 'Exclude', maxHeight: 1080, targetVideoCodec: null })
})

test('choosing a clean-up clears a codec that would make it re-encode', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'TV', targetContainer: 'source' })
  await page.goto('/#/libraries/1/configure/video')
  await page.locator('#lib-codec').selectOption('av1')

  await remuxMode(page).check()
  await pageTab(page, 'Overview').click()

  await expect(page.locator('[data-summary-line="video"]')).toContainText('Cleans up each file\'s container without re-encoding.')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ ruleProfile: 'RemuxCleanup', targetVideoCodec: null })
})

test('a saved clean-up library with a codec left on it is shown as re-encoding, and choosing clean-up again clears it', async ({ page }) => {
  await mockLibraries(page, { ...remuxLibrary, targetVideoCodec: 'av1' })
  await page.goto('/#/libraries/1/configure')
  const video = page.locator('[data-summary-line="video"]')

  await expect(video).toContainText('a saved video codec (AV1) makes it re-encode video')
  await expect(video.locator('[data-summary-tag="warn"]').first()).toHaveText('Re-encodes video')
  await pageTab(page, 'Video').click()
  await remuxMode(page).click()
  await pageTab(page, 'Overview').click()

  await expect(video).not.toContainText('re-encode video')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ targetVideoCodec: null })
})

test('switching the picture check off and on again keeps custom floors and sampling', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'TV', vmafQualityGateEnabled: true, videoQualityStrategy: 'Fixed',
    minVmafHarmonicMean: 91, minVmafMin: 77, minVmafCatastrophicMin: 44, clipVmafEnabled: false, vmafFrameSubsample: 3 })
  await page.goto('/#/libraries/1/configure/video')
  const mode = page.locator('#lib-quality-mode')
  await expect(mode).toHaveValue('fixed-checked')

  await mode.selectOption('fixed-unchecked')
  await mode.selectOption('fixed-checked')

  await expect(page.locator('#lib-vmaf-harmonic')).toHaveValue('91')
  await expect(page.locator('#lib-vmaf-fifth')).toHaveValue('77')
  await expect(page.locator('#lib-vmaf-sampling')).toHaveValue('full-3')
  await mode.selectOption('fixed-unchecked')
  await mode.selectOption('target')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoQualityStrategy: 'AdaptiveVmaf', vmafQualityGateEnabled: true,
    minVmafHarmonicMean: 91, minVmafMin: 77, minVmafCatastrophicMin: 44, clipVmafEnabled: false, vmafFrameSubsample: 3 })
})

test('clearing the bitrate cap clears the floor that depended on it', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'TV', maxBitrateKbps: 8000, minBitrateKbps: 2000 })
  await page.goto('/#/libraries/1/configure/video')
  await expect(page.locator('#lib-minbitrate')).toHaveValue('2000')

  await page.locator('#lib-maxbitrate').fill('')

  await expect(page.locator('#lib-minbitrate')).toHaveValue('')
  await expect(page.locator('#lib-minbitrate')).toBeDisabled()
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ maxBitrateKbps: null, minBitrateKbps: null })
})

test('typing a start time equal to the end time keeps both time inputs', async ({ page }) => {
  await mockLibraries(page, { ...library, autoEnqueueEnabled: true, autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '06:00' })
  await page.goto('/#/libraries/1/configure/schedule')
  await expect(page.locator('#lib-window')).toHaveValue('between')

  await page.locator('#lib-auto-start').fill('06:00')

  await expect(page.locator('#lib-window')).toHaveValue('between')
  await expect(page.locator('#lib-auto-start')).toHaveValue('06:00')
  await expect(page.locator('#lib-auto-end')).toBeVisible()
  await page.locator('#lib-auto-start').fill('02:00')
  await expect(page.locator('#lib-auto-start')).toHaveValue('02:00')
})

test('a library saved with equal times opens as any time', async ({ page }) => {
  await mockLibraries(page, { ...library, autoEnqueueEnabled: true, autoEnqueueWindowStart: '03:00', autoEnqueueWindowEnd: '03:00' })
  await page.goto('/#/libraries/1/configure/schedule')

  await expect(page.locator('#lib-window')).toHaveValue('any')
  await expect(page.locator('#lib-auto-start')).toHaveCount(0)
})

test('a check that lives on another page links to its section there, not to the top of the page', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 600 })
  await mockLibraries(page, { ...library, mediaType: 'TV', vmafQualityGateEnabled: true, minVmafHarmonicMean: 93, minVmafMin: 80, minVmafCatastrophicMin: 50 })
  await page.goto('/#/libraries/1/configure/checks')

  await row(page, 'Picture quality').getByRole('button', { name: 'Video', exact: true }).click()

  await expect(page).toHaveURL(/configure\/video$/)
  await expect(page.locator('#library-quality')).toBeInViewport()
  await expect.poll(() => page.locator('main').evaluate((main) => main.scrollTop)).toBeGreaterThan(100)
})

test('a mixed library that only cleans up video still offers the surround choice for its audio files', async ({ page }) => {
  await mockLibraries(page, { ...remuxLibrary, mediaType: 'Other' })
  await page.goto('/#/libraries/1/configure/audio')

  await expect(page.locator('#library-music #lib-surround')).toBeVisible()
  await page.locator('#lib-surround').selectOption('downmix')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ downmixToStereo: true })
})

test('when the server settings cannot be read, the overview warns that originals may be deleted', async ({ page }) => {
  await mockLibraries(page)
  await page.route('**/api/settings', (route) => route.fulfill({ status: 500, json: { error: 'unavailable' } }))
  await page.goto('/#/libraries/1/configure')
  const schedule = page.locator('[data-summary-line="schedule"]')

  await expect(schedule).toContainText('Optimisarr couldn’t check how long originals are kept. They may be deleted after a number of days.')
  await expect(schedule.locator('[data-summary-tag="warn"]')).toHaveText('May delete originals')
})

test('upscaling switched off for the whole server is not claimed in the overview', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'TV', superResolutionEnabled: true } as typeof library, false)
  await page.goto('/#/libraries/1/configure')

  await expect(page.locator('[data-summary-line="video"]')).not.toContainText('Upscales with AI')
})

test('music and photo libraries are tagged as renaming the files they convert', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure')

  await expect(page.locator('[data-summary-line="audio"]')).toContainText('A converted file gets a new extension, so its name changes.')
  await expect(page.locator('[data-summary-line="audio"] [data-summary-tag="warn"]')).toHaveText('Renames files')
})

test('opening every page of a customised library and saving sends back every setting unchanged', async ({ page }) => {
  const customised = {
    ...library, mediaType: 'TV', priority: 1, minFileSizeBytes: 209_715_200, maxHeight: 1080,
    reencodeSameCodecAboveBytes: 2_147_483_648, skipEfficientSources: false, targetVideoCodec: 'av1', targetContainer: 'mkv',
    hdrHandling: 'TonemapToSdr', optimiseDolbyVision: true, excludePaths: 'Extras\nSamples', qualityCrf: 22, encoderPreset: 'efficient',
    videoAudioCodec: 'opus', videoAudioBitrateKbps: 128, downmixToStereo: true, keepAudioLanguages: 'eng, jpn', keepSubtitleLanguages: 'eng',
    vmafQualityGateEnabled: true, videoQualityStrategy: 'Fixed', minVmafHarmonicMean: 91, minVmafMin: 77, minVmafCatastrophicMin: 44,
    clipVmafEnabled: false, vmafFrameSubsample: 3, durationTolerancePercent: 2, requireSubtitlesRetained: true, minimumSizeSavingPercent: 10,
    audioLoudnessGateEnabled: true, maxLoudnessDriftLufs: 2, audioClippingGateEnabled: true, maxTruePeakDbtp: -1,
    autoEnqueueEnabled: true, autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '06:00', autoReplace: true,
  }
  await mockLibraries(page, customised as typeof library)
  await page.goto('/#/libraries/1/configure')
  for (const name of ['Which files', 'Video', 'Audio & subtitles', 'Checks', 'Schedule & replace', 'Overview']) {
    await pageTab(page, name).click()
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  }

  // One edit, to have something to save. Everything else must come back as it was loaded.
  await pageTab(page, 'Which files').click()
  await page.locator('#lib-name').fill('Films renamed')
  const saved = savedRequest(page)
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  const body = (await saved).postDataJSON() as Record<string, unknown>

  expect(body.name).toBe('Films renamed')
  const { id: _id, name: _name, fileCount: _fileCount, lastAutoEnqueueAt: _last, ...expected } = customised
  for (const [key, value] of Object.entries(expected)) expect(body[key], key).toEqual(value)
})
