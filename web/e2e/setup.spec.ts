import { expect, test, type Page, type Route } from '@playwright/test'

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
  vmafTier: 'Off',
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
  return {
    version: 1,
    completedStep: currentStep - 1,
    currentStep,
    stepCount: 5,
    completed: false,
  }
}

async function mockSetup(page: Page, currentStep = 5, configuredLibrary: typeof library | null = library) {
  let readinessCalls = 0
  let completed = false
  let currentLibraries = configuredLibrary ? [{ ...configuredLibrary }] : []
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup' && route.request().method() === 'GET') return json(route, completed ? { ...setupState(5), completedStep: 5, completed: true } : setupState(currentStep))
    if (path === '/api/setup/readiness') {
      readinessCalls += 1
      return json(route, {
        databaseAvailable: true,
        ready: true,
        platform: 'compose',
        paths: [{
          name: 'Config', role: 'config', libraryId: null, path: '/config', exists: true,
          readable: true, writable: true, issue: 'none', fileSystemId: 'dev', mountId: '1',
          mountPoint: '/', fileSystemType: 'ext4', availableBytes: 100_000_000_000,
          totalBytes: 200_000_000_000, requiredFreeBytes: null,
        }],
        storageRelationships: [],
        tools: [
          { name: 'FFmpeg', command: 'ffmpeg', available: true, required: true, version: '7.1', error: null },
          { name: 'ffprobe', command: 'ffprobe', available: true, required: true, version: '7.1', error: null },
          { name: 'FFmpeg (VMAF)', command: 'ffmpeg', available: true, required: false, version: 'libvmaf', error: null },
        ],
        recommendation,
      })
    }
    if (path === '/api/system/hardware') return json(route, {
      hardware: {
        hardwareAccelerators: ['qsv'],
        encoders: [{ name: 'hevc_qsv', codec: 'hevc', mode: 'Intel QSV', available: true }],
        nvidiaRuntimeAvailable: false,
        driDeviceAvailable: true,
        error: null,
      },
    })
    if (path === '/api/libraries') {
      if (route.request().method() === 'POST') {
        const created = { ...library, ...route.request().postDataJSON(), id: currentLibraries.length + 1 }
        currentLibraries.push(created)
        return json(route, created)
      }
      return json(route, currentLibraries)
    }
    if (path === '/api/libraries/1' && route.request().method() === 'PUT') {
      currentLibraries[0] = { ...currentLibraries[0], ...route.request().postDataJSON(), id: 1 }
      return json(route, currentLibraries[0])
    }
    if (path === '/api/fs/browse') {
      const folder = url.searchParams.get('path') || '/'
      return json(route, folder === '/'
        ? { path: '/', parent: null, directories: [{ name: 'media', path: '/media' }] }
        : { path: folder, parent: '/', directories: [] })
    }
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [
        { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'RemuxCleanup', codec: null, container: 'mkv', crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'TrackCleanup', codec: null, container: null, crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
      ],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'],
      videoCodecs: ['h264', 'hevc', 'av1'],
      containers: ['mp4', 'mkv'],
      encoderPresets: ['quick', 'balanced', 'efficient'],
      legacyEncoderPresets: ['veryslow'],
      imageFormats: ['webp'],
    })
    if (path === '/api/candidates/summary' || path === '/api/candidates' || path === '/api/exclusions') {
      return json(route, [])
    }
    if (path === '/api/libraries/1/access') return json(route, {
      path: currentLibraries[0]?.path ?? '/media/films', exists: true, readable: true, writable: true, ok: true,
      message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/',
      fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000,
      atomicWithWork: true, atomicWithQuarantine: true,
    })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/inventory') return json(route, {
      items: [], total: 0, counts: { all: 0, eligible: 0, skipped: 0, unprobed: 0 },
    })
    if (path === '/api/setup/progress') {
      currentStep = Math.min(route.request().postDataJSON().completedStep + 1, 5)
      return json(route, setupState(currentStep))
    }
    if (path === '/api/setup/apply') {
      completed = true
      return json(route, {
        state: { ...setupState(5), completedStep: 5, completed: true },
        libraryCount: currentLibraries.length,
        settingsApplied: true,
        recommendationsApplied: true,
        alreadyApplied: false,
      })
    }
    if (path === '/api/jobs' || path === '/api/jobs/failures') return json(route, [])
    if (path === '/api/queue/status') return json(route, { runningJobs: 0, suspendedEncodeCount: 0, canStart: true })
    return route.fulfill({ status: 404, body: '{}' })
  })
  return () => readinessCalls
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

for (const locale of ['en', 'de', 'es', 'fr', 'it', 'ja', 'pt', 'ru', 'zh']) {
  test(`${locale} review fits a 390px viewport with usable controls`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await page.addInitScript(({ locale, dark }) => {
      localStorage.setItem('optimisarr:locale', locale)
      localStorage.setItem('optimisarr.theme', dark ? 'dark' : 'light')
    }, { locale, dark: locale === 'ja' || locale === 'zh' })
    await mockSetup(page)
    await page.goto('/')

    await expect(page.locator('#setup-content h1')).toBeVisible()
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const undersized = await page.locator('#setup-content button:visible').evaluateAll((buttons) =>
      buttons.filter((button) => button.getBoundingClientRect().height < 44).length)
    expect(undersized).toBe(0)
  })
}

test('review Change actions preserve the plan and keyboard focus order', async ({ page }) => {
  await mockSetup(page)
  await page.goto('/')

  const change = page.getByRole('button', { name: 'Change' }).first()
  await expect.poll(async () => {
    await change.focus()
    return change.evaluate(element => element === document.activeElement)
  }).toBe(true)
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: /Set up your libraries/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Back' })).toBeDisabled()
})

test('review reports the saved per-library VMAF policy when no recommendation override is selected', async ({ page }) => {
  await mockSetup(page, 5, {
    ...library,
    videoQualityStrategy: 'AdaptiveVmaf',
    vmafQualityGateEnabled: true,
    minVmafHarmonicMean: 93,
    minVmafMin: 80,
    minVmafCatastrophicMin: 50,
    clipVmafEnabled: true,
    vmafFrameSubsample: 1,
  })
  await page.goto('/')

  const vmafRow = page.getByText('Perceptual quality (VMAF)').locator('..')
  await expect(vmafRow).toContainText('Enabled')
})

test('translated setup opens a localised, touch-friendly library editor without changing API values', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.addInitScript(() => {
    localStorage.setItem('optimisarr:locale', 'de')
  })
  await mockSetup(page, 3)
  await page.goto('/')

  await page.getByRole('button', { name: 'Konfigurieren' }).click()
  await expect(page.locator('[data-config-section]')).toHaveCount(2)
  await expect.poll(() => page.evaluate(() => document.documentElement.lang)).toBe('de')

  const mediaType = page.locator('#lib-type')
  await expect(mediaType.locator('option')).toHaveText(['Film', 'Fernsehen', 'Musik', 'Fotos', 'Sonstige'])
  expect(await mediaType.locator('option').evaluateAll((options) =>
    options.map((option) => option.getAttribute('value'))),
  ).toEqual(['Film', 'TV', 'Music', 'Photo', 'Other'])
  await expect(mediaType).toHaveValue('Film')

  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  const undersized = await page.locator('#setup-content button:visible').evaluateAll((buttons) =>
    buttons
      .map((button) => {
        const { width, height } = button.getBoundingClientRect()
        return { label: button.getAttribute('aria-label') ?? button.textContent?.trim(), width, height }
      })
      .filter(({ width, height }) => width < 44 || height < 44))
  expect(undersized).toEqual([])
})

test('invalid safety input has an inline error and focuses the summary', async ({ page }) => {
  await mockSetup(page, 4)
  await page.goto('/')

  const concurrency = page.getByRole('spinbutton', { name: 'Concurrent jobs' })
  await concurrency.fill('0')
  await page.getByRole('button', { name: 'Continue' }).click()

  await expect(concurrency).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByRole('alert')).toBeFocused()
  await expect(page.locator('#setup-concurrency-error')).toBeVisible()
})

test('system re-test announces completion without moving focus', async ({ page }) => {
  const readinessCalls = await mockSetup(page, 2)
  await page.goto('/')

  const retest = page.getByRole('button', { name: 'Re-test system' })
  await retest.focus()
  await retest.click()
  await expect(page.getByText('System evidence refreshed.')).toBeVisible()
  await expect.poll(readinessCalls).toBe(2)
})

test('final apply sends one reviewed plan and shows a no-work-started receipt', async ({ page }) => {
  await mockSetup(page)
  const settingsWrites: string[] = []
  page.on('request', (request) => {
    if (request.url().endsWith('/api/settings') && request.method() !== 'GET') {
      settingsWrites.push(request.method())
    }
  })
  await page.goto('/')

  const requestPromise = page.waitForRequest((request) =>
    request.url().endsWith('/api/setup/apply') && request.method() === 'POST')
  await page.getByRole('button', { name: 'Finish setup' }).click()
  const request = await requestPromise

  expect(request.postDataJSON()).toMatchObject({
    settings: { dryRunMode: true, maxConcurrentJobs: 1 },
    useRecommendedEncoder: false,
  })
  expect(settingsWrites).toEqual([])
  await expect(page.getByRole('heading', { name: 'Setup is safely applied' })).toBeVisible()
  await expect(page.getByText(/No scan, encode, replacement, or deletion was started/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Review candidates' })).toBeVisible()
})

test('review remains usable at the WCAG 400% reflow equivalent and in landscape', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' })
  await page.setViewportSize({ width: 320, height: 640 })
  await mockSetup(page)
  await page.goto('/')

  await expect(page.locator('#setup-content h1')).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)

  await page.setViewportSize({ width: 844, height: 390 })
  await expect(page.locator('#setup-content h1')).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})


test('setup offers theme and language controls and focuses each new step', async ({ page }) => {
  await mockSetup(page, 1)
  await page.goto('/')
  const theme = page.getByRole('button', { name: 'Toggle theme', exact: true })
  await expect(theme).toBeVisible()
  await expect(theme.locator('svg path')).toHaveAttribute('d', /.+/)
  const before = await page.locator('html').getAttribute('class')
  await theme.click()
  await expect.poll(() => page.locator('html').getAttribute('class')).not.toBe(before)
  await expect(theme.locator('svg path')).toHaveAttribute('d', /.+/)
  await expect(page.getByRole('button', { name: 'Language: English' })).toBeVisible()
  await page.getByRole('button', { name: 'Continue', exact: true }).click()
  await expect(page.locator('#setup-content h1')).toHaveText('Check the media toolchain')
  await expect(page.locator('#setup-content h1')).toBeFocused()
})

test('a failed initial readiness request can retry without advancing or losing the wizard', async ({ page }) => {
  await mockSetup(page, 5)
  let failures = 1
  await page.route('**/api/setup/readiness', route => failures-- > 0
    ? route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Readiness unavailable' }) })
    : route.fallback())
  await page.goto('/')
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Finish setup' })).toBeDisabled()
  await page.getByRole('button', { name: 'Try again', exact: true }).click()
  await expect(page.getByRole('alert')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Finish setup' })).toBeEnabled()
})

test('fractional concurrency cannot reach final review', async ({ page }) => {
  await mockSetup(page, 4)
  await page.goto('/')
  await page.getByRole('spinbutton', { name: 'Concurrent jobs' }).fill('1.5')
  await page.getByRole('button', { name: 'Continue', exact: true }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('spinbutton', { name: 'Concurrent jobs' })).toHaveAttribute('aria-invalid', 'true')
})

test('embedded library configuration never offers an unreachable quality-check page', async ({ page }) => {
  await mockSetup(page, 3)
  await page.goto('/')
  await page.getByRole('button', { name: 'Configure', exact: true }).click()
  await page.getByRole('navigation', { name: 'Processing workflow' }).getByRole('button', { name: /Encode/ }).click()
  await page.getByRole('button', { name: 'Video settings', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Personal quality check', exact: true })).toHaveCount(0)
})

test('re-running setup reports saved automation and schedules accurately', async ({ page }) => {
  await mockSetup(page, 5, { ...library, autoEnqueueEnabled: true, autoReplace: true, autoEnqueueWindowStart: '02:00', autoEnqueueWindowEnd: '07:00' })
  await page.goto('/')
  await expect(page.locator('#setup-content')).toContainText('02:00–07:00')
  await expect(page.locator('#setup-content')).toContainText('auto-replace')
  await expect(page.getByText('Skipped — add later', { exact: true })).toHaveCount(0)
})


test('folder picker closes with Escape and never selects a stale folder after a navigation failure', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 })
  await mockSetup(page, 3, null)
  await page.goto('/')
  await page.getByRole('button', { name: 'Create library', exact: true }).click()
  const browse = page.getByRole('button', { name: 'Browse', exact: true }).first()
  await browse.click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(browse).toBeFocused()
  await browse.click()
  await expect(dialog).toHaveAccessibleName('Choose a folder')
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.route('**/api/fs/browse?path=*', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Folder unavailable' }) }))
  await dialog.getByRole('button', { name: 'media', exact: true }).click()
  await expect(dialog.getByRole('alert')).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Select folder', exact: true })).toBeDisabled()
})

for (const theme of ['light', 'dark']) {
  test(`fresh setup completes all steps in ${theme} mode, preserves a draft on reload and exits the receipt`, async ({ page }) => {
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    await page.setViewportSize({ width: 390, height: 844 })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), theme)
    await mockSetup(page, 1, null)
    await page.goto('/')
    const next = page.getByRole('button', { name: 'Continue', exact: true })
    await next.click()
    await expect(page.locator('#setup-content h1')).toHaveText('Check the media toolchain')
    await next.click()
    await expect(next).toBeDisabled()
    await page.getByRole('button', { name: 'Create library', exact: true }).click()
    await page.locator('#lib-name').fill('Family films')
    await page.getByRole('button', { name: 'Browse', exact: true }).first().click()
    await page.getByRole('dialog').getByRole('button', { name: 'media', exact: true }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Select folder', exact: true }).click()
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    await expect(page.getByRole('article', { name: 'Family films' })).toContainText('/media')
    await next.click()
    await page.getByRole('spinbutton', { name: 'Concurrent jobs' }).fill('2')
    await page.reload()
    await expect(page.getByRole('spinbutton', { name: 'Concurrent jobs' })).toHaveValue('2')
    await next.click()
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const applied = page.waitForRequest(request => request.url().endsWith('/api/setup/apply'))
    await page.getByRole('button', { name: 'Finish setup', exact: true }).click()
    expect((await applied).postDataJSON().settings.maxConcurrentJobs).toBe(2)
    await expect(page.getByRole('heading', { name: 'Setup is safely applied' })).toBeVisible()
    await page.getByRole('button', { name: 'Review candidates', exact: true }).click()
    await expect(page).toHaveURL(/#\/inventory$/)
    await expect(page.getByRole('heading', { name: 'Inventory', exact: true })).toBeVisible()
    expect(errors).toEqual([])
  })
}

test('system checks let a missing library path reach the editor, but library access still blocks progress', async ({ page }) => {
  await mockSetup(page, 2)
  await page.route('**/api/setup/readiness', async route => {
    await json(route, {
      databaseAvailable: true, ready: false, platform: 'compose', recommendation,
      paths: [
        { name: 'Config', role: 'config', path: '/config', issue: 'none', exists: true, readable: true, writable: true, availableBytes: null, totalBytes: null },
        { name: 'Films', role: 'library', libraryId: 1, path: '/media/films', issue: 'missing', exists: false, readable: false, writable: false, availableBytes: null, totalBytes: null },
      ],
      storageRelationships: [],
      tools: [{ name: 'FFmpeg', command: 'ffmpeg', available: true, required: true, version: '7.1', error: null }],
    })
  })
  await page.route('**/api/libraries/1/access', route => json(route, { path: '/media/films', exists: false, readable: false, writable: false, ok: false, message: 'Library folder is missing', issue: 'missing', availableBytes: null, atomicWithWork: null, atomicWithQuarantine: null }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Continue', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Configure', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Continue', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Library folder is missing')
  await expect(page.getByRole('button', { name: 'Configure', exact: true })).toBeVisible()
})

test('a failed apply keeps the reviewed plan editable and can be retried', async ({ page }) => {
  await mockSetup(page, 5)
  let failures = 1
  await page.route('**/api/setup/apply', route => failures-- > 0
    ? route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Save unavailable' }) })
    : route.fallback())
  await page.goto('/')
  await page.getByRole('button', { name: 'Finish setup', exact: true }).click()
  await expect(page.getByRole('alert')).toBeFocused()
  await expect(page.getByRole('button', { name: 'Finish setup', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: 'Finish setup', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Setup is safely applied' })).toBeVisible()
})

test('restricted draft storage cannot break setup or completion', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.addInitScript(() => {
    for (const method of ['getItem', 'setItem', 'removeItem'] as const) {
      const original = Storage.prototype[method]
      Storage.prototype[method] = function (key: string, value?: string) {
        if (key === 'optimisarr.setup.plan.v1') throw new DOMException('Storage denied', 'SecurityError')
        return original.call(this, key, value!)
      }
    }
  })
  await mockSetup(page, 5)
  await page.goto('/')
  await page.getByRole('button', { name: 'Finish setup', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Setup is safely applied' })).toBeVisible()
  expect(errors).toEqual([])
})
