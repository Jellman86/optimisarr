import { expect, test, type Page, type Route } from '@playwright/test'

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Film', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: 'hevc',
  targetContainer: 'mp4', hdrHandling: 'Exclude', optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: 24, encoderPreset: null, audioTargetCodec: 'aac', audioBitrateKbps: 128,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  keepAudioLanguages: null, reencodeLossyAudio: false, targetImageFormat: 'webp',
  imageQuality: 80, reencodeLossyImages: false, imageDownscaleMode: 'None', imageDownscaleValue: 0,
  moveOnComplete: false, targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: null,
  minVmafMin: null, vmafQualityGateEnabled: false, minVmafCatastrophicMin: null,
  clipVmafEnabled: null, vmafFrameSubsample: null, autoEnqueueEnabled: false,
  autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '00:00', autoReplace: false,
  lastAutoEnqueueAt: null, fileCount: 1,
}

type CalibrationMediaKind = 'Video' | 'Audio' | 'Image'
const id = '11111111-1111-1111-1111-111111111111'
const names = ['ORIGINAL', 'A', 'B', 'C', 'D', 'E']
const videoProfiles = ['ExperimentalAv1', 'ScottsSettings', 'ConservativeHevc', 'CompatibilityH264']

function namesFor(mediaKind: CalibrationMediaKind) {
  return mediaKind === 'Video' ? names.slice(0, 5) : names
}

function comparingSession(mediaKind: CalibrationMediaKind) {
  const sampleCount = mediaKind === 'Image' ? 1 : 3
  const variantNames = namesFor(mediaKind)
  return {
    id, libraryId: 1, mediaFileId: 7,
    source: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv',
    mediaKind, status: 'Comparing', preparationProgress: 1, preparationState: 'Working', error: null, result: null,
    variants: variantNames.map((name, variantIndex) => ({
      name,
      isOriginal: name === 'ORIGINAL',
      diagnostics: name === 'ORIGINAL'
        ? { profile: null, codec: mediaKind === 'Video' ? 'h264' : null, container: mediaKind === 'Video' ? 'mkv' : null, requestedQuality: null, encoder: null, qualityMode: null, effectiveQuality: null }
        : { profile: mediaKind === 'Video' ? videoProfiles[variantIndex - 1] : null, codec: mediaKind === 'Video' ? ['av1', 'hevc', 'hevc', 'h264'][variantIndex - 1] : mediaKind === 'Audio' ? 'aac' : 'webp', container: mediaKind === 'Video' ? ['mkv', 'mp4', 'mp4', 'mp4'][variantIndex - 1] : null, requestedQuality: mediaKind === 'Video' ? [30, 24, 24, 20][variantIndex - 1] : 70, encoder: mediaKind === 'Video' ? ['libsvtav1', 'libx265', 'libx265', 'libx264'][variantIndex - 1] : null, qualityMode: mediaKind === 'Video' ? 'CRF' : null, effectiveQuality: mediaKind === 'Video' ? [30, 24, 24, 20][variantIndex - 1] : 70 },
      samples: Array.from({ length: sampleCount }, (_, scene) => ({
        sampleNumber: scene + 1, sampleCount, durationSeconds: mediaKind === 'Image' ? 0 : 12,
        url: `/api/calibration/${id}/variants/${name}/samples/${scene}/content`,
        startSeconds: mediaKind === 'Video' && variantIndex === 0 ? 0.751 : 0,
        gainDb: 0,
      })),
    })),
  }
}

function revealedSession(mediaKind: CalibrationMediaKind) {
  const variantNames = namesFor(mediaKind)
  const qualities = mediaKind === 'Video' ? [null, 30, 24, 24, 20] : [null, 30, 27, 24, 21, 18]
  return {
    ...comparingSession(mediaKind), status: 'Revealed',
    result: {
      recommendedQuality: mediaKind === 'Audio' ? 128 : mediaKind === 'Image' ? 70 : 30,
      recommendedProfile: mediaKind === 'Video' ? 'ExperimentalAv1' : null,
      encoder: mediaKind === 'Video' ? 'libx265' : mediaKind === 'Audio' ? 'aac' : 'webp',
      qualityMode: mediaKind === 'Audio' ? 'kbps' : mediaKind === 'Image' ? 'quality' : 'CRF',
      effectiveQuality: mediaKind === 'Audio' ? 128 : mediaKind === 'Image' ? 70 : 30,
      estimatedSavingPercent: 42.5, outcome: 'PreferenceFound', applied: false,
      variants: variantNames.map((name, index) => ({
        name, isOriginal: index === 0,
        profile: index === 0 || mediaKind !== 'Video' ? null : videoProfiles[index - 1],
        codec: index === 0 ? 'h264' : mediaKind === 'Video' ? ['av1', 'hevc', 'hevc', 'h264'][index - 1] : mediaKind === 'Audio' ? 'aac' : 'webp',
        container: mediaKind === 'Video' ? (index === 0 || index === 1 ? 'mkv' : 'mp4') : null,
        quality: index === 0 ? null : mediaKind === 'Audio' ? [0, 192, 160, 128, 96, 64][index] : mediaKind === 'Image' ? [0, 92, 82, 70, 55, 40][index] : qualities[index],
        classification: 'Acceptable', encoder: index === 0 ? null : mediaKind === 'Video' ? 'libx265' : mediaKind === 'Audio' ? 'aac' : 'webp',
        qualityMode: index === 0 ? null : mediaKind === 'Audio' ? 'kbps' : mediaKind === 'Image' ? 'quality' : 'CRF',
        effectiveQuality: index === 0 ? null : mediaKind === 'Audio' ? [0, 192, 160, 128, 96, 64][index] : mediaKind === 'Image' ? [0, 92, 82, 70, 55, 40][index] : qualities[index],
        estimatedSavingPercent: index === 0 ? null : 20 + index * 5,
        recommended: index === (mediaKind === 'Video' ? 1 : 3),
      })),
    },
  }
}

async function mockApp(page: Page, mediaKind: CalibrationMediaKind = 'Video') {
  const currentLibrary = { ...library, mediaType: mediaKind === 'Audio' ? 'Music' : mediaKind === 'Image' ? 'Photo' : 'Film' }
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [currentLibrary])
    if (path === '/api/library-options') return json(route, { mediaTypes: [currentLibrary.mediaType], ruleProfiles: ['ConservativeHevc'], ruleProfileSpecs: [], hdrHandlings: ['Exclude'], videoCodecs: ['hevc'], containers: ['mp4'], encoderPresets: ['medium'], imageFormats: ['webp'] })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 1, skipped: 0 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, { path: currentLibrary.path, exists: true, readable: true, writable: true, ok: true, message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000, atomicWithWork: true, atomicWithQuarantine: true })
    if (path === '/api/libraries/1/calibration/sources') return json(route, [{ mediaFileId: 7, relativePath: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv', durationSeconds: mediaKind === 'Image' ? 0 : 1_200, width: mediaKind === 'Audio' ? null : 1_920, height: mediaKind === 'Audio' ? null : 1_080, mediaKind, isHdr: false }])
    if (path === '/api/libraries/1/calibration' && route.request().method() === 'POST') return json(route, comparingSession(mediaKind))
    if (path.endsWith('/classifications') && route.request().method() === 'POST') return json(route, revealedSession(mediaKind))
    if (path.endsWith('/apply') && route.request().method() === 'POST') return json(route, { ...revealedSession(mediaKind), status: 'Applied', result: { ...revealedSession(mediaKind).result, applied: true } })
    if (path.includes('/content')) return mediaKind === 'Image'
      ? route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') })
      : route.fulfill({ status: 200, contentType: mediaKind === 'Audio' ? 'audio/mp4' : 'video/mp4', body: '' })
    if (path.startsWith('/api/calibration/') && route.request().method() === 'DELETE') return route.fulfill({ status: 204 })
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

async function openLab(page: Page, mediaKind: CalibrationMediaKind = 'Video') {
  await mockApp(page, mediaKind)
  await page.goto('/#/libraries/1/configure')
  await page.getByRole('button', { name: 'Personal quality check' }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/quality-check$/)
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()
}

test('quality check marks the original reference while keeping media-specific candidates anonymous', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  // Use the valid image fixture here so this media-independent flow cannot race an intentionally
  // empty video response. Video timing and fullscreen have their own focused regression below.
  await openLab(page, 'Image')

  await expect(page.getByRole('heading', { name: 'Quality lab' })).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toBeVisible()
  for (const name of names.slice(1)) await expect(page.getByRole('button', { name, exact: true })).toBeVisible()
  await expect(page.getByText('Original', { exact: true })).toHaveCount(3)
  await expect(page.getByText(/Quality 70/)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Reveal samples and result' })).toBeDisabled()
  const undersized = await page.locator('.quality-lab button:visible').evaluateAll((buttons) =>
    buttons.filter((button) => button.getBoundingClientRect().height < 44).length)
  expect(undersized).toBe(0)

  for (const name of names.slice(1)) {
    await page.getByRole('button', { name, exact: true }).click()
    await page.getByRole('button', { name: /Acceptable I notice/ }).click()
  }
  await expect(page.getByText('5 of 5 samples classified')).toBeVisible()
  await page.getByRole('button', { name: 'Reveal samples and result' }).click()

  await expect(page.getByText('Your most efficient acceptable setting')).toBeVisible()
  await expect(page.getByText('Quality 70', { exact: true })).toHaveCount(3)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 812, height: 375 })
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('video switching does not reveal the new sample until its matching frame has been sought', async ({ page }) => {
  await openLab(page)
  const videos = page.locator('video')
  await expect(videos).toHaveCount(5)
  const streamUrls = await videos.evaluateAll((elements) => elements.map((element) => element.getAttribute('src')))
  expect(new Set(streamUrls).size).toBe(5)
  await expect(page.getByText('Stream diagnostics')).toBeVisible()
  await expect(page.getByText('ORIGINAL · h264 · mkv')).toBeVisible()
  await videos.evaluateAll((elements) => {
    elements.forEach((element, index) => {
      const video = element as HTMLVideoElement
      let currentTime = index === 0 ? 2 : 0
      let paused = index !== 0
      let seeking = false
      Object.defineProperty(video, 'duration', { configurable: true, get: () => 12.751 })
      Object.defineProperty(video, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
      Object.defineProperty(video, 'paused', { configurable: true, get: () => paused })
      Object.defineProperty(video, 'seeking', { configurable: true, get: () => seeking })
      video.pause = () => { paused = true }
      video.play = async () => { paused = false; video.dataset.playCalls = String(Number(video.dataset.playCalls ?? '0') + 1) }
      video.addEventListener('seeked', () => { seeking = false })
      if (index === 0) video.dispatchEvent(new Event('loadedmetadata'))
      currentTime = index === 0 ? 2 : 0
      seeking = false
    })
  })

  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await videos.nth(1).dispatchEvent('loadedmetadata')
  await expect(videos.nth(1)).toHaveJSProperty('currentTime', 1.249)
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toHaveAttribute('aria-pressed', 'true')
  expect(await videos.nth(1).getAttribute('data-play-calls')).toBeNull()
  await videos.nth(1).dispatchEvent('seeked')
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(videos.nth(1)).toHaveAttribute('data-play-calls', '1')
  await expect(page.getByText('ExperimentalAv1 · av1 · mkv · CRF 30')).toBeVisible()
  await expect(page.getByText(`/api/calibration/${id}/variants/A/samples/0/content`, { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Inspect video full screen' })).toBeVisible()
})

test('rapid sample clicks keep the full hit target active and switch to the latest choice', async ({ page }) => {
  await openLab(page)
  const videos = page.locator('video')
  await videos.evaluateAll((elements) => {
    elements.forEach((element, index) => {
      const video = element as HTMLVideoElement
      let currentTime = 0
      let seeking = false
      let paused = index !== 0
      Object.defineProperty(video, 'duration', { configurable: true, get: () => 12.751 })
      Object.defineProperty(video, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
      Object.defineProperty(video, 'paused', { configurable: true, get: () => paused })
      Object.defineProperty(video, 'seeking', { configurable: true, get: () => seeking })
      video.pause = () => { paused = true }
      video.play = async () => { paused = false; video.dataset.playCalls = String(Number(video.dataset.playCalls ?? '0') + 1) }
      video.addEventListener('seeked', () => { seeking = false })
      video.dispatchEvent(new Event('loadedmetadata'))
      seeking = false
    })
  })

  await page.getByRole('button', { name: 'A', exact: true }).click()
  await page.getByRole('button', { name: 'B', exact: true }).click()
  await expect(page.getByRole('button', { name: 'B', exact: true })).toBeEnabled()
  await expect(videos.nth(2)).toHaveJSProperty('seeking', true)
  await videos.nth(2).dispatchEvent('seeked')
  await videos.nth(1).dispatchEvent('seeked')
  await expect(page.getByRole('button', { name: 'B', exact: true })).toHaveAttribute('aria-pressed', 'true')
})

test('image quality uses one zoomable viewport and six anonymous variants', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await openLab(page, 'Image')
  const viewport = page.getByRole('group', { name: 'Blind comparison image viewport' })
  await expect(viewport.locator('img')).toHaveCount(6)
  await page.getByRole('button', { name: 'Zoom in' }).click()
  await expect(viewport.getByRole('img', { name: 'Original reference' })).toHaveAttribute('style', /scale\(1\.25\)/)
  await page.getByRole('button', { name: 'E', exact: true }).click()
  await expect(viewport.getByRole('img', { name: 'Blind comparison image E' })).toHaveAttribute('style', /scale\(1\.25\)/)
  await expect(page.getByRole('button', { name: 'Inspect video full screen' })).toHaveCount(0)
})

test('audio quality uses the same anonymous classification model with audio players', async ({ page }) => {
  await openLab(page, 'Audio')
  await expect(page.locator('audio')).toHaveCount(6)
  await expect(page.getByText('Listen under your normal conditions')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Inspect video full screen' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Reveal samples and result' })).toBeDisabled()
})
