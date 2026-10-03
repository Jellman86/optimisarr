import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Tv', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
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

async function mockLibraries(page: Page, configuredLibrary = library) {
  let currentLibrary = { ...configuredLibrary }
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [currentLibrary])
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

for (const mediaType of ['Film', 'TV', 'Music', 'Photo', 'Other']) {
  test(`${mediaType} auto-accept needs risk acknowledgement and confirmation before saving`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType })
    await page.goto('/#/libraries/1/configure/automate')
    const setting = page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true })
    await expect(setting).not.toBeChecked()
    await expect(page.getByText(/Misconfigured settings can leave you with broken files/)).toBeVisible()
    await setting.click()
    const dialog = page.getByRole('dialog', { name: 'Do you really, really mean it?' })
    await expect(dialog).toBeVisible()
    await expect(setting).not.toBeChecked()
    await expect(dialog.getByText(/Originals stay in Quarantine/)).toBeVisible()
    const enable = dialog.getByRole('button', { name: 'Yes, really. Enable auto-accept' })
    await expect(enable).toBeDisabled()
    await expect(dialog.getByRole('button', { name: 'Keep it off' })).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(dialog).not.toBeVisible()
    await expect(setting).not.toBeChecked()
    await expect(setting).toBeFocused()
    await setting.click()
    await expect(enable).toBeDisabled()
    await dialog.getByRole('checkbox', { name: 'I understand that incorrect settings can damage my media.' }).check()
    await enable.click()
    await expect(dialog).not.toBeVisible()
    await expect(setting).toBeChecked()
    const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).postDataJSON()).toMatchObject({ autoReplace: true, mediaType })
  })
}

test('auto-accept can be switched off without confirmation and the saved choice stays off', async ({ page }) => {
  await mockLibraries(page, { ...library, autoReplace: true })
  await page.goto('/#/libraries/1/configure/automate')
  const setting = page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true })
  await expect(setting).toBeChecked()
  await setting.uncheck()
  await expect(page.getByRole('dialog')).not.toBeVisible()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ autoReplace: false })
  await expect(page.getByText('Updated library "Films".', { exact: true })).toBeVisible()
  await expect(page.locator('[data-library-workflow]')).toBeEnabled()
  await expect(setting).not.toBeChecked()
})

test('new libraries keep auto-accept off even when an existing library has it on', async ({ page }) => {
  await mockLibraries(page, { ...library, autoReplace: true })
  await page.goto('/#/libraries/new/automate')
  await expect(page).toHaveURL(/#\/libraries\/new\/automate$/)
  await expect(page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true })).not.toBeChecked()
})

test('auto-accept confirmation fits a phone and stops pulsing with reduced motion', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 667 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/automate')
  await page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Do you really, really mean it?' })
  await expect(dialog).toBeVisible()
  expect(await dialog.locator('[data-auto-accept-warning]').evaluate(element => getComputedStyle(element).animationName)).toBe('none')
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await expect(dialog.getByRole('button', { name: 'Keep it off' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Keep it off' }).click()
  await expect(page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true })).not.toBeChecked()
})

for (const [theme, viewport] of [
  ['light', { width: 1440, height: 900 }],
  ['dark', { width: 667, height: 375 }],
] as const) {
  test(`auto-accept ${theme} confirmation stays accessible and cancellation never saves`, async ({ page }, testInfo) => {
    await page.setViewportSize(viewport)
    await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), theme)
    await mockLibraries(page, { ...library, name: 'A long library name '.repeat(6) })
    const writes: string[] = []
    page.on('request', request => { if (request.method() === 'PUT') writes.push(request.url()) })
    await page.goto('/#/libraries/1/configure/automate')
    const setting = page.getByRole('checkbox', { name: 'Auto-accept passed jobs', exact: true })
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
    await expect(dialog.getByRole('button', { name: 'Yes, really. Enable auto-accept' })).toBeDisabled()
    await dialog.getByRole('button', { name: 'Keep it off' }).click()
    expect(writes).toEqual([])
  })
}

test('workflow pages preserve drafts across breadcrumbs and browser history', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('button', { name: /Choose files/ }).click()
  await expect(page.getByLabel('Media type', { exact: true })).toHaveValue('TV')
  await page.getByLabel('Name', { exact: true }).fill('Film archive')
  await page.getByRole('navigation', { name: 'Processing workflow' }).getByRole('button', { name: /Encode/ }).click()
  await page.getByRole('button', { name: 'Video settings', exact: true }).click()
  await page.getByRole('button', { name: 'Advanced encoding', exact: true }).click()
  await page.getByLabel('Encoder effort', { exact: true }).selectOption('efficient')
  await page.goBack()
  await expect(page).toHaveURL(/\/encode\/video$/)
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Film archive' }).click()
  await page.getByRole('button', { name: /Choose files/ }).click()
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Film archive')
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ name: 'Film archive', encoderPreset: 'efficient' })
})

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


const stage = (page: Page, name: string) => page.getByRole('navigation', { name: 'Processing workflow' }).getByRole('button', { name: new RegExp(name) })

test('track cleanup exposes language choices and hides irrelevant encoder controls', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode')
  await expect(page.getByRole('group', { name: 'Processing mode' }).getByRole('radio')).toHaveCount(3)
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).not.toBeChecked()
  await expect(page.locator('#lib-vmaf-policy')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Video settings', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Audio & subtitles', exact: true })).toHaveCount(1)
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(page.getByLabel('Keep audio languages', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Keep subtitle languages', { exact: true })).toBeVisible()
  await expect(page.locator('#lib-video-audio-codec')).toHaveCount(0)
  await stage(page, 'Choose files').click()
  await expect(page.locator('#lib-minsize')).toHaveCount(0)
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await stage(page, 'Encode').click()
  await expect(page.getByRole('group', { name: 'Processing mode' })).toHaveCount(0)
})

test('new video libraries keep adaptive VMAF as their default and offer explicit presets', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/new/encode/quality')
  await expect(page.getByRole('radio', { name: /Adaptive per-title VMAF/ })).toBeChecked()
  await expect(page.getByRole('radio', { name: /Fixed library quality/ })).not.toBeChecked()
  await expect(page.locator('#lib-vmaf-policy')).toHaveValue('lossless')
  await expect(page.getByText('This is the direct, predictable path with no preparation encodes.')).toBeVisible()
  await stage(page, 'Encode').click()
  await expect(page.getByRole('radio', { name: /Balanced HEVC/ })).toBeVisible()
})

test('quality strategy choices lead with Adaptive VMAF and fill their section width', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/quality')
  const strategies = page.getByTestId('video-quality-strategies')
  await expect(strategies.getByRole('radio').nth(0)).toHaveAttribute('value', 'AdaptiveVmaf')
  await expect(strategies.getByRole('radio').nth(1)).toHaveAttribute('value', 'Fixed')
  const widths = await strategies.evaluate(element => ({
    strategies: element.getBoundingClientRect().width,
    fieldset: element.closest('fieldset')!.getBoundingClientRect().width,
  }))
  expect(widths.strategies).toBeGreaterThanOrEqual(widths.fieldset - 1)
})

test('adaptive quality enables a concrete target and remains exclusive', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/quality')
  await expect(page.getByRole('radio', { name: /Fixed library quality/ })).toBeChecked()
  await page.getByRole('radio', { name: /Adaptive per-title VMAF/ }).check()
  await expect(page.getByRole('radio', { name: /Fixed library quality/ })).not.toBeChecked()
  await expect(page.locator('#lib-vmaf-policy')).toHaveValue('lossless')
  await expect(page.locator('#lib-vmaf-policy option[value="off"]')).toBeDisabled()
  await expect(page.getByText(/Extra work before every full encode/)).toBeVisible()
})

test("switching away from Scott's preset restores the complete selected bundle across pages", async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('radio', { name: /Scott's/ }).check()
  await expect(page.getByText(/Scott's Settings — HEVC/)).toBeVisible()
  await page.getByRole('button', { name: 'Video settings', exact: true }).click()
  await expect(page.locator('#lib-hdr')).toHaveValue('TonemapToSdr')
  await stage(page, 'Encode').click()
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(page.locator('#lib-video-audio-codec')).toHaveValue('aac')
  await expect(page.getByRole('checkbox', { name: /Downmix surround to stereo/ })).toBeChecked()
  await page.getByRole('button', { name: /Advanced options.*Audio/ }).click()
  await expect(page.locator('#lib-video-audio-bitrate')).toHaveValue('96')
  await stage(page, 'Encode').click()
  await page.getByRole('radio', { name: /Efficiency/ }).check()
  await page.getByRole('button', { name: 'Video settings', exact: true }).click()
  await expect(page.locator('#lib-hdr')).toHaveValue('Preserve')
  await stage(page, 'Encode').click()
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(page.locator('#lib-video-audio-codec')).toHaveValue('copy')
  await expect(page.getByRole('checkbox', { name: /Downmix surround to stereo/ })).not.toBeChecked()
  await page.getByRole('button', { name: /Advanced options.*Audio/ }).click()
  await expect(page.locator('#lib-video-audio-bitrate')).toHaveValue('160')
})

test('verification pages scope controls to media type and separate thresholds from policy', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify')
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toBeVisible()
  await expect(page.locator('#lib-duration-tolerance')).toHaveCount(0)
  await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
  await expect(page.locator('#lib-duration-tolerance')).toBeVisible()
  await stage(page, 'Choose files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await stage(page, 'Verify').click()
  await expect(page.getByRole('checkbox', { name: 'Require all subtitle tracks to be retained' })).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })).toBeVisible()
  await stage(page, 'Choose files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await stage(page, 'Verify').click()
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Preserve image EXIF/ICC metadata' })).toBeVisible()
  await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
  await expect(page.locator('#lib-image-ssim')).toBeVisible()
  await expect(page.locator('#lib-duration-tolerance')).toHaveCount(0)
})

test('ordinary controls and advanced pages have distinct homes for every media kind', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/source')
  await expect(page.locator('#lib-priority')).toHaveJSProperty('tagName', 'SELECT')
  await expect(page.locator('#lib-maxheight')).toBeVisible()
  await expect(page.locator('#lib-downscale')).toHaveCount(0)
  await stage(page, 'Schedule').click()
  await expect(page.getByRole('checkbox', { name: /Move output to a target folder/ })).toBeVisible()
  await stage(page, 'Choose files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Music')
  await stage(page, 'Encode').click()
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(page.locator('#lib-audio-codec')).toBeVisible()
  await expect(page.locator('#lib-audio-bitrate')).toHaveCount(0)
  await page.getByRole('button', { name: /Advanced options.*Audio/ }).click()
  await expect(page.locator('#lib-audio-bitrate')).toBeVisible()
  await stage(page, 'Choose files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await stage(page, 'Encode').click()
  await expect(page.getByLabel('Image compatibility to efficiency')).toBeVisible()
  await page.getByRole('button', { name: 'Images', exact: true }).click()
  await expect(page.locator('#lib-image-downscale')).toBeVisible()
  await expect(page.locator('#lib-image-quality')).toHaveCount(0)
})

test('audio encoding presets change the codec budget while leaving the quality gate unchanged', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.005 })
  await page.goto('/#/libraries/1/configure/encode/audio')
  const control = page.locator('[data-audio-encoding-preset]')
  await control.getByRole('button', { name: 'High', exact: true }).click()
  await expect(control).toContainText('192 kbps')
  await page.getByLabel('Target codec', { exact: true }).selectOption('opus')
  await expect(control).toContainText('160 kbps')
  await page.getByLabel('Target codec', { exact: true }).selectOption('mp3')
  await expect(control).toContainText('256 kbps')
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioTargetCodec: 'mp3', audioBitrateKbps: 256, audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.005 })
})

test('existing audio budgets stay unchanged when a library is opened or its format changes', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music', audioBitrateKbps: 173 })
  await page.goto('/#/libraries/1/configure/encode/audio')
  await expect(page.locator('[data-audio-encoding-preset]')).toContainText('173 kbps')
  await page.getByLabel('Target codec', { exact: true }).selectOption('mp3')
  await expect(page.locator('[data-audio-encoding-preset]')).toContainText('173 kbps')
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: 173 })
})

test('video soundtrack presets apply only to explicit re-encoding and preserve audio copy', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/audio')
  await expect(page.locator('[data-audio-encoding-preset]')).toHaveCount(0)
  await page.getByLabel('Audio track', { exact: true }).selectOption('mp3')
  const control = page.locator('[data-audio-encoding-preset]')
  await control.getByRole('button', { name: 'Very high', exact: true }).click()
  await expect(control).toContainText('320 kbps')
  await page.getByLabel('Audio track', { exact: true }).selectOption('opus')
  await expect(control).toContainText('192 kbps')
  await page.getByLabel('Audio track', { exact: true }).selectOption('copy')
  await expect(control).toHaveCount(0)
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ videoAudioCodec: 'copy', videoAudioBitrateKbps: 192 })
})

test('optional verification thresholds follow their switches on the advanced page', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify/advanced')
  const loudness = page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })
  await expect(page.locator('#lib-loudness-drift')).toHaveCount(0)
  await loudness.check()
  await expect(page.locator('#lib-loudness-drift')).toBeVisible()
  await loudness.uncheck()
  await expect(page.locator('#lib-loudness-drift')).toHaveCount(0)
})

test('minimum and maximum savings are optional, ordered, and saved with the library', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify/advanced')
  const minimum = page.getByLabel('Minimum useful saving', { exact: true })
  await expect(minimum).toBeVisible()
  await expect(minimum).toHaveValue('')
  await minimum.fill('0')
  await expect(page.locator('#lib-verification-error')).toHaveText('Minimum useful saving must be above 0% and no more than 99%.')
  await minimum.fill('10')
  const maximum = page.getByLabel('Maximum allowed saving', { exact: true })
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
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ minimumSizeSavingPercent: 10, maximumSizeSavingPercent: 65 })
})

test('invalid settings stay discoverable after navigating away from their field', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/audio')
  await page.getByLabel('Keep subtitle languages', { exact: true }).fill('english')
  await stage(page, 'Schedule').click()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Use comma-separated 2- or 3-letter language codes only.' }).click()
  await expect(page.getByLabel('Keep subtitle languages', { exact: true })).toHaveValue('english')
  await page.getByLabel('Keep subtitle languages', { exact: true }).fill('eng')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled()
})

test('encoder effort uses portable choices and custom settings remain visible on the overview', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/video/advanced')
  const effort = page.getByLabel('Encoder effort', { exact: true })
  await expect(effort.locator('option')).toHaveText(['Encoder default', 'Fast', 'Balanced', 'Efficient'])
  await effort.selectOption('efficient')
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Films', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/configure$/)
  await expect(page.getByRole('button', { name: /^2 \/ 4 Encode/ })).toContainText('Custom settings: 1')
})

test('legacy effort values are preserved until deliberately changed', async ({ page }) => {
  await mockLibraries(page, { ...library, encoderPreset: 'veryslow' })
  await page.goto('/#/libraries/1/configure/encode/video/advanced')
  const effort = page.getByLabel('Encoder effort', { exact: true })
  await expect(effort).toHaveValue('veryslow')
  await expect(effort.getByText('Legacy exact value: veryslow')).toHaveCount(1)
  await stage(page, 'Choose files').click()
  await page.getByLabel('Name', { exact: true }).fill('Films archive')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled()
})

test('deep links reload the correct advanced page and breadcrumbs return through its hierarchy', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/video/advanced')
  await page.reload()
  const crumbs = page.getByRole('navigation', { name: 'Breadcrumb' })
  await expect(crumbs.getByRole('button')).toHaveText(['Libraries', 'Films', 'Encode', 'Video settings'])
  await expect(crumbs.locator('[aria-current=page]')).toHaveText('Advanced encoding')
  await crumbs.getByRole('button', { name: 'Video settings', exact: true }).click()
  await expect(page.locator('#lib-hdr')).toBeVisible()
  await expect(page.locator('#lib-preset')).toHaveCount(0)
})

test('leaving the library warns about unsaved work while internal navigation does not', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/source')
  await page.getByLabel('Name', { exact: true }).fill('Keep this draft')
  await stage(page, 'Encode').click()
  page.once('dialog', dialog => dialog.dismiss())
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'Libraries', exact: true }).click()
  await expect(page).toHaveURL(/\/configure\/encode$/)
  await stage(page, 'Choose files').click()
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Keep this draft')
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page).toHaveURL(/#\/libraries$/)
})

test('workflow and advanced pages fit a phone without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await mockLibraries(page)
  for (const room of ['', '/source', '/encode', '/encode/video', '/encode/video/advanced', '/encode/audio', '/verify/advanced', '/automate']) {
    await page.goto('/#/libraries/1/configure' + room)
    await page.locator('[data-library-workflow]').waitFor()
    const fit = await page.locator('main').evaluate(main => ({ scrollWidth: main.scrollWidth, clientWidth: main.clientWidth }))
    expect(fit.scrollWidth, room).toBeLessThanOrEqual(fit.clientWidth)
  }
})

test('translated tooltips fit short landscape layouts and support keyboard dismissal', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await page.addInitScript(() => localStorage.setItem('optimisarr:locale', 'de'))
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode/video/advanced')
  await page.locator('html').evaluate(element => { element.style.fontSize = '125%' })
  const tips = page.locator('main [role="tooltip"]')
  expect(await tips.count()).toBeGreaterThan(5)
  for (const tip of await tips.all()) {
    const button = tip.locator('xpath=preceding-sibling::button[1]')
    await button.focus()
    await expect(tip).toHaveCSS('opacity', '1')
    const bounds = (await tip.boundingBox())!
    expect(bounds.x).toBeGreaterThanOrEqual(0)
    expect(bounds.y).toBeGreaterThanOrEqual(0)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(812)
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(375)
    await button.press('Escape')
    await expect(tip).toHaveCSS('opacity', '0')
  }
})

test('library actions stay out of the way in a short landscape viewport', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/source')
  await page.getByLabel('Name', { exact: true }).click()
  await page.getByLabel('Name', { exact: true }).fill('Films archive')
  await expect(page.locator('[data-library-actions]')).toHaveCSS('position', 'static')
  await expect(page.getByLabel('Name', { exact: true })).toBeInViewport()
})

test('new library drafts keep required folder validation across workflow navigation', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/new')
  await page.getByLabel('Name', { exact: true }).fill('New films')
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('button', { name: 'New films', exact: true }).click()
  await expect(page).toHaveURL(/\/new\/overview$/)
  await expect(page.getByRole('button', { name: /Choose files/ })).toBeVisible()
  await page.getByRole('button', { name: /Encode/ }).click()
  await expect(page.getByRole('radio', { name: /Balanced/ })).toBeVisible()
  // The folder remains required; changing stages must not bypass validation.
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
  await page.goto('/#/libraries/1/configure/source')
  await page.getByLabel('Name', { exact: true }).fill('Saved films')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled()
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled()
  await stage(page, 'Encode').click()
  await expect(page.getByRole('radio', { name: /Balanced/ })).toBeDisabled()
  finishSave()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeVisible()
})

test('custom preset opens its tuning page and unsuitable deep links show relevant settings', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('radio', { name: 'Custom', exact: true }).click()
  await expect(page).toHaveURL(/\/encode\/video\/advanced$/)
  await expect(page.locator('#lib-codec')).toBeVisible()
  await stage(page, 'Choose files').click()
  await page.getByLabel('Media type', { exact: true }).selectOption('Photo')
  await page.goBack()
  await expect(page.locator('#lib-codec')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Images', exact: true })).toBeVisible()
})

for (const colorScheme of ['light', 'dark'] as const) {
  test(`workflow rooms keep the shared width and themed surfaces in ${colorScheme} mode`, async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 })
    await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' })
    await mockLibraries(page, { ...library, mediaType: 'Other' })
    let referenceWidth = 0
    for (const room of ['', '/source', '/source/advanced', '/encode', '/encode/quality', '/encode/video', '/encode/video/advanced', '/encode/audio', '/encode/audio/advanced', '/encode/images', '/encode/images/advanced', '/verify', '/verify/advanced', '/automate']) {
      await page.goto('/#/libraries/1/configure' + room)
      const workflow = page.locator('[data-library-workflow]')
      await expect(workflow).toBeVisible()
      const width = (await workflow.boundingBox())!.width
      if (!referenceWidth) referenceWidth = width
      expect(width, room).toBe(referenceWidth)
      expect(width).toBe(1152)
      const card = workflow.locator('.card-interactive, [data-config-section]').first()
      await page.mouse.move(0, 0)
      const surface = await card.evaluate(element => ({
        gradient: getComputedStyle(element).backgroundImage,
        panel: getComputedStyle(element).getPropertyValue('--panel').trim(),
        shadow: getComputedStyle(element).boxShadow,
      }))
      const panelRgb = surface.panel.slice(1).match(/.{2}/g)!.map(channel => parseInt(channel, 16)).join(', ')
      expect(surface.gradient).toContain(`rgb(${panelRgb})`)
      expect(surface.shadow).not.toBe('none')
      await card.hover()
      await expect.poll(() => card.evaluate(element => getComputedStyle(element).boxShadow)).not.toBe(surface.shadow)
      for (const input of await workflow.locator('.input').all()) {
        if (await input.isVisible()) expect((await input.boundingBox())!.height).toBeGreaterThanOrEqual(44)
      }
      expect(await page.locator('main').evaluate(main => main.scrollWidth <= main.clientWidth), room).toBe(true)
    }
  })
}

test('library tab headings describe the displayed content and restore the workflow heading', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Candidates\(/ }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Candidates')
  await page.getByRole('button', { name: /Excluded\(/ }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Excluded')
  await page.getByRole('button', { name: 'Rules', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Choose files')
})

test('unknown bookmarked stages return to the overview instead of rendering an empty editor', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/constructor')
  await expect(page.getByRole('button', { name: /Choose files/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Films', exact: true, level: 1 })).toBeVisible()
})

test('audio quality reporting is opt-in and persists across verification breadcrumbs', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/verify')
  const toggle = page.getByRole('checkbox', { name: 'Audio quality report', exact: true })
  await expect(toggle).not.toBeChecked()
  await toggle.check()
  await expect(page.getByText('Report only. Existing safety checks still decide whether an output can replace the original.', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
  await expect(toggle).toBeChecked()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioQualityReportingEnabled: true })
})

test('video libraries do not advertise standalone audio quality reports', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify')
  await expect(page.getByRole('checkbox', { name: 'Audio quality report', exact: true })).toHaveCount(0)
})

test('audio quality gate requires an explicit limit and survives verification navigation', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/verify')
  const gate = page.getByRole('checkbox', { name: 'Require audio quality', exact: true })
  await expect(gate).not.toBeChecked()
  await gate.check()
  await expect(page.locator('[data-audio-quality-limit]')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Set the limit in Advanced verification', exact: true }).click()
  const limit = page.getByRole('spinbutton', { name: 'Maximum audio difference', exact: true })
  await expect(limit).toBeEmpty()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await limit.fill('0')
  await expect(limit).toBeVisible()
  await limit.fill('0.005')
  await stage(page, 'Verify').click()
  await expect(limit).toHaveCount(0)
  await expect(page.getByText('Maximum allowed difference: 0.005', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
  await expect(gate).toBeChecked()
  await expect(limit).toHaveValue('0.005')
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioQualityGateEnabled: true, maximumAudioQualityDistance: 0.005, audioQualityReportingEnabled: false })
})

test('video libraries do not advertise the standalone audio gate', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify')
  await expect(page.getByRole('checkbox', { name: 'Require audio quality', exact: true })).toHaveCount(0)
})

for (const width of [375, 1440]) {
  for (const theme of ['dark', 'light']) {
    test(`audio difference points and custom values work at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1100 })
      await page.addInitScript(selected => localStorage.setItem('optimisarr-theme', selected), theme)
      await mockLibraries(page, { ...library, mediaType: 'Music' })
      await page.goto('/#/libraries/1/configure/verify')
      await page.getByRole('checkbox', { name: 'Require audio quality', exact: true }).check()
      await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
      const control = page.locator('[data-audio-quality-limit]')
      const slider = control.getByRole('slider', { name: 'Maximum audio difference', exact: true })
      const custom = control.getByRole('spinbutton', { name: 'Maximum audio difference', exact: true })
      await expect(custom).toBeEmpty()
      await control.getByRole('button', { name: '0.001', exact: true }).click()
      await expect(slider).toHaveAttribute('aria-valuetext', '0.001')
      await expect(custom).toHaveCount(0)
      await slider.focus()
      await slider.press('ArrowRight')
      await expect(slider).toHaveAttribute('aria-valuetext', '0.01')
      await control.getByRole('button', { name: 'Custom', exact: true }).click()
      await expect(custom).toHaveValue('0.01')
      await custom.fill('0.005')
      await stage(page, 'Verify').click()
      await page.getByRole('button', { name: 'Advanced verification', exact: true }).click()
      await expect(custom).toHaveValue('0.005')
      await control.scrollIntoViewIfNeeded()
      expect(await control.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await test.info().attach('Audio difference control', { body: await control.screenshot(), contentType: 'image/png' })
      await control.getByRole('button', { name: '0', exact: true }).click()
      const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ audioQualityGateEnabled: true, maximumAudioQualityDistance: 0 })
    })
  }
}

for (const [codec, budgets] of [['aac', [96, 128, 192, 256]], ['opus', [96, 128, 160, 192]], ['mp3', [128, 192, 256, 320]]] as const) {
  for (const [index, label] of ['Space saver', 'Balanced', 'High', 'Very high'].entries()) {
    test(`${codec} ${label} saves its encoding budget with the audio gate off`, async ({ page }) => {
      await mockLibraries(page, { ...library, mediaType: 'Music', audioTargetCodec: codec })
      await page.goto('/#/libraries/1/configure/encode/audio')
      const control = page.locator('[data-audio-encoding-preset]')
      await control.getByRole('button', { name: label, exact: true }).click()
      await expect(control).toContainText(`${budgets[index]} kbps`)
      const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: budgets[index], audioQualityGateEnabled: false, maximumAudioQualityDistance: null })
    })
  }
}

test('Custom audio bitrate and Default remain distinct across formats and navigation', async ({ page }) => {
  await mockLibraries(page, { ...library, mediaType: 'Music' })
  await page.goto('/#/libraries/1/configure/encode/audio')
  const control = page.locator('[data-audio-encoding-preset]')
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await control.getByRole('button', { name: 'Custom', exact: true }).click()
  await expect(page).toHaveURL(/encode\/audio\/advanced$/)
  await expect(page.locator('#lib-audio-bitrate')).toHaveValue('128')
  await page.locator('#lib-audio-bitrate').fill('173')
  await page.getByLabel('Target codec', { exact: true }).selectOption('mp3')
  await expect(page.locator('#lib-audio-bitrate')).toHaveValue('173')
  await expect(control.getByRole('button', { name: 'Custom', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await control.getByRole('button', { name: 'Default', exact: true }).click()
  await expect(page.locator('#lib-audio-bitrate')).toBeEmpty()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ audioTargetCodec: 'mp3', audioBitrateKbps: null, maximumAudioQualityDistance: null })
})

for (const mediaType of ['Music', 'Other']) {
  test(`${mediaType} Default shows the server profile's standalone budget and saves no override`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType, ruleProfile: 'ScottsSettings' })
    await page.goto('/#/libraries/1/configure/encode/audio')
    const control = page.locator('[data-audio-encoding-preset]').last()
    await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
    await expect(control).toContainText('AAC · 96 kbps')
    await control.getByRole('button', { name: 'High', exact: true }).click()
    await control.getByRole('button', { name: 'Default', exact: true }).click()
    await page.locator('#lib-audio-codec').selectOption('aac')
    const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).postDataJSON()).toMatchObject({ audioBitrateKbps: null })
  })
}

test('choosing a video profile shows its soundtrack baseline as Default', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('radio', { name: /Scott's/ }).check()
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  const control = page.locator('[data-audio-encoding-preset]')
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(control).toContainText('AAC · 96 kbps')
  await stage(page, 'Encode').click()
  await page.getByRole('radio', { name: /Balanced/ }).check()
  await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
  await expect(control.getByRole('button', { name: 'Default', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(control).toContainText('AAC · 160 kbps')
})

for (const bitrate of [null, 128]) {
  test(`mixed library ${bitrate == null ? 'inherited' : 'saved'} audio budget stays clear after a profile change`, async ({ page }) => {
    await mockLibraries(page, { ...library, mediaType: 'Other', audioBitrateKbps: bitrate })
    await page.goto('/#/libraries/1/configure/encode/audio')
    const control = page.locator('[data-audio-encoding-preset]').last()
    const note = control.getByText('Saved bitrate. Choose Default to follow the profile if it changes.', { exact: true })
    if (bitrate == null) await expect(note).toHaveCount(0)
    else await expect(note).toBeVisible()
    await stage(page, 'Encode').click()
    await page.getByRole('radio', { name: /Scott's/ }).check()
    await page.getByRole('button', { name: 'Audio & subtitles', exact: true }).click()
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
      await page.addInitScript(selected => localStorage.setItem('optimisarr-theme', selected), theme)
      await mockLibraries(page, { ...library, mediaType: 'Music' })
      await page.goto('/#/libraries/1/configure/encode/audio')
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
    test(`soundtrack gate stays separate and retains its explicit limit at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
      await mockLibraries(page)
      await page.goto('/#/libraries/1/configure/verify')
      const report = page.getByRole('checkbox', { name: 'Soundtrack quality report', exact: true })
      const gate = page.getByRole('checkbox', { name: 'Require soundtrack quality', exact: true })
      await expect(report).not.toBeChecked()
      await expect(gate).not.toBeChecked()
      await gate.check()
      await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
      await page.getByRole('button', { name: 'Set the limit in Advanced verification', exact: true }).click()
      const limit = page.getByRole('spinbutton', { name: 'Maximum soundtrack difference', exact: true })
      await limit.fill('0.005')
      await stage(page, 'Verify').click()
      await expect(gate).toBeChecked()
      const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      expect((await saved).postDataJSON()).toMatchObject({ soundtrackQualityGateEnabled: true,
        maximumSoundtrackQualityDistance: 0.005, soundtrackQualityReportingEnabled: false, audioQualityGateEnabled: false })
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    })
  }
}


test('disabling the soundtrack gate discards an invalid unsaved custom limit', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure/verify')
  const gate = page.getByRole('checkbox', { name: 'Require soundtrack quality', exact: true })
  await gate.check()
  await page.getByRole('button', { name: 'Set the limit in Advanced verification', exact: true }).click()
  await page.getByRole('spinbutton', { name: 'Maximum soundtrack difference', exact: true }).fill('2')
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await stage(page, 'Verify').click()
  await gate.uncheck()
  const saved = page.waitForRequest(request => request.method() === 'PUT' && request.url().endsWith('/api/libraries/1'))
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  expect((await saved).postDataJSON()).toMatchObject({ soundtrackQualityGateEnabled: false, maximumSoundtrackQualityDistance: null })
})
