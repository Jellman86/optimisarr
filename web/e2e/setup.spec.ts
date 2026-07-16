import { expect, test, type Page, type Route } from '@playwright/test'

const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  verificationDurationTolerancePercent: 1,
  verificationRequireAudioRetained: true,
  verificationRequireSubtitlesRetained: true,
  verificationRequireSizeReduction: true,
  verificationAudioLoudnessGateEnabled: false,
  verificationMaxLoudnessDriftLufs: 1,
  verificationAudioClippingGateEnabled: false,
  verificationMaxTruePeakDbtp: -1,
  verificationImageQualityGateEnabled: true,
  verificationMinimumImageSsim: 0.95,
  verificationImageMetadataGateEnabled: true,
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 0,
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

async function mockSetup(page: Page, currentStep = 5) {
  let readinessCalls = 0
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup' && route.request().method() === 'GET') return json(route, setupState(currentStep))
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
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/libraries/1/access') return json(route, {
      path: library.path, exists: true, readable: true, writable: true, ok: true,
      message: 'ready', issue: 'none', fileSystemId: 'dev', mountId: '1', mountPoint: '/',
      fileSystemType: 'ext4', availableBytes: 100_000_000_000, totalBytes: 200_000_000_000,
      atomicWithWork: true, atomicWithQuarantine: true,
    })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/inventory') return json(route, {
      items: [], total: 0, counts: { all: 0, eligible: 0, skipped: 0, unprobed: 0 },
    })
    if (path === '/api/setup/progress') return json(route, setupState(Math.min(currentStep + 1, 5)))
    if (path === '/api/setup/apply') return json(route, {
      state: { ...setupState(5), completedStep: 5, completed: true },
      libraryCount: 1,
      settingsApplied: true,
      recommendationsApplied: true,
      alreadyApplied: false,
    })
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
  await change.focus()
  await expect(change).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: /Set up your libraries/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Back' })).toBeDisabled()
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
