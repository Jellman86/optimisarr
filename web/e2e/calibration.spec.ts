import { expect, test, type Page, type Route } from '@playwright/test'

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Film', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  keepAudioLanguages: null, reencodeLossyAudio: false, targetImageFormat: null,
  imageQuality: null, reencodeLossyImages: false, imageDownscaleMode: 'None', imageDownscaleValue: 0,
  moveOnComplete: false, targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: null,
  minVmafMin: null, vmafQualityGateEnabled: false, minVmafCatastrophicMin: null,
  clipVmafEnabled: null, vmafFrameSubsample: null, autoEnqueueEnabled: false,
  autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '00:00', autoReplace: false,
  lastAutoEnqueueAt: null, fileCount: 1,
}

function session() {
  const id = '11111111-1111-1111-1111-111111111111'
  const trialId = '22222222-2222-2222-2222-222222222222'
  const content = (slot: string) => `/api/calibration/${id}/trials/${trialId}/content/${slot}`
  return {
    id, libraryId: 1, mediaFileId: 7, source: 'Example Film.mkv', status: 'Screening',
    preparationProgress: 1, error: null, result: null,
    trial: {
      id: trialId, phase: 'Screening', number: 1, sampleNumber: 1, sampleCount: 3,
      durationSeconds: 12,
      a: { name: 'A', url: content('A'), startSeconds: 0 },
      b: { name: 'B', url: content('B'), startSeconds: 0 },
      x: { name: 'X', url: content('X'), startSeconds: 0 },
    },
  }
}

async function mockApp(page: Page) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film'], ruleProfiles: ['ConservativeHevc'],
      ruleProfileSpecs: [{ profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24 }],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'], videoCodecs: ['hevc'], containers: ['mp4'],
      encoderPresets: ['medium'], imageFormats: ['jpeg'],
    })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 1, skipped: 0 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, {
      path: library.path, exists: true, readable: true, writable: true, ok: true, message: 'ready',
      issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4',
      availableBytes: 100_000_000_000, totalBytes: 200_000_000_000, atomicWithWork: true,
      atomicWithQuarantine: true,
    })
    if (path === '/api/libraries/1/calibration/sources') return json(route, [{
      mediaFileId: 7, relativePath: 'Example Film.mkv', durationSeconds: 1_200, width: 1_920, height: 1_080,
    }])
    if (path === '/api/libraries/1/calibration' && route.request().method() === 'POST') return json(route, session())
    if (path.includes('/content/')) return route.fulfill({ status: 200, contentType: 'video/mp4', body: '' })
    if (path.startsWith('/api/calibration/') && route.request().method() === 'DELETE') return route.fulfill({ status: 204 })
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

test('blind calibration hides settings, supports keyboard switching, and traps focus on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApp(page)
  await page.goto('/#/libraries/1/configure')

  const trigger = page.getByRole('button', { name: 'Personal quality check' })
  await trigger.click()
  const dialog = page.getByRole('dialog', { name: 'Blind quality calibration' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toBeFocused()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await expect(dialog.getByText('CRF/CQ 30')).toHaveCount(0)
  await expect(dialog.getByText(/estimated size reduction/i)).toHaveCount(0)

  await dialog.getByRole('button', { name: 'Prepare blind samples' }).click()
  await expect(dialog.getByText('Does X match A or B?')).toBeVisible()
  await page.keyboard.press('x')
  await expect(dialog.getByRole('button', { name: 'X', exact: true })).toHaveAttribute('aria-pressed', 'true')

  const undersized = await dialog.locator('button:visible').evaluateAll((buttons) =>
    buttons.filter((button) => button.getBoundingClientRect().height < 44).length)
  expect(undersized).toBe(0)

  await dialog.getByRole('button', { name: 'Close' }).click()
  await expect(dialog).toBeHidden()
  await expect(trigger).toBeFocused()
})
