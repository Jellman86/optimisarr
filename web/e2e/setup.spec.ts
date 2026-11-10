import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 0,
  remoteWorkersEnabled: false,
}

const recommendation = {
  encoderMode: 'IntelQsv',
  hardwareDecode: true,
  vmafTier: 'Keep',
  scheduleStart: '01:00',
  scheduleEnd: '06:00',
  encoderReason: 'intel',
  vmafReason: 'cpu-cost',
}

const library = {
  id: 1,
  name: 'Films',
  path: '/media/films',
  mediaType: 'Film',
  ruleProfile: 'ConservativeHevc',
  enabled: true,
  priority: 0,
  minFileSizeBytes: null,
  maxHeight: null,
  reencodeSameCodecAboveBytes: null,
  skipEfficientSources: true,
  targetVideoCodec: null,
  targetContainer: null,
  hdrHandling: null,
  optimiseDolbyVision: false,
  excludePaths: null,
  qualityCrf: null,
  encoderPreset: null,
  audioTargetCodec: null,
  audioBitrateKbps: null,
  videoAudioCodec: null,
  videoAudioBitrateKbps: null,
  downmixToStereo: false,
  keepAudioLanguages: null,
  keepSubtitleLanguages: null,
  reencodeLossyAudio: false,
  targetImageFormat: null,
  imageQuality: null,
  reencodeLossyImages: false,
  imageDownscaleMode: 'None',
  imageDownscaleValue: 0,
  moveOnComplete: false,
  targetFolder: null,
  moveOverwrite: false,
  minVmafHarmonicMean: null,
  minVmafMin: null,
  vmafQualityGateEnabled: false,
  minVmafCatastrophicMin: null,
  clipVmafEnabled: null,
  vmafFrameSubsample: null,
  durationTolerancePercent: 1,
  requireAudioRetained: true,
  requireSubtitlesRetained: false,
  requireSizeReduction: true,
  audioLoudnessGateEnabled: false,
  maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false,
  maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true,
  minimumImageSsim: 0.95,
  imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false,
  autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00',
  autoReplace: false,
  videoQualityStrategy: 'Fixed',
  lastAutoEnqueueAt: null,
  fileCount: 0,
}

function setupState(currentStep = 5) {
  return { version: 1, completedStep: currentStep - 1, currentStep, stepCount: 5, completed: false }
}

const readyPath = (name: string, role: string, path: string) => ({
  name, role, libraryId: null, path, exists: true, readable: true, writable: true, issue: 'none',
  fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4',
  availableBytes: 100_000_000_000, totalBytes: 200_000_000_000, requiredFreeBytes: null,
})

const eligibleFile = (id: number, name: string, sizeBytes: number) => ({
  id, libraryId: 1, relativePath: `${name}/${name}.mkv`, sizeBytes, status: 'Probed', mediaKind: 'Video', container: 'matroska',
  videoCodec: 'h264', width: 1920, height: 1080, durationSeconds: 3000, audioCodecs: 'aac', audioLanguages: 'eng',
  audioTrackCount: 1, subtitleTrackCount: 0, probedAt: '2026-11-10T10:00:00Z', probeError: null, optimisedMarker: null,
})
const eligibleFiles = [eligibleFile(11, 'Small', 600_000_000), eligibleFile(12, 'Typical', 3_400_000_000), eligibleFile(13, 'Large', 8_900_000_000)]

type Mock = {
  currentStep?: number
  libraries?: (typeof library)[]
  recommended?: Partial<typeof recommendation>
  readiness?: Record<string, unknown>
  access?: Record<string, unknown>
  settings?: Record<string, unknown>
  inventory?: { files?: ReturnType<typeof eligibleFile>[]; all?: number; unprobed?: number }
  arrs?: unknown[]
  watchers?: unknown[]
}

async function mockSetup(page: Page, mock: Mock = {}) {
  let currentStep = mock.currentStep ?? 5
  let completed = false
  const calls = { readiness: 0, scans: [] as number[], created: [] as Record<string, unknown>[], updated: [] as Record<string, unknown>[], applied: [] as Record<string, unknown>[] }
  const currentLibraries = (mock.libraries ?? [library]).map((item) => ({ ...item }))
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    const method = route.request().method()
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup' && method === 'GET') return json(route, completed ? { ...setupState(5), completedStep: 5, completed: true } : setupState(currentStep))
    if (path === '/api/setup/readiness') {
      calls.readiness += 1
      return json(route, {
        databaseAvailable: true, ready: true, platform: 'compose',
        paths: [readyPath('Config', 'config', '/config'), readyPath('Work', 'work', '/work'), readyPath('Quarantine', 'quarantine', '/trash')],
        storageRelationships: currentLibraries.map((item) => ({ libraryId: item.id, libraryName: item.name, workAtomic: true, quarantineAtomic: true })),
        tools: [
          { name: 'FFmpeg', command: 'ffmpeg', available: true, required: true, version: '7.1', error: null },
          { name: 'ffprobe', command: 'ffprobe', available: true, required: true, version: '7.1', error: null },
          { name: 'FFmpeg (VMAF)', command: 'ffmpeg', available: true, required: false, version: 'libvmaf', error: null },
        ],
        recommendation: { ...recommendation, ...mock.recommended },
        ...mock.readiness,
      })
    }
    if (path === '/api/system/hardware') return json(route, {
      hardware: { hardwareAccelerators: ['qsv'], encoders: [{ name: 'hevc_qsv', codec: 'hevc', mode: 'Intel QSV', available: true }], nvidiaRuntimeAvailable: false, driDeviceAvailable: true, error: null },
    })
    if (path === '/api/libraries') {
      if (method === 'POST') {
        const body = route.request().postDataJSON()
        calls.created.push(body)
        const created = { ...library, ...body, id: currentLibraries.length + 1 }
        currentLibraries.push(created)
        return json(route, created)
      }
      return json(route, currentLibraries)
    }
    const scan = path.match(/^\/api\/libraries\/(\d+)\/scan$/)
    if (scan) {
      calls.scans.push(Number(scan[1]))
      return json(route, { discovered: 12, added: 12, updated: 0, skippedUnsettled: 0 })
    }
    const saved = path.match(/^\/api\/libraries\/(\d+)$/)
    if (saved && method === 'PUT') {
      const body = route.request().postDataJSON()
      calls.updated.push(body)
      const index = currentLibraries.findIndex((item) => item.id === Number(saved[1]))
      currentLibraries[index] = { ...currentLibraries[index], ...body, id: Number(saved[1]) }
      return json(route, currentLibraries[index])
    }
    if (/^\/api\/libraries\/\d+\/access$/.test(path)) return json(route, {
      path: '/media/films', exists: true, readable: true, writable: true, ok: true, message: 'ready', issue: 'none',
      fileSystemId: 'dev', mountId: '1', mountPoint: '/', fileSystemType: 'ext4', availableBytes: 100_000_000_000,
      totalBytes: 200_000_000_000, atomicWithWork: true, atomicWithQuarantine: true, ...mock.access,
    })
    if (path === '/api/libraries/target-folder-check') {
      const folder = String(route.request().postDataJSON().path)
      return json(route, folder.startsWith('/media/films')
        ? { path: folder, allowed: false, reason: 'insideLibrary', libraryName: 'Films', message: 'inside', availableBytes: null }
        : { path: folder, allowed: true, reason: 'none', libraryName: null, message: 'ok', availableBytes: 1_500_000_000_000 })
    }
    if (path === '/api/fs/browse') {
      const folder = url.searchParams.get('path') || '/'
      return json(route, folder === '/'
        ? { path: '/', parent: null, directories: [{ name: 'media', path: '/media' }, { name: 'optimised', path: '/optimised' }] }
        : folder === '/media'
          ? { path: '/media', parent: '/', directories: [{ name: 'films', path: '/media/films' }] }
          : { path: folder, parent: '/', directories: [] })
    }
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [
        { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
      ],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'], videoCodecs: ['h264', 'hevc', 'av1'], containers: ['mp4', 'mkv'],
      encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: ['veryslow'], imageFormats: ['webp'],
    })
    if (path === '/api/settings') return json(route, { ...settings, ...mock.settings })
    if (path === '/api/settings/cleanup') return json(route, { retentionDays: 0, dryRunMode: true, failedOutputCount: 0, failedOutputBytes: 0, quarantinedOriginalCount: 0, quarantinedOriginalBytes: 0, planToken: 'setup', totalCount: 0, totalBytes: 0 })
    if (path === '/api/arr-connections') return json(route, mock.arrs ?? [])
    if (path === '/api/activity-watchers') return json(route, mock.watchers ?? [])
    if (path === '/api/arr-connections/tracking' || path === '/api/notification-targets' || path === '/api/workers') return json(route, [])
    if (path === '/api/inventory') {
      const files = mock.inventory?.files ?? []
      return json(route, {
        items: files.map((file) => ({ file, eligible: true, reason: 'Video qualifies.' })), total: files.length,
        counts: { all: mock.inventory?.all ?? files.length, eligible: files.length, skipped: 0, unprobed: mock.inventory?.unprobed ?? 0 },
      })
    }
    if (path === '/api/media/12/plan') return json(route, {
      mediaFileId: 12, libraryId: 1, eligible: true, reason: 'Video qualifies.', kind: 'Video', action: 'Encode',
      source: { relativePath: 'Typical/Typical.mkv', sizeBytes: 3_400_000_000, container: 'matroska', videoCodec: 'h264', width: 1920, height: 1080, durationSeconds: 3000, isHdr: false, isDolbyVision: false, interlaced: false, audioCodecs: 'aac', maxAudioChannels: 2, audioTrackCount: 1, subtitleTrackCount: 0 },
      picture: { codec: 'hevc', crf: 24, width: null, height: null, frameRateCap: null, tonemapToSdr: false, deinterlace: false, cropBlackBars: false, noiseReduction: 'Off' },
      sound: { codec: 'aac', bitrateKbps: 160, downmixToStereo: false }, audioTracks: null, subtitleTracks: null,
      file: { container: 'mp4', fileName: 'Typical.mp4', renamed: true, mayBecomeMatroska: true },
    })
    if (path.endsWith('/thumbnail')) return route.fulfill({ status: 404 })
    if (path === '/api/setup/progress') {
      currentStep = Math.min(route.request().postDataJSON().completedStep + 1, 5)
      return json(route, setupState(currentStep))
    }
    if (path === '/api/setup/apply') {
      calls.applied.push(route.request().postDataJSON())
      completed = true
      return json(route, { state: { ...setupState(5), completedStep: 5, completed: true }, libraryCount: currentLibraries.length, settingsApplied: true, recommendationsApplied: true, alreadyApplied: false })
    }
    if (path === '/api/jobs' || path === '/api/jobs/failures') return json(route, [])
    if (path === '/api/queue/status') return json(route, { runningJobs: 0, suspendedEncodeCount: 0, canStart: true })
    if (path === '/api/health') return json(route, { status: 'healthy', version: 'test' })
    return route.fulfill({ status: 404, body: '{}' })
  })
  return calls
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
}

const heading = (page: Page) => page.locator('#setup-content h1')
const next = (page: Page) => page.getByRole('button', { name: 'Continue', exact: true })
const check = (page: Page, key: string) => page.locator(`[data-setup-check="${key}"]`)

for (const locale of ['en', 'de', 'es', 'fr', 'it', 'ja', 'pt', 'ru', 'zh']) {
  test(`${locale} last step fits a 390px viewport with usable controls`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await page.addInitScript(({ locale, dark }) => {
      localStorage.setItem('optimisarr:locale', locale)
      localStorage.setItem('optimisarr.theme', dark ? 'dark' : 'light')
    }, { locale, dark: locale === 'ja' || locale === 'zh' })
    await mockSetup(page, { inventory: { files: eligibleFiles } })
    await page.goto('/')

    await expect(heading(page)).toBeVisible()
    await expect(page.locator('[data-setup-suggestions] article')).toHaveCount(3)
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const undersized = await page.locator('#setup-content button:visible').evaluateAll((buttons) =>
      buttons.filter((button) => button.getBoundingClientRect().height < 44).length)
    expect(undersized).toBe(0)
  })
}

test('the five steps are named for what each one asks', async ({ page }) => {
  await mockSetup(page, { currentStep: 1 })
  await page.goto('/')

  await expect(page.getByRole('list', { name: 'Setup progress' }).getByRole('listitem')).toHaveCount(5)
  for (const name of ['This machine', 'Your library', 'Your other apps', 'Your files', 'Try one file']) {
    await expect(page.getByRole('list', { name: 'Setup progress' })).toContainText(name)
  }
  await expect(heading(page)).toHaveText('Checking this machine')
})

test('this machine says in plain words what was tested and keeps the evidence closed', async ({ page }) => {
  await mockSetup(page, { currentStep: 1 })
  await page.goto('/')

  await expect(check(page, 'tools')).toContainText('FFmpeg and ffprobe are installed and run.')
  await expect(check(page, 'tools')).toHaveAttribute('data-setup-check-state', 'ready')
  await expect(check(page, 'path-work-0')).toContainText('Each job needs room for one extra copy of the file while it runs.')
  await expect(check(page, 'replace-1')).toContainText('a replacement is a rename, not a copy')
  await expect(check(page, 'replace-1')).toHaveAttribute('data-setup-check-state', 'fast')
  await expect(check(page, 'encoder')).toContainText('An Intel Quick Sync encoder passed the test on this machine. It is much faster than the CPU.')
  // Evidence such as filesystem ids is the proof behind a tick, not the first thing to read.
  await expect(page.locator('[data-setup-details]')).not.toHaveAttribute('open', '')
  await expect(page.getByText('filesystem dev', { exact: false }).first()).toBeHidden()
  await page.getByText('Technical details', { exact: true }).click()
  await expect(page.getByText('ext4 · filesystem dev · mount 1', { exact: false }).first()).toBeVisible()
  await expect(page.locator('#setup-content')).toContainText('Keep access private.')
  await expect(next(page)).toBeEnabled()
  await expect(page.locator('[data-setup-blocked]')).toHaveCount(0)
})

test('the tested hardware encoder is pre-selected, CPU is the other choice, and setup applies what was chosen', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 1 })
  await page.goto('/')
  const encoders = page.getByRole('group', { name: 'Encoder' })

  await expect(encoders.getByRole('button', { name: 'Intel Quick Sync (recommended)' })).toHaveAttribute('aria-pressed', 'true')
  await encoders.getByRole('button', { name: 'CPU only' }).click()
  await expect(encoders.getByRole('button', { name: 'CPU only' })).toHaveAttribute('aria-pressed', 'true')
  for (let step = 1; step < 5; step++) await page.getByRole('button', { name: /^(Continue|Skip for now)$/ }).click()
  await expect(heading(page)).toHaveText('Try one file first')
  await expect(page.locator('[data-setup-summary]')).toContainText('Encoder: CPU.')
  await page.getByRole('button', { name: 'Finish and open the dashboard' }).click()

  await expect.poll(() => calls.applied.length).toBe(1)
  expect(calls.applied[0].useRecommendedEncoder).toBe(false)
  expect((calls.applied[0].settings as Record<string, unknown>).encoderMode).toBe('Cpu')
})

test('with no hardware encoder there is nothing to choose and the CPU is named', async ({ page }) => {
  await mockSetup(page, { currentStep: 1, recommended: { encoderMode: 'Cpu', encoderReason: 'cpu', hardwareDecode: false } })
  await page.goto('/')

  await expect(check(page, 'encoder')).toContainText('No hardware encoder passed the test on this machine, so this uses the CPU.')
  await expect(page.getByRole('group', { name: 'Encoder' })).toHaveCount(0)
})

test('the picture quality check gets a sentence and never an offer to switch it off', async ({ page }) => {
  await mockSetup(page, { currentStep: 1 })
  await page.goto('/')

  await expect(check(page, 'quality')).toContainText('It runs on the CPU here, so it adds time to each film or episode. It stays on for new libraries')
  await expect(check(page, 'quality')).toHaveAttribute('data-setup-check-state', 'slower')
  await expect(page.locator('#setup-content').getByRole('checkbox')).toHaveCount(0)
})

test('a picture quality check that cannot run says so without blocking setup', async ({ page }) => {
  await mockSetup(page, { currentStep: 1, recommended: { vmafTier: 'Off', vmafReason: 'unavailable' } })
  await page.goto('/')

  await expect(check(page, 'quality')).toContainText('Not available. This FFmpeg was built without VMAF')
  await expect(next(page)).toBeEnabled()
})

test('a missing required tool blocks Continue, says why, and opens the details', async ({ page }) => {
  await mockSetup(page, { currentStep: 1, readiness: { tools: [
    { name: 'FFmpeg', command: 'ffmpeg', available: false, required: true, version: null, error: 'ffmpeg: not found' },
    { name: 'ffprobe', command: 'ffprobe', available: true, required: true, version: '7.1', error: null },
  ] } })
  await page.goto('/')

  await expect(check(page, 'tools')).toContainText('Missing: FFmpeg. Optimisarr cannot read or encode a file without them.')
  await expect(check(page, 'tools')).toContainText('Needs fixing')
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Continue is off until the items marked “Needs fixing” are fixed. Nothing has been changed on your disk.')
  await expect(page.locator('[data-setup-details]')).toHaveAttribute('open', '')
  await expect(page.getByText('ffmpeg: not found', { exact: false })).toBeVisible()
})

test('a working space that cannot be written shows the fix for the detected platform', async ({ page }) => {
  await mockSetup(page, { currentStep: 1, readiness: { platform: 'unraid', paths: [
    readyPath('Config', 'config', '/config'),
    { ...readyPath('Work', 'work', '/work'), writable: false, issue: 'unwritable' },
    readyPath('Quarantine', 'quarantine', '/trash'),
  ] } })
  await page.goto('/')

  await expect(check(page, 'path-work-0')).toContainText('the container user cannot create a file there')
  const fix = page.locator('[data-setup-fix]')
  await expect(fix.getByRole('button', { name: /Unraid/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(fix.getByRole('button', { name: /Unraid/ })).toContainText('detected')
  await fix.getByRole('button', { name: 'Docker Compose' }).click()
  await expect(fix).toContainText('docker compose up -d --force-recreate')
  await expect(next(page)).toBeDisabled()
})

test('testing again announces completion, keeps focus and keeps the encoder choice', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 1 })
  await page.goto('/')
  await page.getByRole('group', { name: 'Encoder' }).getByRole('button', { name: 'CPU only' }).click()
  const retest = page.getByRole('button', { name: 'Test again' })

  await retest.focus()
  await retest.click()

  await expect(page.getByText('Tested again just now.')).toBeVisible()
  await expect.poll(() => calls.readiness).toBe(2)
  await expect(page.getByRole('group', { name: 'Encoder' }).getByRole('button', { name: 'CPU only' })).toHaveAttribute('aria-pressed', 'true')
})

test('a library is three fields and one preset, summarised from what will be saved, and read at once', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 2, libraries: [] })
  await page.goto('/')

  await expect(heading(page)).toHaveText('Add a library')
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Continue is off until you add a library.')
  const form = page.locator('[data-setup-library-form]')
  await form.getByLabel('Name').fill('TV')
  await form.getByRole('group', { name: 'What’s in it' }).getByRole('button', { name: 'TV', exact: true }).click()
  await form.getByLabel('Folder').fill('/media/tv')
  await expect(form.getByRole('radio')).toHaveCount(3)
  await expect(form.getByRole('radio', { name: /^Balanced/ })).toBeChecked()
  const summary = form.locator('[data-library-summary]')
  await expect(summary).toContainText('With these choices, for each file in TV, Optimisarr will:')
  await expect(summary).not.toContainText('Edit opens every control')
  await expect(summary.locator('[data-summary-line="video"]')).toContainText('HEVC')
  await form.getByRole('radio', { name: /^Efficiency/ }).check()
  await expect(summary.locator('[data-summary-line="video"]')).toContainText('AV1')
  await expect(summary.locator('[data-summary-line="schedule"]')).toContainText('waits for you')

  await form.getByRole('button', { name: 'Add library', exact: true }).click()

  await expect(page.locator('[data-setup-library="1"]')).toContainText('/media/tv')
  await expect(page.locator('[data-setup-library="1"] [data-setup-scan]')).toHaveText('12 files found.')
  expect(calls.created).toHaveLength(1)
  expect(calls.created[0]).toMatchObject({ name: 'TV', path: '/media/tv', mediaType: 'TV', ruleProfile: 'ExperimentalAv1', autoEnqueueEnabled: false, autoReplace: false })
  expect(calls.scans).toEqual([1])
  await expect(page.locator('[data-setup-library-form]')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add another library' })).toBeVisible()
  await expect(next(page)).toBeEnabled()
})

test('a music library is not asked how its video should be made smaller', async ({ page }) => {
  await mockSetup(page, { currentStep: 2, libraries: [] })
  await page.goto('/')
  const form = page.locator('[data-setup-library-form]')

  await form.getByRole('group', { name: 'What’s in it' }).getByRole('button', { name: 'Music', exact: true }).click()

  await expect(form.getByRole('radio')).toHaveCount(0)
  await expect(form.locator('[data-library-summary] [data-summary-line="video"]')).toHaveCount(0)
})

test('an unfinished library form says what is missing and a refused save shows the server reason', async ({ page }) => {
  await mockSetup(page, { currentStep: 2, libraries: [] })
  await page.route('**/api/libraries', route => route.request().method() === 'POST'
    ? route.fulfill({ status: 400, json: { code: 'library.path.missing', error: 'That folder does not exist.' } })
    : route.fallback())
  await page.goto('/')
  const form = page.locator('[data-setup-library-form]')

  await form.getByRole('button', { name: 'Add library', exact: true }).click()
  await expect(form.getByRole('alert')).toHaveText('Give the library a name and a folder.')
  await form.getByLabel('Name').fill('Films')
  await form.getByLabel('Folder').fill('/nowhere')
  await form.getByRole('button', { name: 'Add library', exact: true }).click()

  await expect(form.getByRole('alert')).not.toHaveText('Give the library a name and a folder.')
  await expect(form.getByLabel('Name')).toHaveValue('Films')
})

test('Browse fills the folder, and Escape closes the picker without choosing', async ({ page }) => {
  await mockSetup(page, { currentStep: 2, libraries: [] })
  await page.goto('/')
  const form = page.locator('[data-setup-library-form]')

  await form.getByRole('button', { name: 'Browse' }).click()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(form.getByLabel('Folder')).toHaveValue('')

  await form.getByRole('button', { name: 'Browse' }).click()
  const picker = page.getByRole('dialog', { name: 'Choose a folder' })
  await picker.getByRole('button', { name: 'media', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()
  await expect(form.getByLabel('Folder')).toHaveValue('/media')
})

test('a failed look for files says so and can be tried again', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 2, libraries: [] })
  let failures = 1
  await page.route('**/api/libraries/1/scan', route => failures-- > 0
    ? route.fulfill({ status: 500, json: { error: 'The folder went away.' } })
    : route.fallback())
  await page.goto('/')
  const form = page.locator('[data-setup-library-form]')
  await form.getByLabel('Name').fill('Films')
  await form.getByLabel('Folder').fill('/media/films')
  await form.getByRole('button', { name: 'Add library', exact: true }).click()
  const saved = page.locator('[data-setup-library="1"]')

  await expect(saved.locator('[data-setup-scan="failed"]')).toContainText('Could not look for files: The folder went away.')
  await saved.getByRole('button', { name: 'Look again' }).click()

  await expect(saved.locator('[data-setup-scan="done"]')).toHaveText('12 files found.')
  expect(calls.scans).toEqual([1])
})

test('a library whose folder cannot be used blocks Continue with the reason and can be changed', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 2, access: { ok: false, writable: false, issue: 'unwritable', message: 'Optimisarr cannot write to /media/films.' } })
  await page.goto('/')
  const saved = page.locator('[data-setup-library="1"]')

  await expect(saved).toContainText('Optimisarr cannot write to /media/films.')
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Continue is off until every library’s folder can be read and written.')
  await saved.getByRole('button', { name: 'Change: Films' }).click()
  const form = page.locator('[data-setup-library-form]')
  await expect(form.getByLabel('Name')).toHaveValue('Films')
  await form.getByLabel('Folder').fill('/media/movies')
  await form.getByRole('button', { name: 'Save changes' }).click()

  await expect.poll(() => calls.updated.length).toBe(1)
  expect(calls.updated[0]).toMatchObject({ name: 'Films', path: '/media/movies' })
  expect(calls.created).toEqual([])
})

test('other apps are optional and each says what it prevents', async ({ page }) => {
  await mockSetup(page, { currentStep: 3 })
  await page.goto('/')

  await expect(heading(page)).toHaveText('Connect your other apps')
  const arr = page.locator('[data-setup-app="downloads"]')
  await expect(arr.getByRole('heading', { name: 'Sonarr and Radarr' })).toBeVisible()
  await expect(arr).toContainText('Holds a file back while Sonarr or Radarr is still importing it.')
  await expect(arr.locator('[data-setup-app-count]')).toHaveText('None connected')
  const servers = page.locator('[data-setup-app="servers"]')
  await expect(servers).toContainText('Pauses new jobs while someone is watching. Jobs already running carry on.')
  await expect(page.locator('#setup-content')).toContainText('Skipped apps can be added later in Settings → Download managers and Settings → Media servers.')
  await page.getByRole('button', { name: 'Skip for now', exact: true }).click()
  await expect(heading(page)).toHaveText('What happens to your files')
})

test('an app’s existing settings form opens inside the step, and connected apps are counted', async ({ page }) => {
  await mockSetup(page, { currentStep: 3, arrs: [{ id: 1, name: 'Sonarr', type: 'Sonarr', baseUrl: 'http://sonarr:8989', hasApiKey: true, enabled: true, createdAt: '2026-11-10T10:00:00Z', updatedAt: '2026-11-10T10:00:00Z' }] })
  await page.goto('/')
  const arr = page.locator('[data-setup-app="downloads"]')

  await expect(arr.locator('[data-setup-app-count]')).toHaveText('1 connected')
  await expect(next(page)).toBeVisible()
  await arr.getByRole('button', { name: 'Set up' }).click()

  await expect(arr.locator('[data-setup-app-form] #global-download-managers')).toBeVisible()
  await expect(arr.locator('[data-setup-app-form]')).toContainText('Sonarr')
  // The Settings frame stays out: no second page title, no way out of setup.
  await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1)
  await expect(page.getByRole('button', { name: 'All settings' })).toHaveCount(0)
  await arr.getByRole('button', { name: 'Hide the form' }).click()
  await expect(arr.locator('[data-setup-app-form]')).toHaveCount(0)
})

test('your files offers two outcomes, never automatic, and saves the choice with the library', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 4 })
  await page.goto('/')
  const files = page.locator('[data-setup-files="1"]')

  await expect(heading(page)).toHaveText('What happens to your files')
  await expect(files.getByRole('radio')).toHaveCount(2)
  await expect(files.getByRole('radio', { name: 'Replace the original automatically', exact: true })).toHaveCount(0)
  await expect(page.locator('#setup-content')).toContainText('Replacing automatically is not offered here.')
  await files.getByRole('radio', { name: /another folder/ }).check()
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Continue is off until the save folder is one Optimisarr can use.')
  await files.getByRole('button', { name: 'Browse', exact: true }).click()
  const picker = page.getByRole('dialog', { name: 'Choose a folder' })
  await picker.getByRole('button', { name: 'optimised', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()
  await expect(next(page)).toBeEnabled()

  await next(page).click()

  await expect(heading(page)).toHaveText('Try one file first')
  expect(calls.updated).toHaveLength(1)
  expect(calls.updated[0]).toMatchObject({ moveOnComplete: true, targetFolder: '/optimised', autoReplace: false })
  await expect(page.locator('[data-setup-summary]')).toContainText('Films: A file that passes is saved under /optimised. The original is not touched.')
})

test('a save folder inside the library is refused before Continue', async ({ page }) => {
  await mockSetup(page, { currentStep: 4 })
  await page.goto('/')
  const files = page.locator('[data-setup-files="1"]')

  await files.getByRole('radio', { name: /another folder/ }).check()
  await files.getByRole('button', { name: 'Browse', exact: true }).click()
  const picker = page.getByRole('dialog', { name: 'Choose a folder' })
  await picker.getByRole('button', { name: 'media', exact: true }).click()
  await picker.getByRole('button', { name: 'films', exact: true }).click()
  await picker.getByRole('button', { name: 'Select folder', exact: true }).click()

  await expect(files.locator('[data-target-folder-state="insideLibrary"]')).toContainText('This is inside the Films library.')
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Continue is off until the save folder is one Optimisarr can use.')
})

test('leaving the files answer alone saves nothing', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 4 })
  await page.goto('/')

  await next(page).click()

  await expect(heading(page)).toHaveText('Try one file first')
  expect(calls.updated).toEqual([])
})

test('originals are kept until approved or for a number of days, and the answer is what setup applies', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 4 })
  await page.goto('/')
  const keep = page.locator('[data-setup-keep]')

  await expect(keep.getByRole('radio', { name: /Until I approve each one/ })).toBeChecked()
  await expect(keep).toContainText('Once an original is deleted, that replacement can’t be undone. Quarantine is not a backup.')
  await keep.getByRole('radio', { name: 'For a set number of days, then delete them' }).check()
  await keep.getByRole('spinbutton', { name: 'Days' }).fill('0')
  await expect(next(page)).toBeDisabled()
  await expect(next(page)).toHaveAccessibleDescription('Enter a whole number, 0 or more.')
  await keep.getByRole('spinbutton', { name: 'Days' }).fill('45')
  await expect(page.locator('[data-setup-lock]').getByRole('checkbox')).toBeChecked()
  await next(page).click()
  await expect(page.locator('[data-setup-summary]')).toContainText('Originals stay in Quarantine for 45 days, then they are deleted. Replacing is locked for now (dry-run).')
  await page.getByRole('button', { name: 'Finish and open the dashboard' }).click()

  await expect.poll(() => calls.applied.length).toBe(1)
  expect(calls.applied[0]).toMatchObject({ applyRecommendedVmaf: false, applyRecommendedSchedule: false, useRecommendedEncoder: true })
  expect(calls.applied[0].settings).toMatchObject({ replacementQuarantineRetentionDays: 45, dryRunMode: true, maxConcurrentJobs: 1 })
})

test('the dry-run lock says where it is lifted and can be left off', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 4 })
  await page.goto('/')
  const lock = page.locator('[data-setup-lock]')

  await expect(lock).toContainText('You’ll be offered the switch on your first finished file.')
  await lock.getByRole('checkbox').uncheck()
  await next(page).click()
  await expect(page.locator('[data-setup-summary]')).not.toContainText('Replacing is locked')
  await page.getByRole('button', { name: 'Finish and open the dashboard' }).click()

  await expect.poll(() => calls.applied.length).toBe(1)
  expect(calls.applied[0].settings).toMatchObject({ dryRunMode: false, replacementQuarantineRetentionDays: 0 })
})

test('the last step suggests real files, and choosing one finishes setup on its plan', async ({ page }) => {
  const calls = await mockSetup(page, { inventory: { files: eligibleFiles, all: 40 } })
  await page.goto('/')

  await expect(heading(page)).toHaveText('Try one file first')
  await expect(page.locator('[data-setup-try-intro]')).toHaveText('Optimisarr has found 40 files. So far 3 of them can be made smaller. Pick one and see what you get. Nothing else will start.')
  const suggestions = page.locator('[data-setup-suggestions] article')
  await expect(suggestions).toHaveCount(3)
  await expect(page.getByRole('article', { name: 'A typical file' })).toContainText('Typical.mkv')
  await expect(page.getByRole('article', { name: 'A small file, quick to try' })).toContainText('Small.mkv')
  await expect(page.getByRole('article', { name: 'A large file' })).toContainText('Large.mkv')
  await expect(page.locator('#setup-content')).toContainText('Choosing a file finishes setup and opens its plan. Nothing is queued until you press Queue there.')
  expect(calls.applied).toEqual([])

  await page.getByRole('article', { name: 'A typical file' }).getByRole('button', { name: 'Optimise this file…' }).click()

  await expect(page).toHaveURL(/#\/inventory\/optimise\/12$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Optimise this file' })).toBeVisible()
  expect(calls.applied).toHaveLength(1)
})

test('the last step waits while files are still being read, then says when none can be made smaller', async ({ page }) => {
  await mockSetup(page, { inventory: { files: [], all: 9, unprobed: 9 } })
  await page.goto('/')

  await expect(page.locator('[data-setup-try-waiting]')).toHaveText('Still looking through your files. Suggestions appear here as they are read.')
  await expect(page.locator('#setup-content')).toContainText('Still reading 9 files in the background. This doesn’t change them.')
  await expect(page.getByRole('button', { name: 'Finish and open the dashboard' })).toBeEnabled()

  await page.route('**/api/inventory?**', route => route.fulfill({ json: { items: [], total: 0, counts: { all: 9, eligible: 0, skipped: 9, unprobed: 0 } } }))
  await expect(page.locator('[data-setup-try-none]')).toContainText('No file can be made smaller yet.', { timeout: 10_000 })
})

test('what you’ve set up is one line per step, and Change returns to that step with the plan kept', async ({ page }) => {
  await mockSetup(page, { inventory: { files: eligibleFiles } })
  await page.goto('/')
  const summary = page.locator('[data-setup-summary]')

  await expect(summary).toContainText('Encoder: Intel Quick Sync.')
  await expect(summary).toContainText('Films · Balanced · /media/films')
  await expect(summary).toContainText('No other apps connected.')
  await expect(summary).toContainText('Films: A file that passes waits for you, then replaces the original.')
  await expect(summary).toContainText('Optimising automatically: off.')
  await summary.getByRole('button', { name: 'Change' }).nth(3).click()
  await expect(heading(page)).toHaveText('What happens to your files')
  await expect(heading(page)).toBeFocused()
  await page.locator('[data-setup-lock]').getByRole('checkbox').uncheck()
  await next(page).click()

  await expect(heading(page)).toHaveText('Try one file first')
  await expect(summary).not.toContainText('Replacing is locked')
})

test('the summary counts the apps that are connected', async ({ page }) => {
  await mockSetup(page, { arrs: [{ id: 1, name: 'Sonarr', type: 'Sonarr', baseUrl: 'http://sonarr:8989', hasApiKey: true, enabled: true }], inventory: { files: eligibleFiles } })
  await page.goto('/')

  await expect(page.locator('[data-setup-summary]')).toContainText('Download managers connected: 1. Media servers connected: 0.')
})

test('re-running setup with a library that optimises by itself does not claim nothing else will start', async ({ page }) => {
  await mockSetup(page, { libraries: [{ ...library, autoEnqueueEnabled: true, autoReplace: true }], settings: { dryRunMode: false }, inventory: { files: eligibleFiles } })
  await page.goto('/')

  const intro = page.locator('[data-setup-try-intro]')
  await expect(intro).toContainText('Libraries set to optimise automatically can start encoding the files it finds during their schedule.')
  await expect(intro).toContainText('libraries set to replace automatically will also replace originals')
  await expect(intro).not.toContainText('Nothing else will start.')
  await expect(page.locator('[data-setup-summary]')).toContainText('Films: A file that passes replaces the original straight away.')
  await expect(page.locator('[data-setup-summary]')).not.toContainText('Optimising automatically: off.')
})

test('a library already set to replace automatically keeps that choice on the files step', async ({ page }) => {
  const calls = await mockSetup(page, { currentStep: 4, libraries: [{ ...library, autoReplace: true }] })
  await page.goto('/')
  const files = page.locator('[data-setup-files="1"]')

  await expect(files.getByRole('radio')).toHaveCount(3)
  await expect(files.getByRole('radio', { name: 'Replace the original automatically', exact: true })).toBeChecked()
  await next(page).click()
  expect(calls.updated).toEqual([])
})

test('setup offers theme and language controls and focuses each new step', async ({ page }) => {
  await mockSetup(page, { currentStep: 1 })
  await page.goto('/')
  const theme = page.getByRole('button', { name: 'Toggle theme', exact: true })
  await expect(theme).toBeVisible()
  const before = await page.locator('html').getAttribute('class')
  await theme.click()
  await expect.poll(() => page.locator('html').getAttribute('class')).not.toBe(before)
  await expect(page.getByRole('button', { name: 'Language: English' })).toBeVisible()
  await next(page).click()
  await expect(heading(page)).toHaveText('Add a library')
  await expect(heading(page)).toBeFocused()
  await page.getByRole('button', { name: 'Back', exact: true }).click()
  await expect(heading(page)).toHaveText('Checking this machine')
})

test('a failed first check can be retried without advancing or losing the wizard', async ({ page }) => {
  await mockSetup(page)
  let failures = 1
  await page.route('**/api/setup/readiness', route => failures-- > 0
    ? route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Readiness unavailable' }) })
    : route.fallback())
  await page.goto('/')
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Finish and open the dashboard' })).toBeDisabled()
  await page.getByRole('button', { name: 'Try again', exact: true }).click()
  await expect(page.getByRole('alert')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Finish and open the dashboard' })).toBeEnabled()
})

test('a finish the server refuses keeps the plan, says why, and can be retried', async ({ page }) => {
  const calls = await mockSetup(page)
  let failures = 1
  await page.route('**/api/setup/apply', route => failures-- > 0
    ? route.fulfill({ status: 400, json: { code: 'setup.tools.required', error: 'Setup cannot finish while a required tool is missing: FFmpeg.', args: { tools: 'FFmpeg' } } })
    : route.fallback())
  await page.goto('/')
  const finish = page.getByRole('button', { name: 'Finish and open the dashboard' })

  await finish.click()
  await expect(page.getByRole('alert')).toBeFocused()
  await expect(page.getByRole('alert')).toContainText('Setup cannot finish while a required tool is missing: FFmpeg.')
  await expect(finish).toBeEnabled()
  await finish.click()

  await expect(page.locator('#setup-content')).toHaveCount(0)
  expect(calls.applied).toHaveLength(1)
})

test('restricted draft storage cannot break setup or completion', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.addInitScript(() => {
    for (const method of ['getItem', 'setItem', 'removeItem'] as const) {
      const original = Storage.prototype[method]
      Storage.prototype[method] = function (key: string, value?: string) {
        if (key === 'optimisarr.setup.plan.v2') throw new DOMException('Storage denied', 'SecurityError')
        return original.call(this, key, value!)
      }
    }
  })
  await mockSetup(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Finish and open the dashboard' }).click()
  await expect(page.locator('#setup-content')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('answers survive a reload before setup is finished', async ({ page }) => {
  await mockSetup(page, { currentStep: 4 })
  await page.goto('/')
  await page.locator('[data-setup-keep]').getByRole('radio', { name: 'For a set number of days, then delete them' }).check()
  await page.locator('[data-setup-keep]').getByRole('spinbutton', { name: 'Days' }).fill('21')
  await page.locator('[data-setup-lock]').getByRole('checkbox').uncheck()

  await page.reload()

  await expect(page.locator('[data-setup-keep]').getByRole('spinbutton', { name: 'Days' })).toHaveValue('21')
  await expect(page.locator('[data-setup-lock]').getByRole('checkbox')).not.toBeChecked()
})

test('the last step remains usable at the WCAG 400% reflow equivalent and in landscape', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' })
  await page.setViewportSize({ width: 320, height: 640 })
  await mockSetup(page, { inventory: { files: eligibleFiles } })
  await page.goto('/')

  await expect(heading(page)).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)

  await page.setViewportSize({ width: 844, height: 390 })
  await expect(heading(page)).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

for (const step of [1, 2, 3, 4, 5]) {
  test(`step ${step} has no accessibility violations and fits a phone`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await mockSetup(page, { currentStep: step, libraries: step === 2 ? [] : [library], inventory: { files: eligibleFiles } })
    await page.goto('/')
    await expect(heading(page)).toBeVisible()
    await expect(page.locator('#setup-content')).toHaveAttribute('aria-busy', 'false')

    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const results = await new AxeBuilder({ page }).analyze()
    expect(results.violations).toEqual([])
  })
}
