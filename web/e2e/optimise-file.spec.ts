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
  await expect(queue).toHaveAccessibleDescription(/the library’s rules skip this file/)
  await expect(page.getByRole('button', { name: 'Preview', exact: true })).toBeDisabled()
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
