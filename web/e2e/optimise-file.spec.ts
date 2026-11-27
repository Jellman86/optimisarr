import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Film', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  normalizeOpusChannelLayout: true,
  keepAudioLanguages: 'eng', keepSubtitleLanguages: null, reencodeLossyAudio: false,
  targetImageFormat: null, imageQuality: null, reencodeLossyImages: false,
  imageDownscaleMode: 'None', imageDownscaleValue: 0, moveOnComplete: false,
  targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: 93, minVmafMin: null,
  vmafQualityGateEnabled: true, minVmafCatastrophicMin: null, clipVmafEnabled: null,
  vmafFrameSubsample: null, durationTolerancePercent: 1, requireAudioRetained: true,
  requireSubtitlesRetained: false, requireSizeReduction: true,
  minimumSizeSavingPercent: null, maximumSizeSavingPercent: null,
  audioLoudnessGateEnabled: false, maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false, maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true, minimumImageSsim: 0.95, imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, videoQualityStrategy: 'AdaptiveVmaf',
  lastAutoEnqueueAt: null, fileCount: 2,
}

const source = {
  relativePath: 'Orbit (2025)/Orbit.2025.2160p.x264.mkv', sizeBytes: 42_800_000_000, container: 'matroska',
  videoCodec: 'h264', width: 3840, height: 2160, durationSeconds: 9330, isHdr: false, isDolbyVision: false,
  interlaced: false, audioCodecs: 'truehd, aac', maxAudioChannels: 8, audioTrackCount: 2, subtitleTrackCount: 2,
}

const plan = {
  mediaFileId: 7, libraryId: 1, eligible: true, reason: 'Video codec differs from the library’s HEVC target.',
  kind: 'Video', action: 'Encode', source,
  picture: { codec: 'hevc', crf: 24, width: null, height: null, shrinkToHeight: null, frameRateCap: null, tonemapToSdr: false, deinterlace: false, cropBlackBars: false, noiseReduction: 'Off', upscales: false },
  image: null,
  sound: { codec: 'aac', bitrateKbps: 160, downmixToStereo: false },
  audioTracks: [{ index: 0, language: 'eng', kept: true }, { index: 1, language: 'fra', kept: false }],
  subtitleTracks: [{ index: 0, language: 'eng', kept: true }, { index: 1, language: 'fra', kept: true }],
  file: { container: 'mp4', fileName: 'Orbit.2025.2160p.x265.mp4', renamed: true, mayBecomeMatroska: true },
}

const skipped = {
  ...plan, mediaFileId: 8, eligible: false, reason: 'Already uses the target codec.',
  source: { ...source, relativePath: 'Arrival/Arrival.2016.1080p.HEVC.mkv', videoCodec: 'hevc' },
}

type Overrides = { plan?: unknown; library?: Record<string, unknown>; settings?: Record<string, unknown> }

async function mockPlan(page: Page, overrides: Overrides = {}) {
  const json = (route: Route, body: unknown) => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [{ ...library, ...overrides.library }])
    if (path === '/api/settings') return json(route, { dryRunMode: false, replacementQuarantineRetentionDays: 30, ...overrides.settings })
    if (path === '/api/library-options') return json(route, {
      ruleProfileSpecs: [{ profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false }],
    })
    if (path === '/api/media/7/plan') return json(route, overrides.plan ?? plan)
    if (path === '/api/media/8/plan') return json(route, skipped)
    if (path === '/api/jobs') return json(route, [])
    if (path === '/api/queue/status') return json(route, { runningJobs: 0 })
    if (path === '/api/health') return json(route, { status: 'healthy', version: 'test' })
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    return json(route, {})
  })
}

const row = (page: Page, name: string) => page.locator(`[data-plan-row="${name}"]`)

test('a music file that is only trimmed keeps its format in the plan', async ({ page }) => {
  await mockPlan(page, { library: { mediaType: 'Music', trimAudioSilence: true, trimSilenceWithoutConverting: true }, plan: {
    ...plan, kind: 'Audio', action: 'Audio', picture: null, image: null, audioTracks: null, subtitleTracks: null,
    source: { ...source, relativePath: 'Album/01 Track.mp3', videoCodec: null, width: null, height: null, audioCodecs: 'mp3' },
    sound: { codec: 'mp3', bitrateKbps: null, downmixToStereo: false, trimsSilence: true, trimOnly: true },
    file: { container: 'mp3', fileName: '01 Track.mp3', renamed: false, mayBecomeMatroska: false },
  } })
  await page.goto('/#/inventory/optimise/7')
  const audio = page.locator('[data-plan-row="audio"]')

  await expect(audio).toContainText('Stays MP3. Only silence at the start and end is trimmed, if the job finds any.')
  await expect(audio).not.toContainText('Converted to')
  await expect(audio.locator('[data-plan-tag]')).toContainText('Shortens files')
})

test('a music plan says silence at the edges is trimmed when the library trims it', async ({ page }) => {
  await mockPlan(page, { library: { mediaType: 'Music', trimAudioSilence: true }, plan: {
    ...plan, kind: 'Audio', action: 'Encode', picture: null, image: null, audioTracks: null, subtitleTracks: null,
    source: { ...source, relativePath: 'Album/01 Track.flac', videoCodec: null, width: null, height: null, audioCodecs: 'flac' },
    sound: { codec: 'opus', bitrateKbps: 128, downmixToStereo: false, trimsSilence: true },
    file: { container: 'opus', fileName: '01 Track.opus', renamed: true, mayBecomeMatroska: false },
  } })
  await page.goto('/#/inventory/optimise/7')
  const audio = page.locator('[data-plan-row="audio"]')

  await expect(audio).toContainText('Silence at the start and end is trimmed if the job finds any.')
  await expect(audio.locator('[data-plan-tag]')).toContainText('Shortens files')
})

test('the plan says what would change, part by part, and opening it queues nothing', async ({ page }) => {
  await mockPlan(page)
  const writes: string[] = []
  page.on('request', request => { if (request.method() !== 'GET' && request.url().includes('/api/')) writes.push(request.url()) })
  await page.goto('/#/inventory/optimise/7')

  await expect(page.getByRole('heading', { level: 1, name: 'Optimise this file' })).toBeVisible()
  await expect(page.locator('[data-plan-verdict]')).toHaveAttribute('data-plan-verdict', 'yes')
  await expect(page.locator('[data-plan-verdict]')).toContainText('Video codec differs')

  await expect(row(page, 'video').getByRole('cell').first()).toContainText('H.264 · 3840 × 2160')
  await expect(row(page, 'video').getByRole('cell').last()).toContainText('Re-encodes video to HEVC (H.265).')
  await expect(row(page, 'video')).toContainText('The quality is chosen for this file by measuring, to reach 93')
  await expect(row(page, 'video')).toContainText('Picture size, frame rate and colour are kept.')
  await expect(row(page, 'video').locator('[data-plan-tag]')).toHaveCount(0)

  await expect(row(page, 'audio')).toContainText('Converted to AAC at 160 kbps.')
  await expect(row(page, 'audio')).toContainText('Removed: FRA.')
  await expect(row(page, 'audio').locator('[data-plan-tag]')).toHaveText('Removes tracks')
  await expect(row(page, 'subtitles')).toContainText('Every track is kept.')

  await expect(row(page, 'file')).toContainText('Renamed to Orbit.2025.2160p.x265.mp4.')
  await expect(row(page, 'file').locator('[data-plan-tag]')).toHaveText('Renames files')
  await expect(row(page, 'file')).toContainText('If its subtitles are pictures, as on a Blu-ray or DVD, it is saved as MKV instead')

  await expect(row(page, 'size')).toContainText('Not known until it’s encoded.')
  await expect(row(page, 'size')).toContainText('It must come out smaller, or the original is kept.')
  await expect(row(page, 'size')).toContainText('Preview encodes a short part')
  await expect(row(page, 'checks')).not.toBeEmpty()
  expect(writes).toEqual([])
})

test('a picture change is tagged where it is described', async ({ page }) => {
  await mockPlan(page, { plan: { ...plan, picture: { ...plan.picture, width: 1920, height: 1080, shrinkToHeight: 1080, tonemapToSdr: true } } })
  await page.goto('/#/inventory/optimise/7')

  await expect(row(page, 'video')).toContainText('Made smaller, to 1920 × 1080.')
  await expect(row(page, 'video')).toContainText('Converts HDR to SDR.')
  await expect(row(page, 'video').locator('[data-plan-tag]')).toHaveText('Changes the picture')
})

test('a keyframe interval is named in the plan without tagging a picture change', async ({ page }) => {
  await mockPlan(page, { plan: { ...plan, picture: { ...plan.picture, keyframeIntervalSeconds: 10 } } })
  await page.goto('/#/inventory/optimise/7')

  await expect(row(page, 'video')).toContainText('Keyframes no more than 10 s apart.')
  await expect(row(page, 'video').locator('[data-plan-tag]')).toHaveCount(0)
})

test('what only the job can measure is said as a rule, not as a fact about this file', async ({ page }) => {
  // With black bars still to be measured, the server sends the height limit and no exact size.
  await mockPlan(page, { plan: { ...plan, picture: { ...plan.picture, shrinkToHeight: 1080, frameRateCap: 30, cropBlackBars: true } } })
  await page.goto('/#/inventory/optimise/7')
  const video = row(page, 'video')

  await expect(video).toContainText('Shrinks video taller than 1080p down to 1080p.')
  await expect(video).not.toContainText('Made smaller, to')
  await expect(video).toContainText('Halves frame rates above 30 fps.')
  await expect(video).toContainText('Black bars are cut off, if any are found.')
})

test('upscaling is shown only when the server says this job would be upscaled, and its sound is copied', async ({ page }) => {
  await mockPlan(page, { library: { superResolutionEnabled: true } })
  await page.goto('/#/inventory/optimise/7')
  await expect(row(page, 'video')).toContainText('Re-encodes video')
  await expect(row(page, 'video')).not.toContainText('Upscales with AI')

  await mockPlan(page, { library: { superResolutionEnabled: true }, plan: { ...plan, picture: { ...plan.picture, upscales: true }, sound: { codec: null, bitrateKbps: null, downmixToStereo: false } } })
  await page.reload()

  await expect(row(page, 'video')).toContainText('Upscales with AI before the checks.')
  await expect(row(page, 'audio')).toContainText('AI upscaling copies the sound as it is.')
  await expect(row(page, 'audio')).not.toContainText('Converted to')
})

test('an image plan says its format, any resize and when the image is compressed a second time', async ({ page }) => {
  const photo = {
    ...plan, kind: 'Image', action: 'Image', picture: null, sound: null, audioTracks: null, subtitleTracks: null,
    source: { ...source, relativePath: 'Trip/IMG_0002.jpg', container: 'image2', videoCodec: 'mjpeg', width: 4000, height: 3000, durationSeconds: null, audioCodecs: null, audioTrackCount: null, subtitleTrackCount: null },
    file: { container: 'webp', fileName: 'IMG_0002.webp', renamed: true, mayBecomeMatroska: false },
    image: { format: 'webp', quality: 70, resizeMode: 'MaxLongEdge', resizeValue: 3840, recompressesLossy: true },
  }
  await mockPlan(page, { plan: photo, library: { mediaType: 'Photo' } })
  await page.goto('/#/inventory/optimise/7')
  const image = row(page, 'image')

  await expect(image).toContainText('Converted to WebP at quality 70.')
  await expect(image).toContainText('Shrinks images to fit 3840 px on the long edge.')
  await expect(image).toContainText('This image is already compressed, so it loses a little quality a second time.')
  await expect(image.locator('[data-plan-tag]')).toHaveText('Changes the picture')
  await expect(row(page, 'file')).toContainText('Renamed to IMG_0002.webp.')
})

test('a slow answer for one file does not show, or leave loading, on the file opened since', async ({ page }) => {
  await mockPlan(page)
  let fail = () => {}
  const asked = new Promise<void>(resolve => {
    void page.route('**/api/media/7/plan', async route => {
      resolve()
      await new Promise<void>(release => { fail = release })
      await route.fulfill({ status: 500, json: { error: 'The first file could not be planned.' } })
    })
  })
  await page.goto('/#/inventory/optimise/7')
  await asked
  await page.goto('/#/inventory/optimise/8')
  await expect(page.locator('[data-plan-verdict]')).toHaveAttribute('data-plan-verdict', 'no')
  const answered = page.waitForResponse('**/api/media/7/plan')

  fail()
  await answered

  await expect(page.locator('[data-one-file-plan]')).toBeVisible()
  await expect(page.locator('main')).not.toContainText('The first file could not be planned.')
})

test('a fixed quality setting says whether the result is measured', async ({ page }) => {
  await mockPlan(page, { library: { videoQualityStrategy: 'Fixed', vmafQualityGateEnabled: false } })
  await page.goto('/#/inventory/optimise/7')

  await expect(row(page, 'video')).toContainText('Fixed quality setting 24. Picture quality is not measured.')
})

test('Queue this file queues it once and points at the queue', async ({ page }) => {
  await mockPlan(page)
  let posts = 0
  await page.route('**/api/media/7/enqueue', route => {
    posts += 1
    return route.fulfill({ json: { jobId: 41, alreadyQueued: false } })
  })
  await page.goto('/#/inventory/optimise/7')
  const queue = page.getByRole('button', { name: 'Queue this file', exact: true })
  await expect(queue).toHaveAccessibleDescription('Queueing adds this one file. The encode does not touch your original.')

  await queue.click()

  const queued = page.locator('[data-plan-queued]')
  await expect(queued).toContainText('Added to the queue')
  await expect(queue).toBeDisabled()
  expect(posts).toBe(1)
  await queued.getByRole('link', { name: 'Open the queue' }).click()
  await expect(page).toHaveURL(/#\/queue$/)
})

test('a file already in the queue says so, and a refusal shows the server reason', async ({ page }) => {
  await mockPlan(page)
  let refuse = false
  await page.route('**/api/media/7/enqueue', route => refuse
    ? route.fulfill({ status: 409, json: { code: 'media.enqueue.needsProbe', error: 'This file has changed since it was scanned. Scan and probe it again.' } })
    : route.fulfill({ json: { jobId: 41, alreadyQueued: true } }))
  await page.goto('/#/inventory/optimise/7')
  await page.getByRole('button', { name: 'Queue this file', exact: true }).click()
  await expect(page.locator('[data-plan-queued]')).toContainText('already in the queue')

  refuse = true
  await page.reload()
  await page.getByRole('button', { name: 'Queue this file', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('changed since it was scanned')
  await expect(page.getByRole('button', { name: 'Queue this file', exact: true })).toBeEnabled()
})

test('a file the rules skip cannot be queued or previewed and says where the rule is', async ({ page }) => {
  await mockPlan(page)
  await page.goto('/#/inventory/optimise/8')

  const verdict = page.locator('[data-plan-verdict]')
  await expect(verdict).toHaveAttribute('data-plan-verdict', 'no')
  await expect(verdict).toContainText('Already uses the target codec.')
  await expect(verdict.getByRole('link', { name: 'Films → Which files' })).toHaveAttribute('href', '#/libraries/1/configure/files')
  const queue = page.getByRole('button', { name: 'Queue this file', exact: true })
  await expect(queue).toBeDisabled()
  await expect(queue).toHaveAccessibleDescription('Queue and Preview are off because this file is skipped. The reason is above.')
  await expect(page.getByRole('button', { name: 'Preview', exact: true })).toBeDisabled()
  await expect(page.locator('[data-plan-try-again]')).toHaveCount(0)
})

test('a file held back by its last failure is tried again from the plan, through that job', async ({ page }) => {
  await mockPlan(page, { plan: { ...plan, eligible: false, reason: 'Failed last time, so it isn’t tried again by itself', retryJobId: 55 } })
  const retried: string[] = []
  let enqueued = 0
  await page.route('**/api/jobs/55/retry*', route => {
    retried.push(new URL(route.request().url()).pathname)
    return route.fulfill({ json: { id: 55, status: 'Queued' } })
  })
  await page.route('**/api/media/7/enqueue', route => { enqueued += 1; return route.fulfill({ json: { jobId: 41, alreadyQueued: false } }) })
  await page.goto('/#/inventory/optimise/7')

  await expect(page.getByRole('button', { name: 'Queue this file', exact: true })).toHaveCount(0)
  const tryAgain = page.getByRole('button', { name: 'Try again', exact: true })
  await expect(tryAgain).toHaveAccessibleDescription(/It failed last time.*Try again puts it back in the queue/)
  await expect(page.getByRole('link', { name: 'See why it failed' })).toHaveAttribute('href', '#/queue/failures')
  await expect(page.getByRole('link', { name: 'Films → Which files' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Preview', exact: true })).toHaveCount(0)

  await tryAgain.click()

  await expect(page.locator('[data-plan-queued]')).toContainText('Added to the queue')
  await expect(tryAgain).toBeDisabled()
  await expect(page.getByRole('link', { name: 'See why it failed' })).toHaveCount(0)
  expect(retried).toEqual(['/api/jobs/55/retry'])
  expect(enqueued).toBe(0)
})

test('what happens to the original follows the library’s files choice', async ({ page }) => {
  await mockPlan(page, { settings: { dryRunMode: true } })
  await page.goto('/#/inventory/optimise/7')
  const original = page.locator('[data-plan-original]')

  await expect(original).toHaveAttribute('data-plan-original', 'ask')
  await expect(original).toContainText('A file that passes waits for you, then replaces the original.')
  await expect(original).toContainText('Moves to Quarantine.')
  await expect(original.locator('[data-plan-dry-run]')).toContainText('Replacing is locked for now (dry-run).')
  await expect(original.getByRole('link', { name: 'Open the settings of Films' })).toHaveAttribute('href', '#/libraries/1/configure')
})

test('a library that saves elsewhere says the original is not touched, and the dry-run lock is not mentioned', async ({ page }) => {
  await mockPlan(page, { settings: { dryRunMode: true }, library: { moveOnComplete: true, targetFolder: '/optimised/films' } })
  await page.goto('/#/inventory/optimise/7')
  const original = page.locator('[data-plan-original]')

  await expect(original).toHaveAttribute('data-plan-original', 'elsewhere')
  await expect(original).toContainText('A file that passes is saved under /optimised/films. The original is not touched.')
  await expect(original.locator('[data-plan-dry-run]')).toHaveCount(0)
})

test('a slow queue answer does not appear on the file opened since', async ({ page }) => {
  await mockPlan(page)
  let answer = () => {}
  const asked = new Promise<void>(resolve => {
    void page.route('**/api/media/7/enqueue', async route => {
      resolve()
      await new Promise<void>(release => { answer = release })
      await route.fulfill({ json: { jobId: 41, alreadyQueued: false } })
    })
  })
  await page.goto('/#/inventory/optimise/7')
  await page.getByRole('button', { name: 'Queue this file', exact: true }).click()
  await asked
  await page.goto('/#/inventory/optimise/8')
  await expect(page.locator('[data-plan-verdict]')).toHaveAttribute('data-plan-verdict', 'no')
  const answered = page.waitForResponse('**/api/media/7/enqueue')

  answer()
  await answered

  await expect(page.locator('[data-plan-queued]')).toHaveCount(0)
})

test('a plan that cannot be worked out shows why and can be retried', async ({ page }) => {
  await mockPlan(page)
  let fail = true
  await page.route('**/api/media/7/plan', route => fail
    ? route.fulfill({ status: 409, json: { code: 'media.enqueue.needsProbe', error: 'Probe this file first.' } })
    : route.fallback())
  await page.goto('/#/inventory/optimise/7')
  await expect(page.locator('main')).toContainText('Probe this file first.')

  fail = false
  await page.getByRole('button', { name: 'Try again', exact: true }).click()
  await expect(page.locator('[data-one-file-plan]')).toBeVisible()
})

test('the plan stacks on a phone, labels each side and has no accessibility violations', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 800 })
  await mockPlan(page, { settings: { dryRunMode: true } })
  await page.goto('/#/inventory/optimise/7')
  await expect(page.locator('[data-one-file-plan]')).toBeVisible()

  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1)
  const before = await row(page, 'video').getByRole('cell').first().evaluate(cell => getComputedStyle(cell, '::before').content)
  expect(before).toContain('Now')
  for (const name of ['Queue this file', 'Preview']) {
    const box = await page.getByRole('button', { name, exact: true }).boundingBox()
    expect(box!.height).toBeGreaterThanOrEqual(44)
  }
  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations).toEqual([])
})

const cleanupPlan = {
  ...plan, action: 'TrackCleanup', picture: null, sound: { codec: null, bitrateKbps: null, downmixToStereo: false },
  file: { container: 'mkv', fileName: 'Orbit.2025.2160p.x264.mkv', renamed: false, mayBecomeMatroska: false },
  source: { ...source, sizeBytes: 60 * 1024 ** 3 },
}
const cleanupLibrary = { ruleProfile: 'TrackCleanup', minimumTrackRemovalSavingPercent: 5 }

test('a track cleanup plan below the minimum says how much it would save and that the file is skipped', async ({ page }) => {
  await mockPlan(page, { library: cleanupLibrary, plan: {
    ...cleanupPlan, eligible: false,
    reason: "Removing these tracks would save about 120 MB (0.19%), below this library's minimum of 5%",
    trackSaving: { bytes: 120 * 1024 ** 2, percent: 0.195, minimumPercent: 5, minimumBytes: null, meetsMinimum: false },
  } })
  await page.goto('/#/inventory/optimise/7')
  const size = row(page, 'size')

  await expect(size).toContainText("Removing the tracks saves about 120 MB (0.19%). That is below this library's minimum of 5%, so this file is skipped.")
  // A skipped file never runs, so what a run must achieve isn't said about it.
  await expect(size).not.toContainText('Preview encodes a short part')
  await expect(size.locator('[data-plan-tag]')).toContainText('Skipped')
  await expect(size).not.toContainText('Not known until')
})

test('a track cleanup plan that meets the minimum says so', async ({ page }) => {
  await mockPlan(page, { library: { ...cleanupLibrary, minimumTrackRemovalSavingBytes: 1024 ** 3 }, plan: {
    ...cleanupPlan,
    trackSaving: { bytes: 6 * 1024 ** 3, percent: 10, minimumPercent: 5, minimumBytes: 1024 ** 3, meetsMinimum: true },
  } })
  await page.goto('/#/inventory/optimise/7')
  const size = row(page, 'size')

  await expect(size).toContainText("Removing the tracks saves about 6.0 GB (10%). That meets this library's minimum of 5% and 1.0 GB.")
  await expect(size.locator('[data-plan-tag]')).toHaveCount(0)
})

test('a track cleanup plan whose saving cannot be known says the minimum cannot be checked', async ({ page }) => {
  await mockPlan(page, { library: cleanupLibrary, plan: {
    ...cleanupPlan, eligible: false,
    trackSaving: { bytes: null, percent: null, minimumPercent: 5, minimumBytes: null, meetsMinimum: null, needsReprobe: false },
  } })
  await page.goto('/#/inventory/optimise/7')

  await expect(row(page, 'size')).toContainText("The size of the tracks to remove isn't known, so this library's minimum of 5% can't be checked and this file is skipped.")
  await expect(row(page, 'size')).not.toContainText('Re-probe')
})

test('a track cleanup plan for a file probed before track sizes were recorded asks for a re-probe', async ({ page }) => {
  await mockPlan(page, { library: cleanupLibrary, plan: {
    ...cleanupPlan, eligible: false,
    trackSaving: { bytes: null, percent: null, minimumPercent: 5, minimumBytes: null, meetsMinimum: null, needsReprobe: true },
  } })
  await page.goto('/#/inventory/optimise/7')
  const size = row(page, 'size')

  await expect(size).toContainText("This file was probed before track sizes were recorded, so this library's minimum of 5% can't be checked yet and the file is skipped. Re-probe it to check.")
  await expect(size.locator('[data-plan-tag]')).toContainText('Skipped')
  await expect(size).not.toContainText("isn't known")
})

test('a saving just below the minimum never reads as reaching it', async ({ page }) => {
  // 4.999% would round to 5%, and 1023.97 MB to 1024 MB; both are short of 5% and 1 GB.
  await mockPlan(page, { library: { ...cleanupLibrary, minimumTrackRemovalSavingBytes: 1024 ** 3 }, plan: {
    ...cleanupPlan, eligible: false,
    trackSaving: { bytes: 1024 ** 3 - 30 * 1024, percent: 4.999, minimumPercent: 5, minimumBytes: 1024 ** 3, meetsMinimum: false },
  } })
  await page.goto('/#/inventory/optimise/7')

  await expect(row(page, 'size')).toContainText("Removing the tracks saves about 1023 MB (4.9%). That is below this library's minimum of 5% and 1.0 GB")
})

test('a track cleanup plan without a minimum gives the saving and judges nothing', async ({ page }) => {
  await mockPlan(page, { library: { ruleProfile: 'TrackCleanup' }, plan: {
    ...cleanupPlan,
    trackSaving: { bytes: 6 * 1024 ** 3, percent: 10, minimumPercent: null, minimumBytes: null, meetsMinimum: null },
  } })
  await page.goto('/#/inventory/optimise/7')
  const size = row(page, 'size')

  await expect(size).toContainText('Removing the tracks saves about 6.0 GB (10%).')
  await expect(size).not.toContainText('minimum')
})

test('the track saving fits a phone without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 800 })
  await mockPlan(page, { library: cleanupLibrary, plan: {
    ...cleanupPlan, eligible: false,
    trackSaving: { bytes: 120 * 1024 ** 2, percent: 0.195, minimumPercent: 5, minimumBytes: null, meetsMinimum: false },
  } })
  await page.goto('/#/inventory/optimise/7')
  await expect(row(page, 'size')).toContainText('so this file is skipped')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
})
