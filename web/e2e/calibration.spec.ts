import { expect, test, type Locator, type Page, type Route, type TestInfo } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mkdir, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { fabricatedQualityMedia, serveQualityMedia, type QualityCheckMedia } from './helpers/quality-check-media'

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

function comparingSession(mediaKind: CalibrationMediaKind, diagnosticsEnabled = false) {
  const sampleCount = mediaKind === 'Image' ? 1 : 3
  const variantNames = namesFor(mediaKind)
  return {
    id, libraryId: 1, mediaFileId: 7,
    source: mediaKind === 'Image' ? 'Example Photo.tiff' : mediaKind === 'Audio' ? 'Example Track.flac' : 'Example Film.mkv',
    mediaKind, status: 'Comparing', preparationProgress: 1, preparationState: 'Working', error: null, result: null,
    variants: variantNames.map((name, variantIndex) => ({
      name,
      isOriginal: name === 'ORIGINAL',
      diagnostics: !diagnosticsEnabled ? null : name === 'ORIGINAL'
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

async function mockApp(page: Page, mediaKind: CalibrationMediaKind = 'Video', deferVideoContent = false, keepPreparing = false, revealImmediately = false, serverEncoding = false) {
  await page.route('**/hubs/jobs/negotiate**', route => json(route, {
    negotiateVersion: 1, connectionId: 'quality-check', connectionToken: 'quality-check',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }))
  await page.routeWebSocket('**/hubs/jobs?*', socket => {
    let metrics: ReturnType<typeof setInterval> | undefined
    socket.onMessage(message => {
      if (!String(message).includes('"protocol"')) return
      socket.send('{}\u001e')
      // This server samples its own CPU and GPU only while it is encoding.
      if (serverEncoding) metrics = setInterval(() => socket.send(JSON.stringify({ type: 1, target: 'systemMetrics', arguments: [{ cpuPercent: 37, gpuSupported: true, gpuPercent: 62, gpuEngine: 'Video' }] }) + '\u001e'), 250)
    })
    const ping = setInterval(() => socket.send('{"type":6}\u001e'), 10_000)
    socket.onClose(() => { clearInterval(ping); clearInterval(metrics) })
  })
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
    if (path === '/api/libraries/1/calibration' && route.request().method() === 'POST') return json(route, keepPreparing ? preparingSession(mediaKind) : revealImmediately ? revealedSession(mediaKind) : comparingSession(mediaKind, route.request().postDataJSON().diagnosticsEnabled === true))
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

test('sample preparation on this server shows the same live CPU and GPU usage cards as active queue work', async ({ page }) => {
  await mockApp(page, 'Video', false, true, false, true)
  await page.goto('/#/libraries/1/quality-check')
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()

  await expect(page.getByRole('heading', { name: 'Preparing blind samples' })).toBeVisible()
  await expect(page.getByText('42%', { exact: true })).toBeVisible()
  await expect(page.getByText('CPU', { exact: true })).toBeVisible()
  await expect(page.getByText('GPU', { exact: true })).toBeVisible()
})

test('sample preparation on a worker hides this server\'s idle usage cards instead of showing dashes', async ({ page }) => {
  await openLab(page, 'Video', false, true)

  await expect(page.getByRole('heading', { name: 'Preparing blind samples' })).toBeVisible()
  await expect(page.getByText('Live system usage')).toHaveCount(0)
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
  // Native controls own the picture in verification mode, so the close-up controls stay away.
  await expect(page.getByRole('group', { name: 'Video zoom and pan controls' })).toHaveCount(0)
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

// The fixture streams are empty, so stand in for the browser loading the chosen resource. The
// seek stays pending until the test dispatches `seeked`, like a real stream fetching its frame.
async function loadPendingVideo(video: ReturnType<Page['locator']>, size = { width: 1920, height: 1080 }) {
  await video.evaluate((element, { width, height }) => {
    const player = element as HTMLVideoElement
    let currentTime = 0
    let paused = true
    let seeking = false
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'videoWidth', { configurable: true, get: () => width })
    Object.defineProperty(player, 'videoHeight', { configurable: true, get: () => height })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeking = true } })
    Object.defineProperty(player, 'paused', { configurable: true, get: () => paused })
    Object.defineProperty(player, 'seeking', { configurable: true, get: () => seeking })
    player.pause = () => { paused = true }
    player.play = async () => { paused = false }
    player.addEventListener('seeked', () => { seeking = false })
    player.dispatchEvent(new Event('loadedmetadata'))
  }, size)
}

test('video close-up zoom and pan stay matched when switching samples', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await loadPendingVideo(video, { width: 3840, height: 2160 })
  await video.dispatchEvent('seeked')

  await expect(page.getByRole('button', { name: 'Fit the whole picture in view' })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'Zoom in' }).click()
  await page.getByRole('button', { name: 'Zoom in' }).click()
  await expect(page.getByText('Zoom 150%')).toBeVisible()
  await expect(video).toHaveAttribute('style', /scale\(1\.5\)/)
  const stage = page.getByRole('group', { name: 'Blind comparison streams' })
  const box = (await stage.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2 + 20)
  await page.mouse.up()
  await expect(video).toHaveAttribute('style', /translate\(40px, 20px\) scale\(1\.5\)/)

  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/A/samples/0/content`)
  await loadPendingVideo(video)
  await video.dispatchEvent('seeked')
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(video).toHaveAttribute('style', /translate\(40px, 20px\) scale\(1\.5\)/)

  // 1:1 follows the original's picture size, so a 4K reference needs more than the fitted view.
  await page.getByRole('button', { name: 'Show actual pixels (1:1)' }).click()
  const scale = await video.evaluate((element) => Number(/scale\(([\d.e-]+)\)/.exec(element.getAttribute('style') ?? '')?.[1]))
  expect(scale).toBeGreaterThan(2)
  await page.getByRole('button', { name: 'Fit the whole picture in view' }).click()
  await expect(video).toHaveAttribute('style', /translate\(0px, 0px\) scale\(1\)/)
})

test('full screen keeps a sample switcher and shortcut keys that wait for the matching frame', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await loadPendingVideo(video)
  await video.dispatchEvent('seeked')
  await video.evaluate((element) => { (element as HTMLVideoElement).currentTime = 2.751 })
  await video.dispatchEvent('seeked')

  await page.getByRole('button', { name: 'Inspect video full screen' }).click()
  await expect.poll(() => page.evaluate(() => document.fullscreenElement !== null)).toBe(true)
  const switcher = page.getByRole('group', { name: 'Switch sample' })
  await expect(switcher.getByRole('button')).toHaveText(['O', 'A', 'B', 'C', 'D'])
  await expect(switcher.getByRole('button', { name: 'Show original' })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'Zoom in' }).click()

  await page.keyboard.press('b')
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/B/samples/0/content`)
  await loadPendingVideo(video)
  // B has loaded but not yet reached the frame, so the original stays named and B is not shown.
  await expect(switcher.getByRole('button', { name: 'Show original' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByText('Loading exact video resource…')).toBeVisible()
  await expect(video).toHaveJSProperty('currentTime', 2)
  await video.dispatchEvent('seeked')
  await expect(switcher.getByRole('button', { name: 'Show sample B' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByText('Loading exact video resource…')).toHaveCount(0)
  await expect(video).toHaveAttribute('style', /scale\(1\.25\)/)

  await switcher.getByRole('button', { name: 'Show original' }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/ORIGINAL/samples/0/content`)
  await loadPendingVideo(video)
  await video.dispatchEvent('seeked')
  await expect(video).toHaveJSProperty('currentTime', 2.751)
  await expect(switcher.getByRole('button', { name: 'Show original' })).toHaveAttribute('aria-pressed', 'true')
  expect(await page.evaluate(() => document.fullscreenElement !== null)).toBe(true)
})

test('pausing a video settles its picture on the exact frame other samples will seek to', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await loadPendingVideo(video)
  await video.dispatchEvent('seeked')
  await page.getByRole('button', { name: 'Play sample' }).click()
  await expect(page.getByRole('button', { name: 'Pause sample' })).toBeVisible()
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    let currentTime = 4.2
    let seeks = 0
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => currentTime, set: (value: number) => { currentTime = value; seeks++; player.dataset.seeks = String(seeks) } })
  })
  await page.getByRole('button', { name: 'Pause sample' }).click()
  await expect(video).toHaveAttribute('data-seeks', '1')
  await expect(video).toHaveJSProperty('currentTime', 4.2)
  await expect(page.getByRole('button', { name: 'Play sample' })).toBeVisible()
})

test('a seek the browser clamps is reported instead of shown as the matched frame', async ({ page }) => {
  await openLab(page, 'Video', true)
  const video = page.locator('video')
  await expect(video).toHaveCount(1)
  await loadPendingVideo(video)
  await video.dispatchEvent('seeked')
  await video.evaluate((element) => { (element as HTMLVideoElement).currentTime = 5.751 })
  await video.dispatchEvent('seeked')

  await page.getByRole('button', { name: 'A', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/A/samples/0/content`)
  // An unseekable resource finishes "seeking" at the start instead of the requested time.
  await video.evaluate((element) => {
    const player = element as HTMLVideoElement
    Object.defineProperty(player, 'duration', { configurable: true, get: () => 12.751 })
    Object.defineProperty(player, 'currentTime', { configurable: true, get: () => 0, set: () => undefined })
    Object.defineProperty(player, 'seeking', { configurable: true, get: () => false })
    player.dispatchEvent(new Event('loadedmetadata'))
  })
  await expect(page.getByText('could not play one of the native comparison streams', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: 'A', exact: true })).toHaveAttribute('aria-pressed', 'false')
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
  // Switching away from a candidate leaves its rating controls visible until the new sample
  // is ready. They must not accept a rating intended for the pending sample.
  await page.getByRole('button', { name: 'C', exact: true }).click()
  await expect(video).toHaveAttribute('src', `/api/calibration/${id}/variants/C/samples/0/content`)
  await expect(ratings.getByRole('button', { name: /Acceptable/ })).toBeDisabled()
  await expect(page.getByText('Rating unlocks when the selected sample has loaded.')).toBeVisible()
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
  await page.getByRole('button', { name: 'Zoom out' }).click()
  await expect(original).toHaveAttribute('style', new RegExp(`scale\\(${scale}\\)`))
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
  await expect(change).toContainText('no file. Choosing a preset also adopts')
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


test('keyboard focus follows preparation and returns to source selection', async ({ page }) => {
  await openLab(page, 'Image', false, true)
  const heading = page.getByRole('heading', { name: 'Preparing blind samples', exact: true })
  await expect(heading).toBeFocused()
  await page.route(`**/api/calibration/${id}`, (route) => json(route, { ...preparingSession('Image'), status: 'Failed', error: 'The selected photo could not be decoded.' }))
  await expect(page.getByRole('alert')).toContainText('The selected photo could not be decoded.')
  await page.getByRole('button', { name: 'Check another file' }).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: 'Choose a representative still image' })).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(page.getByRole('combobox')).toBeFocused()
})

test('comparison and reveal hand keyboard focus to their new content', async ({ page }) => {
  await openLab(page, 'Image')
  await expect(page.getByRole('heading', { name: 'Original', exact: true })).toBeFocused()
  for (const name of names.slice(1)) {
    await page.getByRole('button', { name, exact: true }).click()
    await page.getByRole('group', { name: `Rate sample ${name}` }).getByRole('button', { name: /Acceptable/ }).click()
  }
  await page.getByRole('button', { name: 'Reveal samples and result' }).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: 'Your most efficient acceptable setting' })).toBeFocused()
})

test('stale Apply refusal is announced and never marks the setting as applied', async ({ page }) => {
  await openLab(page, 'Image', false, false, true)
  await page.route(`**/api/calibration/${id}/apply`, (route) => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'Library settings changed. Start a new quality check.' }) }))
  await page.getByRole('button', { name: 'Use this setting for the library' }).click()
  await expect(page.getByRole('alert')).toContainText('Library settings changed')
  await expect(page.getByRole('button', { name: 'Setting applied — no work was queued' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Check another file' }).click()
  await expect(page.getByRole('button', { name: 'Prepare blind samples' })).toBeVisible()
})

test('a lost preparation poll offers a way to start another check', async ({ page }) => {
  await openLab(page, 'Image', false, true)
  await page.route(`**/api/calibration/${id}`, (route) => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Preparation status is temporarily unavailable.' }) }))
  await expect(page.getByRole('alert')).toContainText('Preparation status is temporarily unavailable.')
  await page.getByRole('button', { name: 'Check another file' }).click()
  await expect(page.getByRole('button', { name: 'Prepare blind samples' })).toBeVisible()
})

test('the next unrated shortcut hands focus to the new sample rating', async ({ page }) => {
  await openLab(page, 'Image')
  await page.getByRole('button', { name: 'Next unrated sample: A', exact: true }).focus()
  await page.keyboard.press('Enter')
  const rating = page.getByRole('group', { name: 'Rate sample A' })
  await expect(rating).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(rating.getByRole('button', { name: /Indistinguishable/ })).toBeFocused()
  await page.keyboard.press('Tab')
  await page.keyboard.press('Enter')
  await expect(rating.getByRole('button', { name: /Acceptable/ })).toHaveAttribute('aria-pressed', 'true')
})

test('a completed background preparation does not steal navigation focus', async ({ page }) => {
  await openLab(page, 'Image', false, true)
  const back = page.getByRole('button', { name: 'Back to library' })
  await back.focus()
  await page.route(`**/api/calibration/${id}`, route => json(route, comparingSession('Image')))
  await expect(page.getByRole('heading', { name: 'Original', exact: true })).toBeVisible()
  await expect(back).toBeFocused()
})

test('waiting for a worker describes the wait without pretending preparation has started', async ({ page }) => {
  await mockApp(page, 'Image', false, true)
  const waiting = { ...preparingSession('Image'), preparationState: 'Waiting', preparationProgress: 0 }
  await page.route('**/api/libraries/1/calibration', route => json(route, waiting))
  await page.route(`**/api/calibration/${id}`, route => json(route, waiting))
  await page.goto('/#/libraries/1/quality-check')
  await page.getByRole('button', { name: 'Prepare blind samples' }).click()
  await expect(page.getByRole('heading', { name: 'Waiting for an available worker' })).toBeVisible()
  await expect(page.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
  await expect(page.getByRole('button', { name: 'Back to library' })).toBeEnabled()
})

test('an empty source list explains how to populate it and cannot start a check', async ({ page }) => {
  await mockApp(page, 'Image')
  await page.route('**/api/libraries/1/calibration/sources', route => json(route, []))
  await page.goto('/#/libraries/1/quality-check')
  await expect(page.getByText('No suitable file is ready', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Prepare blind samples' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Back to library' })).toBeEnabled()
})

test('unplayable media announces the failure and offers another check', async ({ page }) => {
  await openLab(page, 'Image')
  await page.getByRole('img', { name: 'Original reference' }).evaluate(image => image.dispatchEvent(new Event('error')))
  await expect(page.getByRole('alert')).toContainText('play')
  await expect(page.getByRole('button', { name: 'Reveal samples and result' })).toBeDisabled()
  await page.getByRole('button', { name: 'Check another file' }).click()
  await expect(page.getByRole('button', { name: 'Prepare blind samples' })).toBeVisible()
})

test('a keyboard-only check can rate every sample, reveal and apply with confirmation', async ({ page }) => {
  async function tabTo(target: Locator) {
    for (let step = 0; step < 50; step++) {
      if (await target.evaluate(element => element === document.activeElement)) return
      await page.keyboard.press('Tab')
    }
    await expect(target).toBeFocused()
  }
  await mockApp(page, 'Image')
  await page.goto('/#/libraries/1/quality-check')
  await expect(page.getByRole('combobox')).toBeVisible()
  await tabTo(page.getByRole('button', { name: 'Prepare blind samples' }))
  await page.keyboard.press('Enter')
  for (const name of names.slice(1)) {
    await tabTo(page.getByRole('button', { name: `Next unrated sample: ${name}`, exact: true }))
    await page.keyboard.press('Enter')
    const rating = page.getByRole('group', { name: `Rate sample ${name}` })
    await expect(rating).toBeFocused()
    await tabTo(rating.getByRole('button', { name: /Acceptable/ }))
    await page.keyboard.press('Enter')
  }
  await tabTo(page.getByRole('button', { name: 'Reveal samples and result' }))
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: 'Your most efficient acceptable setting' })).toBeFocused()
  await tabTo(page.getByRole('button', { name: 'Use this setting for the library' }))
  await page.keyboard.press('Enter')
  await expect(page.getByRole('status')).toHaveText('Setting applied — no work was queued')
})

test('enlarged text keeps the Original sample label inside its control', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openLab(page, 'Image', false, false, true)
  await page.locator('html').evaluate(html => html.style.fontSize = '200%')
  const original = page.getByRole('button', { name: 'Original reference', exact: true })
  const contained = await original.evaluate(button => {
    const label = button.querySelector('span')!
    const range = document.createRange()
    range.selectNodeContents(label)
    const bounds = button.getBoundingClientRect()
    return range.getBoundingClientRect().right <= bounds.right - parseFloat(getComputedStyle(button).paddingRight) + 1
  })
  expect(contained).toBe(true)
})

test.describe('quality check accessibility and capture acceptance', () => {
  let media: QualityCheckMedia
  test.beforeAll(async ({ browser }) => {
    const canvasPage = await browser.newPage()
    try { media = await fabricatedQualityMedia(canvasPage) }
    finally { await canvasPage.close() }
  })

  async function audit(page: Page, info: TestInfo, name: string) {
    const results = await new AxeBuilder({ page }).include('.quality-lab')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
    await info.attach(`${name}-accessibility`, { body: JSON.stringify(results, null, 2), contentType: 'application/json' })
    expect(results.violations).toEqual([])
    await expect.poll(() => page.evaluate(() => ({
      document: document.documentElement.scrollWidth - window.innerWidth,
      main: document.querySelector('main')!.scrollWidth - document.querySelector('main')!.clientWidth,
    }))).toEqual({ document: 0, main: 0 })
    const escapedControls = await page.locator('.quality-lab button:visible').evaluateAll(buttons => {
      const main = document.querySelector('main')!.getBoundingClientRect()
      return buttons.filter(button => {
        const bounds = button.getBoundingClientRect()
        return bounds.left < main.left - 1 || bounds.right > main.right + 1
      }).map(button => button.textContent)
    })
    expect(escapedControls).toEqual([])
    const directory = process.env.QUALITY_CAPTURE_DIR
    if (directory) {
      await mkdir(directory, { recursive: true })
      // Expand only capture height so a nested scrolling main does not clip the bottom of
      // the element screenshot. Width, text scale and the tested layout stay unchanged.
      const viewport = page.viewportSize()!
      const height = await page.locator('main').evaluate(main => main.scrollHeight + window.innerHeight - main.clientHeight + 2)
      await page.setViewportSize({ width: viewport.width, height: Math.max(viewport.height, height) })
      await page.locator('.quality-lab').screenshot({ path: join(directory, `${name}.png`), animations: 'disabled' })
      await page.setViewportSize(viewport)
      await writeFile(join(directory, `${name}-accessibility.json`), JSON.stringify(results, null, 2))
    }
  }

  for (const kind of ['Video', 'Audio', 'Image'] as const) for (const theme of ['dark', 'light'] as const) {
    test(`${kind} ${theme}: source, comparison and result at desktop, narrow and enlarged text`, async ({ page }, info) => {
      test.setTimeout(150_000)
      const errors: string[] = []
      page.on('pageerror', error => errors.push(error.message))
      await page.addInitScript(value => localStorage.setItem('optimisarr.theme', value), theme)
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await page.setViewportSize({ width: 1440, height: 1400 })
      await mockApp(page, kind)
      await serveQualityMedia(page, kind, media)
      await page.goto('/#/libraries/1/quality-check')
      await expect(page.getByRole('combobox')).toBeVisible()
      // This fixture deliberately compares different presets; request that opt-in path.
      if (kind === 'Video') await page.getByRole('checkbox', { name: /Compare presets/ }).check()
      const prefix = `${kind.toLowerCase()}-${theme}`
      await page.getByRole('button', { name: 'Prepare blind samples' }).hover()
      await audit(page, info, `${prefix}-setup`)
      await page.getByRole('button', { name: 'Prepare blind samples' }).click()
      await expect(page.getByRole('heading', { name: 'Original', exact: true })).toBeVisible()
      await expect(page.getByText('Stream diagnostics', { exact: true })).toHaveCount(0)
      if (kind === 'Video') await expect.poll(() => page.locator('video').evaluate(video => video.readyState)).toBeGreaterThanOrEqual(2)
      if (kind !== 'Image') await page.getByRole('button', { name: kind === 'Audio' ? 'Excerpt 1' : 'Scene 1', exact: true }).hover()
      await audit(page, info, `${prefix}-compare`)
      await page.setViewportSize({ width: 390, height: 844 })
      await audit(page, info, `${prefix}-compare-narrow`)
      await page.setViewportSize({ width: 1440, height: 1400 })
      for (const name of namesFor(kind).slice(1)) {
        await page.getByRole('button', { name, exact: true }).click()
        await page.getByRole('group', { name: `Rate sample ${name}` }).getByRole('button', { name: /Acceptable/ }).click()
      }
      await page.getByRole('button', { name: 'Reveal samples and result' }).click()
      await expect(page.getByRole('heading', { name: 'Your most efficient acceptable setting' })).toBeVisible()
      await page.getByRole('button', { name: 'Use this setting for the library' }).hover()
      await audit(page, info, `${prefix}-results`)
      // Root text enlargement exercises wrapping separately from the 720 CSS-pixel viewport
      // equivalent of a 1440px browser at 200% zoom. Neither asserts native browser chrome.
      await page.locator('html').evaluate(html => html.style.fontSize = '200%')
      await audit(page, info, `${prefix}-results-text-200`)
      await page.locator('html').evaluate(html => html.style.fontSize = '')
      await page.setViewportSize({ width: 720, height: 700 })
      await audit(page, info, `${prefix}-results-zoom-200`)
      expect(errors).toEqual([])
    })
  }
})
