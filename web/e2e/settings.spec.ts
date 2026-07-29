import { expect, test, type Page, type Route } from '@playwright/test'

const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  hdrToneMapMode: 'Software',
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 14,
}

const tools = [
  {
    name: 'FFmpeg',
    command: '/usr/lib/jellyfin-ffmpeg/ffmpeg',
    available: true,
    required: true,
    version: 'ffmpeg version 7.1.4-Jellyfin Copyright (c) 2000-2026 the FFmpeg developers',
    error: null,
  },
  {
    name: 'FFmpeg (VMAF)',
    command: '/usr/local/lib/optimisarr/ffmpeg-vmaf',
    available: true,
    required: false,
    version: 'libvmaf filter available',
    error: null,
  },
  {
    name: 'ffprobe',
    command: '/usr/lib/jellyfin-ffmpeg/ffprobe',
    available: true,
    required: true,
    version: 'ffprobe version 7.1.4-Jellyfin Copyright (c) 2007-2026 the FFmpeg developers',
    error: null,
  },
]

const hardware = {
  hardwareAccelerators: ['cuda', 'vaapi', 'qsv', 'drm', 'opencl', 'vulkan'],
  encoders: [
    { name: 'h264_qsv', codec: 'h264', mode: 'Intel QSV', available: true },
    { name: 'hevc_qsv', codec: 'hevc', mode: 'Intel QSV', available: true },
    { name: 'av1_qsv', codec: 'av1', mode: 'Intel QSV', available: false },
  ],
  nvidiaRuntimeAvailable: false,
  driDeviceAvailable: true,
  error: null,
}

const locales = ['en', 'de', 'es', 'fr', 'it', 'ja', 'pt', 'ru', 'zh']

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockSettings(page: Page) {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, {
      version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true,
    })
    if (path === '/api/health') return json(route, { status: 'healthy', service: 'optimisarr', version: 'test' })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/settings/cleanup') return json(route, {
      retentionDays: 14, dryRunMode: true, failedOutputCount: 2, failedOutputBytes: 2_147_483_648,
      quarantinedOriginalCount: 1, quarantinedOriginalBytes: 4_294_967_296,
      planToken: 'test', totalCount: 3, totalBytes: 6_442_450_944,
    })
    if (path === '/api/activity-watchers' || path === '/api/arr-connections' || path === '/api/notification-targets') {
      return json(route, [])
    }
    if (path === '/api/system/tools') return json(route, { tools })
    if (path === '/api/system/hardware') return json(route, { hardware })
    return json(route, {})
  })
}

test('global settings use the same logical section flow as library configuration', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings')

  const expectedSections = new Map([
    ['General', ['Queue', 'Replacement and cleanup']],
    ['Connections', ['Media servers', 'Download managers']],
    ['Notifications', ['Notifications']],
    ['Tools', ['Tools', 'Hardware acceleration', 'Encoders']],
    ['Backup', ['Backup & restore', 'First-run setup']],
  ])

  for (const [tab, headings] of expectedSections) {
    await page.getByRole('tab', { name: tab }).click()
    const sections = page.locator('[role="tabpanel"] [data-config-section]')
    await expect(sections.getByRole('heading', { level: 2 })).toHaveText(headings)
  }

  await expect(page.getByRole('button', { name: 'Run setup again' })).toBeVisible()

  await page.getByRole('tab', { name: 'General' }).click()
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: 'Connections' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('tabpanel')).toHaveAttribute('aria-labelledby', 'settings-tab-connections')
})

test('settings and tool capability cards stay within a small mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockSettings(page)
  await page.goto('/#/settings')
  await page.locator('html').evaluate((element) => {
    element.style.fontSize = '125%'
  })
  const toolsTab = page.getByRole('tab', { name: 'Tools' })
  await toolsTab.click()
  await expect(toolsTab).toBeInViewport()

  const fit = await page.locator('main').evaluate((main) => ({
    scrollWidth: main.scrollWidth,
    clientWidth: main.clientWidth,
  }))
  expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth)

  const cards = page.locator('[data-tool-card]')
  await expect(cards).toHaveCount(3)
  for (const card of await cards.all()) {
    const cardFit = await card.evaluate((element) => {
      const cardRect = element.getBoundingClientRect()
      const mainRect = element.closest('main')!.getBoundingClientRect()
      return {
        scrollWidth: element.scrollWidth,
        clientWidth: element.clientWidth,
        left: cardRect.left,
        right: cardRect.right,
        mainLeft: mainRect.left,
        mainRight: mainRect.right,
      }
    })
    expect(cardFit.scrollWidth).toBeLessThanOrEqual(cardFit.clientWidth)
    expect(cardFit.left).toBeGreaterThanOrEqual(cardFit.mainLeft)
    expect(cardFit.right).toBeLessThanOrEqual(cardFit.mainRight)
  }

  const refreshBox = await page.getByRole('button', { name: 'Refresh' }).boundingBox()
  expect(refreshBox?.height).toBeGreaterThanOrEqual(44)
})

test('every settings tab reflows without horizontal page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await mockSettings(page)
  await page.goto('/#/settings')

  for (const tab of ['General', 'Connections', 'Notifications', 'Tools', 'Backup']) {
    await page.getByRole('tab', { name: tab }).click()
    const fit = await page.locator('main').evaluate((main) => ({
      scrollWidth: main.scrollWidth,
      clientWidth: main.clientWidth,
    }))
    expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth)
  }
})

test('information tooltips are translated, populated, and readable in every locale', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockSettings(page)
  await page.goto('/#/settings')
  await page.locator('html').evaluate((element) => {
    element.style.fontSize = '125%'
  })

  for (const locale of locales) {
    await page.evaluate((code) => localStorage.setItem('optimisarr:locale', code), locale)
    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('lang', locale)

    for (const tabIndex of [0, 1]) {
      await page.getByRole('tab').nth(tabIndex).click()
      const tooltips = page.locator('main [role="tooltip"]')
      expect(await tooltips.count()).toBeGreaterThan(0)

      for (const tooltip of await tooltips.all()) {
        const id = await tooltip.getAttribute('id')
        expect(id).toBeTruthy()
        expect((await tooltip.textContent())?.trim().length).toBeGreaterThan(0)

        const button = tooltip.locator('xpath=preceding-sibling::button[1]')
        await expect(button).toHaveAttribute('aria-describedby', id!)
        const accessibleLabel = await button.getAttribute('aria-label')
        expect(accessibleLabel?.trim().length).toBeGreaterThan(0)
        if (locale !== 'en') expect(accessibleLabel).not.toMatch(/^About:/)

        await button.focus()
        await expect(tooltip).toBeVisible()
        const bounds = await tooltip.boundingBox()
        expect(bounds).not.toBeNull()
        expect(bounds!.x).toBeGreaterThanOrEqual(0)
        expect(bounds!.y).toBeGreaterThanOrEqual(0)
        expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(812)
        expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(375)
      }
    }
  }
})
