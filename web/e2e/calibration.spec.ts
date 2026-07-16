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

type CalibrationMediaKind = 'Video' | 'Audio' | 'Image'

function session(mediaKind: CalibrationMediaKind = 'Video') {
  const id = '11111111-1111-1111-1111-111111111111'
  const trialId = '22222222-2222-2222-2222-222222222222'
  const content = (slot: string) => `/api/calibration/${id}/trials/${trialId}/content/${slot}`
  return {
    id,
    libraryId: 1,
    mediaFileId: 7,
    source: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv',
    mediaKind,
    status: 'Screening',
    preparationProgress: 1, preparationState: 'Working', error: null, result: null,
    trial: {
      id: trialId, phase: 'Screening', number: 1, sampleNumber: 1, sampleCount: 3,
      durationSeconds: mediaKind === 'Image' ? 0 : 12,
      a: { name: 'A', url: content('A'), startSeconds: 0, gainDb: 0 },
      b: { name: 'B', url: content('B'), startSeconds: mediaKind === 'Video' ? 0.751 : 0, gainDb: 0 },
      x: { name: 'X', url: content('X'), startSeconds: mediaKind === 'Video' ? 0.751 : 0, gainDb: 0 },
    },
  }
}

async function mockApp(page: Page, mediaKind: CalibrationMediaKind = 'Video') {
  const currentLibrary = {
    ...library,
    mediaType: mediaKind === 'Audio' ? 'Music' : mediaKind === 'Image' ? 'Photo' : 'Film',
  }
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [currentLibrary])
    if (path === '/api/library-options') return json(route, {
      mediaTypes: [currentLibrary.mediaType], ruleProfiles: ['ConservativeHevc'],
      ruleProfileSpecs: [{ profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24 }],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'], videoCodecs: ['hevc'], containers: ['mp4'],
      encoderPresets: ['medium'], imageFormats: ['jpeg'],
    })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 1, skipped: 0 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, {
      path: currentLibrary.path, exists: true, readable: true, writable: true, ok: true, message: 'ready',
      issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4',
      availableBytes: 100_000_000_000, totalBytes: 200_000_000_000, atomicWithWork: true,
      atomicWithQuarantine: true,
    })
    if (path === '/api/libraries/1/calibration/sources') return json(route, [{
      mediaFileId: 7,
      relativePath: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv',
      durationSeconds: mediaKind === 'Image' ? 0 : mediaKind === 'Audio' ? 240 : 1_200,
      width: mediaKind === 'Audio' ? null : 1_920,
      height: mediaKind === 'Audio' ? null : 1_080,
      mediaKind,
      isHdr: false,
    }])
    if (path === '/api/libraries/1/calibration' && route.request().method() === 'POST') return json(route, session(mediaKind))
    if (path.includes('/content/')) return mediaKind === 'Image'
      ? route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') })
      : route.fulfill({ status: 200, contentType: 'video/mp4', body: '' })
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
  await expect(dialog.locator('..')).toHaveClass(/backdrop-blur-sm/)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await expect(dialog.getByText('CRF/CQ 30')).toHaveCount(0)
  await expect(dialog.getByText(/estimated size reduction/i)).toHaveCount(0)

  await dialog.getByRole('button', { name: 'Prepare blind samples' }).click()
  await expect(dialog.getByText('Does X match A or B?')).toBeVisible()
  await expect(dialog.locator('video[controls]')).toHaveCount(0)
  await expect(dialog.getByRole('slider', { name: 'Sample position' })).toHaveAttribute('max', '12')
  await expect(dialog.getByText('0:00 / 0:12')).toBeVisible()
  await page.keyboard.press('x')
  await expect(dialog.getByRole('button', { name: 'X', exact: true })).toHaveAttribute('aria-pressed', 'true')

  await dialog.getByRole('button', { name: 'Minimise' }).click()
  const minimized = page.getByRole('region', { name: 'Blind quality calibration' })
  await expect(dialog).toBeHidden()
  await expect(minimized).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.body.style.overflow)).not.toBe('hidden')

  await minimized.getByRole('button', { name: 'Expand' }).click()
  await expect(dialog).toBeVisible()
  await expect(dialog).toBeFocused()
  await expect(dialog.getByRole('button', { name: 'X', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.keyboard.press('Escape')
  await expect(minimized).toBeVisible()
  await minimized.getByRole('button', { name: 'Expand' }).click()

  const undersized = await dialog.locator('button:visible').evaluateAll((buttons) =>
    buttons.filter((button) => button.getBoundingClientRect().height < 44).length)
  expect(undersized).toBe(0)

  await dialog.getByRole('button', { name: 'Close' }).click()
  await expect(dialog).toBeHidden()
  await expect(trigger).toBeFocused()
})

test('image calibration keeps one zoomed viewport while switching on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApp(page, 'Image')
  await page.goto('/#/libraries/1/configure')

  await page.getByRole('button', { name: 'Personal quality check' }).click()
  const dialog = page.getByRole('dialog', { name: 'Blind quality calibration' })
  await dialog.getByRole('button', { name: 'Prepare blind samples' }).click()

  const viewport = dialog.getByRole('group', { name: 'Blind comparison image viewport' })
  await expect(viewport).toBeVisible()
  await dialog.getByRole('button', { name: 'Zoom in' }).click()
  const image = viewport.getByRole('img', { name: 'Blind comparison image A' })
  await expect(image).toHaveAttribute('style', /scale\(1\.5\)/)

  await page.keyboard.press('x')
  await expect(viewport.getByRole('img', { name: 'Blind comparison image X' })).toHaveAttribute('style', /scale\(1\.5\)/)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

for (const [mediaKind, mediaType] of [['Audio', 'Music'], ['Image', 'Photo']] as const) {
  test(`personal quality check is available for a saved ${mediaType} library`, async ({ page }) => {
    await mockApp(page, mediaKind)
    await page.goto('/#/libraries/1/configure')

    await expect(page.getByRole('button', { name: 'Personal quality check' })).toBeVisible()
  })
}
