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
  vmafFrameSubsample: null, autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, lastAutoEnqueueAt: null, fileCount: 1,
}

async function mockLibraries(page: Page) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'Music'],
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
      encoderPresets: ['medium'], imageFormats: ['webp'],
    })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 0, skipped: 1 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, {
      path: library.path, exists: true, readable: true, writable: true, ok: true,
      message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/',
      fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000,
      atomicWithWork: true, atomicWithQuarantine: true,
    })
    if (path === '/api/libraries/1' && route.request().method() === 'PUT') {
      return json(route, { ...library, ...route.request().postDataJSON() })
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

  await expect(page.getByRole('radio')).toHaveCount(3)
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).not.toBeChecked()
  await expect(page.getByLabel('VMAF quality')).toHaveCount(0)

  await page.getByRole('button', { name: /Advanced options/ }).click()
  await expect(page.getByLabel('Keep audio languages')).toBeVisible()
  await expect(page.getByLabel('Keep subtitle languages')).toBeVisible()
  await expect(page.getByLabel('Target codec')).toHaveCount(0)
  await expect(page.getByLabel('Minimum file size')).toHaveCount(0)

  await page.getByLabel('Media type').selectOption('Music')
  await expect(page.getByRole('radio')).toHaveCount(0)
  await page.getByLabel('Media type').selectOption('Film')
  await expect(page.getByRole('radio', { name: /Re-encode video/ })).toBeChecked()
})

test('invalid subtitle language syntax cannot be saved', async ({ page }) => {
  await mockLibraries(page)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('radio', { name: /Only remove unwanted audio\/subtitle languages/ }).check()
  await page.getByRole('button', { name: /Advanced options/ }).click()

  await page.getByLabel('Keep subtitle languages').fill('english')
  await expect(page.getByRole('alert')).toHaveText('Use comma-separated 2- or 3-letter language codes only.')
  await expect(page.getByRole('button', { name: 'Save' })).toHaveCount(2)
  for (const button of await page.getByRole('button', { name: 'Save' }).all()) {
    await expect(button).toBeDisabled()
  }
})
