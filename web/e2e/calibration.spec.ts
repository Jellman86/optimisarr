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

function preparingSession(mediaKind: CalibrationMediaKind) {
  return {
    id, libraryId: 1, mediaFileId: 7,
    source: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv',
    mediaKind, status: 'Preparing', preparationProgress: 0.42, preparationState: 'Working', error: null, result: null,
    variants: [],
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
      currentQuality: mediaKind === 'Audio' ? 160 : mediaKind === 'Image' ? 80 : 24,
      currentProfile: mediaKind === 'Video' ? 'ConservativeHevc' : null,
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
        vmaf: index === 0 || mediaKind !== 'Video' ? null : {
          measuredSamples: 3, totalSamples: 3,
          mean: 96 - index, harmonicMean: 95 - index,
          fifthPercentile: 90 - index, minimum: 75 - index,
          frameCount: 864, modelVersion: 'vmaf_v0.6.1', preprocessing: null,
          samples: Array.from({ length: 3 }, (_, sample) => ({
            sampleNumber: sample + 1, measured: true,
            mean: 96 - index, harmonicMean: 95 - index,
            fifthPercentile: 90 - index, minimum: 75 - index,
            frameCount: 288, modelVersion: 'vmaf_v0.6.1', preprocessing: null, error: null,
          })),
        },
      })),
    },
  }
}

async function mockApp(page: Page, mediaKind: CalibrationMediaKind = 'Video', deferVideoContent = false, keepPreparing = false, revealImmediately = false) {
  const currentLibrary = { ...library, mediaType: mediaKind === 'Audio' ? 'Music' : mediaKind === 'Image' ? 'Photo' : 'Film' }
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [currentLibrary])
    if (path === '/api/library-options') return json(route, { mediaTypes: [currentLibrary.mediaType], ruleProfiles: ['ConservativeHevc'], ruleProfileSpecs: [], hdrHandlings: ['Exclude'], videoCodecs: ['hevc'], containers: ['mp4'], encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: [], imageFormats: ['webp'] })
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 1, skipped: 0 }])
    if (path === '/api/candidates' || path === '/api/exclusions') return json(route, [])
    if (path === '/api/libraries/1/access') return json(route, { path: currentLibrary.path, exists: true, readable: true, writable: true, ok: true, message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000, atomicWithWork: true, atomicWithQuarantine: true })
    if (path === '/api/libraries/1/calibration/sources') return json(route, [{ mediaFileId: 7, relativePath: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv', durationSeconds: mediaKind === 'Image' ? 0 : 1_200, width: mediaKind === 'Audio' ? null : 1_920, height: mediaKind === 'Audio' ? null : 1_080, mediaKind, isHdr: false }])
    if (path === '/api/libraries/1/calibration' && route.request().method() === 'POST') return json(route, keepPreparing ? preparingSession(mediaKind) : revealImmediately ? revealedSession(mediaKind) : comparingSession(mediaKind))
    if (path === `/api/calibration/${id}` && route.request().method() === 'GET') return json(route, preparingSession(mediaKind))
    if (path.endsWith('/classifications') && route.request().method() === 'POST') return json(route, revealedSession(mediaKind))
    if (path.endsWith('/apply') && route.request().method() === 'POST') return json(route, { ...revealedSession(mediaKind), status: 'Applied', result: { ...revealedSession(mediaKind).result, applied: true } })
    if (path.includes('/content')) {
      if (mediaKind === 'Video' && deferVideoContent) await new Promise((resolve) => setTimeout(resolve, 5_000))
      return mediaKind === 'Image'
        ? route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') })
        : route.fulfill({ status: 200, contentType: mediaKind === 'Audio' ? 'audio/mp4' : 'video/mp4', body: '' })
    }
    if (path.startsWith('/api/calibration/') && route.request().method() === 'DELETE') return route.fulfill({ status: 204 })
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

async function openLab(page: Page, mediaKind: CalibrationMediaKind = 'Video', deferVideoContent = false, keepPreparing = false, revealImmediately = false, enableDiagnostics = false) {
  await mockApp(page, mediaKind, deferVideoContent, keepPreparing, revealImmediately)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('button', { name: 'Personal quality check' }).click()
  await expect(page).toHaveURL(/#\/libraries\/1\/quality-check$/)
  if (enableDiagnostics) {
    await page.getByText('Troubleshooting options', { exact: true }).click()
    await page.getByRole('checkbox', { name: 'Temporary stream verification' }).check()
  }
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()
}

test('revealed video results show objective VMAF evidence for every anonymous preset', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await openLab(page, 'Video', false, false, true)

  await expect(page.getByText(/VMAF H /)).toHaveCount(4)
  await expect(page.getByText(/P5 /)).toHaveCount(4)
  await expect(page.getByText(/min /)).toHaveCount(4)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('video grading bay stays inside narrow portrait and landscape viewports', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await openLab(page, 'Video')

  const video = page.locator('video')
  const stage = video.locator('..')
  await expect(video).toBeVisible()
  await expect.poll(async () => stage.evaluate((element) => {
    const bounds = element.getBoundingClientRect()
    return bounds.left >= 0 && bounds.right <= window.innerWidth
  })).toBe(true)
  await expect.poll(async () => video.evaluate((element) => {
    const bounds = element.getBoundingClientRect()
    return bounds.left >= 0 && bounds.right <= window.innerWidth
  })).toBe(true)

  await page.setViewportSize({ width: 812, height: 375 })
  await expect.poll(async () => video.evaluate((element) => {
    const bounds = element.getBoundingClientRect()
    return bounds.left >= 0 && bounds.right <= window.innerWidth
  })).toBe(true)
})

test('sample preparation shows the same live CPU and GPU usage cards as active queue work', async ({ page }) => {
  await openLab(page, 'Video', false, true)

  await expect(page.getByRole('heading', { name: 'Preparing blind samples' })).toBeVisible()
  await expect(page.getByText('42%', { exact: true })).toBeVisible()
  await expect(page.getByText('CPU', { exact: true })).toBeVisible()
  await expect(page.getByText('GPU', { exact: true })).toBeVisible()
})

test('quality check marks the original reference while keeping media-specific candidates anonymous', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  // Use the valid image fixture here so this media-independent flow cannot race an intentionally
  // empty video response. Video timing and fullscreen have their own focused regression below.
  await openLab(page, 'Image')

  await expect(page.getByRole('heading', { name: 'Personal quality check' })).toBeVisible()
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

test('active stream bypass is an explicit per-check option in the start request', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('button', { name: 'Personal quality check' }).click()
  const bypass = page.getByRole('checkbox', { name: 'Ignore active media streams for this check' })
  await expect(bypass).not.toBeChecked()
  await bypass.check()
  const requested = page.waitForRequest((request) =>
    request.method() === 'POST'
      && new URL(request.url()).pathname === '/api/libraries/1/calibration')
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()
  const request = await requested

  expect(request.postDataJSON()).toMatchObject({ ignoreActiveStreams: true })
})

test('temporary stream verification is opt-in', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/libraries/1/configure/encode')
  await page.getByRole('button', { name: 'Personal quality check' }).click()
  // The option breaks blindness, so it starts hidden in a collapsed section.
  await expect(page.getByRole('checkbox', { name: 'Temporary stream verification' })).toBeHidden()
  await page.getByText('Troubleshooting options', { exact: true }).click()
  const proof = page.getByRole('checkbox', { name: 'Temporary stream verification' })
  await expect(proof).not.toBeChecked()
  const requested = page.waitForRequest((request) =>
    request.method() === 'POST'
      && new URL(request.url()).pathname === '/api/libraries/1/calibration')
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()
  const request = await requested

  expect(request.postDataJSON()).toMatchObject({ diagnosticsEnabled: false })
})

test('video switching replaces one native player resource and preserves its matching frame', async ({ page }) => {
  await openLab(page, 'Video', true, false, false, true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await expect(video).toHaveAttribute('controls', '')
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/ORIGINAL/samples/0/content`)
  await expect(page.getByText('Stream diagnostics')).toBeVisible()
  await expect(page.getByText('ORIGINAL · h264 · mkv')).toBeVisible()
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let currentTime = 2
    let paused = false
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => paused })
    player.pause = () => { paused = true }
  })

  const requested = page.waitForRequest((request) => request.url().includes(`/variants/A/samples/0/content`))
  await page.getByRole('button', { name: 'A', exact: true }).click()
  await requested
  await expect(video).toHaveCount(1)
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/A/samples/0/content`)
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let currentTime = 0
    let paused = true
    let seeking = false
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => paused })
    Object.defineProperty(player, 'seeking', { configurable: true, get: () => seeking })
    player.pause = () => { paused = true }
    player.play = async () => { paused = false; player.dataset.playCalls = String(Number(player.dataset.playCalls ?? '0') + 1) }
    player.addEventListener('seeked', () => { seeking = false })
    player.dispatchEvent(new Event('loadedmetadata'))
  })
  await expect(video).toHaveJSProperty('currentTime', 1.249)
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toHaveAttribute('aria-pressed', 'true')
  expect(await video.getAttribute('data-play-calls')).toBeNull()
  await video.dispatchEvent('seeked')
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(video).toHaveAttribute('data-play-calls', '1')
  await expect(page.getByText('ExperimentalAv1 · av1 · mkv · CRF 30')).toBeVisible()
  await expect(page.getByText(`/api/calibration/${id}/variants/A/samples/0/content`, { exact: true })).toBeVisible()
  const exactResource = page.getByRole('link', { name: 'Open exact video resource' })
  await expect(exactResource).toHaveAttribute('href', await video.evaluate((element) => (element as HTMLVideoElement).currentSrc))
  await expect(page.getByRole('button', { name: 'Inspect video full screen' })).toBeVisible()
})

test('rapid sample clicks keep the full hit target active and switch to the latest choice', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let currentTime = 0
    let paused = false
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => paused })
    player.pause = () => { paused = true }
  })

  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/A/samples/0/content`)
  await page.getByRole('button', { name: 'B', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/B/samples/0/content`)
  await expect(page.getByRole('button', { name: 'B', exact: true })).toBeEnabled()
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let currentTime = 0
    let seeking = false
    let paused = true
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => paused })
    Object.defineProperty(player, 'seeking', { configurable: true, get: () => seeking })
    player.pause = () => { paused = true }
    player.play = async () => { paused = false }
    player.addEventListener('seeked', () => { seeking = false })
    player.dispatchEvent(new Event('loadedmetadata'))
  })
  await expect(video).toHaveJSProperty('seeking', true)
  await video.dispatchEvent('seeked')
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

test('music comparison uses listening language throughout', async ({ page }) => {
  await openLab(page, 'Audio')
  await expect(page.getByRole('navigation', { name: 'Listening excerpts' }).getByRole('button')).toHaveText(['Excerpt 1', 'Excerpt 2', 'Excerpt 3'])
  // The fixture streams are empty, so stand in for the browser reporting each excerpt as loaded.
  await page.locator('audio').evaluateAll((players) => players.forEach((player) => {
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12 })
    player.dispatchEvent(new Event('loadedmetadata'))
  }))
  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('heading', { name: 'How does this sample sound?' })).toBeVisible()
  await expect(page.getByRole('button', { name: /Audibly worse/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /I cannot reliably hear a difference/ })).toBeVisible()
  await expect(page.getByText(/Visibly|look\?|see a difference|Scene/)).toHaveCount(0)
})

for (const [mediaKind, copy] of [['Video', 'disposable clips'], ['Audio', 'disposable excerpts'], ['Image', 'disposable test images']] as const) {
  test(`setup safety copy names the temporary ${mediaKind.toLowerCase()} media`, async ({ page }) => {
    await mockApp(page, mediaKind)
    await page.goto('/#/libraries/1/quality-check')
    await expect(page.getByText(copy)).toBeVisible()
    await expect(page.getByRole('list', { name: 'Quality check progress' }).locator('[aria-current="step"]')).toContainText('Choose source')
  })
}

test('rating unlocks only after a pending switch has shown the chosen sample', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => true })
  })
  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/A/samples/0/content`)
  // The original is still on screen, so no rating is offered for any sample yet.
  await expect(page.getByRole('button', { name: 'Original reference', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'B', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/B/samples/0/content`)
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let seeking = false
    let currentTime = 0
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => true })
    Object.defineProperty(player, 'seeking', { configurable: true, get: () => seeking })
    player.addEventListener('seeked', () => { seeking = false })
    player.dispatchEvent(new Event('loadedmetadata'))
  })
  await expect(page.getByRole('button', { name: 'B', exact: true })).toHaveAttribute('aria-pressed', 'false')
  await video.dispatchEvent('seeked')
  await expect(page.getByRole('button', { name: 'B', exact: true })).toHaveAttribute('aria-pressed', 'true')
  const ratings = page.getByRole('group', { name: 'Rate sample B' })
  await expect(ratings.getByRole('button', { name: /Acceptable/ })).toBeEnabled()
  await ratings.getByRole('button', { name: /Acceptable/ }).click()
  await expect(page.getByText('1 of 4 samples classified')).toBeVisible()
})

test('rating can step straight to the next unrated sample', async ({ page }) => {
  await openLab(page, 'Image')
  await expect(page.getByRole('button', { name: 'A', exact: true })).toContainText('Not rated')
  await page.getByRole('button', { name: 'Next unrated sample: A' }).click()
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'true')
  for (const name of names.slice(1)) {
    await page.getByRole('group', { name: `Rate sample ${name}` }).getByRole('button', { name: /Acceptable/ }).click()
    await expect(page.getByRole('button', { name, exact: true })).toContainText('Acceptable')
    if (name !== 'E') await page.getByRole('button', { name: `Next unrated sample: ${names[names.indexOf(name) + 1]}` }).click()
  }
  await expect(page.getByText('Every sample is rated.', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Reveal samples and result' })).toBeEnabled()
})

test('photo inspection switches between fit and actual pixels with matched zoom', async ({ page }) => {
  await openLab(page, 'Image')
  const viewport = page.getByRole('group', { name: 'Blind comparison image viewport' })
  const original = viewport.getByRole('img', { name: 'Original reference' })
  await expect(page.getByRole('button', { name: 'Fit the whole image in view' })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'Zoom in' }).click()
  await expect(page.getByText('Zoom 125%')).toBeVisible()
  await expect(page.getByText('Drag to pan.', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Fit the whole image in view' }).click()
  await expect(original).toHaveAttribute('style', /scale\(1\)/)
  // The fixture is a single pixel, so 1:1 is far smaller than the fitted view.
  await page.getByRole('button', { name: 'Show actual pixels (1:1)' }).click()
  const scale = await original.evaluate((image) => Number(/scale\(([\d.e-]+)\)/.exec(image.getAttribute('style') ?? '')?.[1]))
  expect(scale).toBeLessThan(0.01)
  await page.getByRole('button', { name: 'C', exact: true }).click()
  await expect(viewport.getByRole('img', { name: 'Blind comparison image C' })).toHaveAttribute('style', new RegExp(`scale\\(${scale}\\)`))
})

test('results lead with the recommendation and the exact library change before Apply', async ({ page }) => {
  await openLab(page, 'Video', false, false, true)
  await expect(page.getByRole('heading', { name: 'Your most efficient acceptable setting' })).toBeVisible()
  await expect(page.getByText('Chosen from your ratings', { exact: false })).toBeVisible()
  const change = page.getByRole('heading', { name: 'What Apply changes' }).locator('..')
  await expect(change.getByRole('definition')).toHaveText(['Conservative HEVC · CRF 24', 'Experimental AV1 · CRF 30'])
  await expect(change).toContainText('It queues no work and replaces no file.')
  await expect(change).toContainText('Choosing a preset also adopts')
  await expect(change).toContainText('Estimated from these short samples')
  await expect(page.getByText('They did not choose the recommendation.', { exact: false })).toBeVisible()
  await expect(page.getByRole('list', { name: 'Quality check progress' }).locator('[aria-current="step"]')).toContainText('Reveal and apply')
  await page.getByRole('button', { name: 'Use this setting for the library' }).click()
  await expect(page.getByRole('button', { name: 'Setting applied — no work was queued' })).toBeDisabled()
})

test('music results describe the bitrate change without a preset', async ({ page }) => {
  await openLab(page, 'Audio', false, false, true)
  const change = page.getByRole('heading', { name: 'What Apply changes' }).locator('..')
  await expect(change.getByRole('definition')).toHaveText(['160 kbps', '128 kbps'])
  await expect(change).not.toContainText('Choosing a preset')
  await expect(page.getByText('this playback equipment', { exact: false })).toBeVisible()
})

test('check another file discards the session and returns to source selection', async ({ page }) => {
  await openLab(page, 'Image', false, false, true)
  const discarded = page.waitForRequest((request) => request.method() === 'DELETE' && new URL(request.url()).pathname === `/api/calibration/${id}`)
  await page.getByRole('button', { name: 'Check another file' }).click()
  await discarded
  await expect(page.getByRole('button', { name: 'Prepare blind samples' })).toBeVisible()
})

for (const compareVideoPresets of [false, true]) {
  test(`video check explicitly requests preset comparison: ${compareVideoPresets}`, async ({ page }) => {
    await mockApp(page, 'Video', false, true)
    await page.goto('/#/libraries/1/quality-check')
    const checkbox = page.getByRole('checkbox', { name: /Compare presets/ })
    await expect(checkbox).not.toBeChecked()
    if (compareVideoPresets) await checkbox.check()
    const request = page.waitForRequest(r => r.method() === 'POST' && new URL(r.url()).pathname === '/api/libraries/1/calibration')
    await page.getByRole('button', { name: 'Prepare blind samples', exact: true }).click()
    expect((await request).postDataJSON().compareVideoPresets).toBe(compareVideoPresets)
  })
}

test('no acceptable candidate keeps the library unchanged and offers another check', async ({ page }) => {
  await mockApp(page, 'Image')
  const revealed = revealedSession('Image')
  const noPreference = {
    ...revealed,
    result: {
      ...revealed.result, recommendedQuality: null, estimatedSavingPercent: null, outcome: 'NoAcceptableSetting',
      variants: revealed.result.variants.map((variant) => ({ ...variant, classification: 'VisiblyWorse', recommended: false })),
    },
  }
  await page.route('**/api/libraries/1/calibration', (route) => json(route, noPreference))
  await page.goto('/#/libraries/1/quality-check')
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()

  await expect(page.getByRole('heading', { name: 'Keep the current quality' })).toBeVisible()
  await expect(page.getByText('the library stays unchanged', { exact: false })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'What Apply changes' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Use this setting for the library' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Check another file' })).toBeVisible()
})
