import { expect, test, type Page, type Route } from '@playwright/test'

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Film', ruleProfile: 'ConservativeHevc',
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
  audioLoudnessGateEnabled: false, maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false, maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true, minimumImageSsim: 0.95, imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, videoQualityStrategy: 'Fixed',
  lastAutoEnqueueAt: null, fileCount: 1,
}

async function mockLibraries(page: Page, configuredLibrary = library) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [configuredLibrary])
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'Music', 'Photo'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [
        { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20 },
        { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24 },
        { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30 },
        { profile: 'RemuxCleanup', codec: null, container: 'mkv', crf: null },
        { profile: 'TrackCleanup', codec: null, container: null, crf: null },
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
      return json(route, { ...configuredLibrary, ...route.request().postDataJSON() })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

test('track cleanup is an exclusive mode and exposes only its relevant video controls', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')

  const processingModes = page.getByRole('group', { name: 'Processing mode' })
  await expect(processingModes.getByRole('radio')).toHaveCount(3)
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).not.toBeChecked()
  await expect(page.getByLabel('VMAF quality')).toHaveCount(0)

  await expect(page.getByLabel('Keep audio languages')).toBeVisible()
  await expect(page.getByLabel('Keep subtitle languages')).toBeVisible()
  await page.getByRole('button', { name: /Advanced options/ }).click()
  await expect(page.getByLabel('Target codec')).toHaveCount(0)
  await expect(page.getByLabel('Minimum file size')).toHaveCount(0)

  await page.getByLabel('Media type').selectOption('Music')
  await expect(processingModes).toHaveCount(0)
  await page.getByLabel('Media type').selectOption('Film')
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).toBeChecked()
})

test('adaptive quality path enables a concrete VMAF target and remains exclusive', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')

  const fixed = page.getByRole('radio', { name: /Fixed library quality/ })
  const adaptive = page.getByRole('radio', { name: /Adaptive per-title VMAF/ })
  await expect(fixed).toBeChecked()
  await adaptive.check()

  await expect(adaptive).toBeChecked()
  await expect(fixed).not.toBeChecked()
  const vmafQuality = page.locator('#lib-vmaf-policy')
  await expect(vmafQuality).toHaveValue('lossless')
  await expect(vmafQuality.locator('option[value="off"]')).toBeDisabled()
  await expect(page.getByText(/Extra work before every full encode/)).toBeVisible()
})

test('verification policy follows the selected library media type', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')

  await expect(page.getByLabel('Duration tolerance')).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Require all subtitle tracks to be retained' })).toBeVisible()
  await expect(page.getByLabel('Minimum SSIM')).toHaveCount(0)

  await page.getByLabel('Media type').selectOption('Music')
  await expect(page.getByLabel('Duration tolerance')).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Require all subtitle tracks to be retained' })).toHaveCount(0)
  await expect(page.locator('#lib-vmaf-policy')).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })).toBeVisible()

  await page.getByLabel('Media type').selectOption('Photo')
  await expect(page.getByLabel('Duration tolerance')).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Require all audio tracks to be retained' })).toHaveCount(0)
  await expect(page.getByLabel('Minimum SSIM')).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Preserve image EXIF/ICC metadata' })).toBeVisible()
})

test('optional verification thresholds use progressive disclosure', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')

  const loudness = page.getByRole('checkbox', { name: 'Audio loudness drift (EBU R128)' })
  await expect(page.getByLabel('Maximum loudness drift')).toHaveCount(0)
  await loudness.check()
  await expect(page.getByLabel('Maximum loudness drift')).toBeVisible()
  await loudness.uncheck()
  await expect(page.getByLabel('Maximum loudness drift')).toHaveCount(0)
})

test('library editor fits a narrow viewport without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')

  const fit = await page.locator('main').evaluate((main) => ({
    scrollWidth: main.scrollWidth,
    clientWidth: main.clientWidth,
  }))
  expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth)
})

test('invalid subtitle language syntax cannot be saved', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()

  await page.getByLabel('Keep subtitle languages').fill('english')
  await expect(page.getByRole('alert')).toHaveText('Use comma-separated 2- or 3-letter language codes only.')
  const save = page.getByRole('button', { name: 'Save' })
  await expect(save).toHaveCount(1)
  await expect(save).toBeDisabled()
})

test('encoder effort uses portable choices rather than raw ffmpeg presets', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('button', { name: /Advanced options/ }).click()

  const effort = page.getByLabel('Encoder effort')
  await expect(effort.locator('option')).toHaveText([
    'Encoder default',
    'Fast',
    'Balanced',
    'Efficient',
  ])
  await effort.selectOption('efficient')
  await expect(page.getByRole('button', { name: 'Save' })).toBeEnabled()
})

test('a recognised legacy preset stays visible and valid until deliberately changed', async ({ page }) => {
  await mockLibraries(page, { ...library, encoderPreset: 'veryslow' })
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('button', { name: /Advanced options/ }).click()

  const effort = page.getByLabel('Encoder effort')
  await expect(effort).toHaveValue('veryslow')
  await expect(effort.locator('option').first()).toHaveText('Encoder default')
  await expect(effort.getByText('Legacy exact value: veryslow')).toHaveCount(1)

  await page.getByLabel('Name').fill('Films archive')
  await expect(page.getByRole('button', { name: 'Save' })).toBeEnabled()
})
