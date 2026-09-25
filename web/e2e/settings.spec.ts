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
  remoteWorkersEnabled: false,
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
    if (path === '/api/jobs') return json(route, [])
    if (path === '/api/queue/status') return json(route, { runningJobs: 0, suspendedEncodeCount: 0 })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/diagnostics/capture') return json(route, null)
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

  // Each room owns its own sections, and opening one is a URL rather than tab state.
  const expectedRooms = new Map([
    ['Encoding', ['Queue']],
    ['Files & safety', ['Replacement and cleanup']],
    ['Media servers', ['Media servers']],
    ['Download managers', ['Download managers']],
    ['Notifications', ['Notifications']],
    ['System', ['Appearance', 'Diagnostic capture', 'Tools', 'Hardware acceleration', 'Encoders', 'Backup & restore', 'First-run setup']],
  ])

  for (const [room, headings] of expectedRooms) {
    await page.getByRole('button', { name: new RegExp(`^${room}`) }).click()
    const sections = page.locator('[data-config-section]')
    await expect(sections.getByRole('heading', { level: 2 })).toHaveText(headings)
    await page.getByRole('button', { name: 'All settings' }).click()
  }

  await page.getByRole('button', { name: /^System/ }).click()
  await expect(page.getByRole('button', { name: 'Run setup again' })).toBeVisible()
})

test('advanced workload controls preview and save independent local capacity', async ({ page }) => {
  await mockSettings(page)
  let saved: Record<string, unknown> | null = null
  await page.route('**/api/settings', async route => {
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON()
      return json(route, saved)
    }
    return json(route, { ...settings, workloadConcurrencyMode: 'Automatic', nonVideoSlots: 0,
      evidenceValidationSlots: 1, automaticNonVideoSlots: 1, automaticEvidenceValidationSlots: 2 })
  })
  await page.goto('/#/settings/encoding')
  await page.getByText('Advanced workload lanes').click()
  await expect(page.getByText('1 video · 1 extra audio/image · 2 evidence')).toBeVisible()
  await page.getByLabel('Lane allocation').selectOption('Manual')
  await page.getByLabel('Extra audio & image slots').fill('2')
  await page.getByLabel('Evidence validation slots').fill('3')
  await expect(page.getByText('1 video · 2 extra audio/image · 3 evidence')).toBeVisible()
  await page.getByRole('button', { name: 'Save settings' }).click()
  expect(saved).toMatchObject({ workloadConcurrencyMode: 'Manual', nonVideoSlots: 2, evidenceValidationSlots: 3 })
  await page.setViewportSize({ width: 375, height: 667 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('diagnostic capture is opt-in, can be stopped, and exports the selected job', async ({ page }) => {
  await mockSettings(page)
  const sessionId = '00000000-0000-4000-8000-000000000042'
  let capture: Record<string, unknown> | null = null
  let started: Record<string, unknown> | null = null
  await page.route('**/api/diagnostics/capture**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/bundle')) {
      expect(path).toContain(`/capture/${sessionId}/jobs/42/bundle`)
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{"manifest":{}}' })
    }
    if (path.endsWith('/stop')) {
      capture = { ...capture, status: 'Stopped', stoppedAt: '2026-09-23T11:00:00Z' }
      return json(route, capture)
    }
    if (route.request().method() === 'POST') {
      started = route.request().postDataJSON()
      capture = {
        id: sessionId, status: 'Recording', startedAt: '2026-09-23T10:00:00Z',
        expiresAt: '2026-09-23T11:00:00Z', stoppedAt: null,
        scopedJobId: 42, includePaths: false, eventsStored: 0,
        maximumEvents: 10000, eventLimitReached: false,
      }
      return json(route, capture, 201)
    }
    return json(route, capture)
  })

  await page.goto('/#/settings/system')
  await expect(page.getByText('Enhanced diagnostics off')).toBeVisible()
  await page.getByLabel('Capture duration').selectOption('1')
  await page.getByLabel('Job ID (optional)').fill('42')
  await page.getByRole('button', { name: 'Start capture' }).click()
  await expect(page.getByText('Recording diagnostics')).toBeVisible()
  expect(started).toEqual({ durationHours: 1, scopedJobId: 42, includePaths: false })

  await page.getByRole('button', { name: 'Stop capture' }).click()
  await expect(page.getByText('Enhanced diagnostics off')).toBeVisible()
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download diagnostics' }).click()
  expect((await download).suggestedFilename()).toContain(`optimisarr-diagnostics-42-${sessionId}`)
})

test('strict sidecar verification defaults on and an explicit opt-out is saved', async ({ page }) => {
  await mockSettings(page)
  let current = { ...settings, remoteWorkersAvailable: true, workerVerificationRequired: true }
  await page.route('**/api/settings', async route => {
    if (route.request().method() === 'PUT') current = route.request().postDataJSON()
    return json(route, current)
  })
  await page.goto('/#/settings/files')
  const strict = page.getByRole('checkbox', { name: 'Verify entirely on the sidecar', exact: true })
  await expect(strict).toHaveCount(0)
  await page.getByRole('checkbox', { name: 'Remote workers', exact: true }).check()
  await expect(strict).toBeChecked()
  await strict.uncheck()
  const saved = page.waitForResponse(response => response.url().endsWith('/api/settings')
    && response.request().method() === 'PUT')
  await page.getByRole('button', { name: /^Save/ }).click()
  await saved
  expect(current.workerVerificationRequired).toBe(false)
  expect(current.remoteWorkersEnabled).toBe(true)
  await page.reload()
  await expect(strict).not.toBeChecked()
})

test('an edit survives walking to another room and back', async ({ page }) => {
  // The whole risk of splitting settings into rooms: if navigating between them quietly
  // drops a draft, rooms are worse than the single page they replaced.
  await mockSettings(page)
  await page.goto('/#/settings')

  await page.getByRole('button', { name: /^Encoding/ }).click()
  const jobs = page.locator('#max-jobs')
  await jobs.fill('3')

  await page.getByRole('button', { name: 'All settings' }).click()
  await page.getByRole('button', { name: /^Files & safety/ }).click()
  await page.getByRole('button', { name: 'All settings' }).click()
  await page.getByRole('button', { name: /^Encoding/ }).click()

  await expect(jobs).toHaveValue('3')
  // And the page is still offering to write it, from wherever you are.
  await expect(page.getByText('1 unsaved change')).toBeVisible()
})

test('leaving settings with an unsaved edit asks first, but moving between rooms does not', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings')

  await page.getByRole('button', { name: /^Encoding/ }).click()
  await page.locator('#max-jobs').fill('7')

  // Walking to another room must never prompt — the draft is meant to survive it.
  let prompts = 0
  page.on('dialog', (dialog) => {
    prompts += 1
    void dialog.dismiss()
  })
  await page.getByRole('button', { name: 'All settings' }).click()
  await page.getByRole('button', { name: /^Files & safety/ }).click()
  expect(prompts).toBe(0)

  // Leaving Settings altogether must prompt, and dismissing it keeps you where you are.
  await page.locator('nav').getByRole('button', { name: 'Dashboard' }).click()
  await expect.poll(() => prompts).toBe(1)
  await expect(page).toHaveURL(/#\/settings/)
})

test('a changed value says what it was and can be put back', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings')

  await page.getByRole('button', { name: /^Encoding/ }).click()
  const jobs = page.locator('#max-jobs')
  const before = await jobs.inputValue()
  await jobs.fill('4')

  await expect(page.getByText(`was ${before}`)).toBeVisible()
  await page.getByRole('button', { name: 'put back' }).click()
  await expect(jobs).toHaveValue(before)
  await expect(page.getByText('unsaved change')).toHaveCount(0)
})

test('settings and tool capability cards stay within a small mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'dark' })
  await mockSettings(page)
  await page.goto('/#/settings')
  await page.locator('html').evaluate((element) => {
    element.style.fontSize = '125%'
  })
  // Seven cards are taller than a phone, so the last one is reached by scrolling — what
  // matters is that it is reachable and fully inside the column, not that it starts on screen.
  const systemCard = page.getByRole('button', { name: /^System/ })
  await systemCard.scrollIntoViewIfNeeded()
  await expect(systemCard).toBeInViewport()
  await systemCard.click()

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

  const refreshBox = await page.locator('#global-tools').getByRole('button', { name: 'Refresh' }).boundingBox()
  expect(refreshBox?.height).toBeGreaterThanOrEqual(44)
})

test('every settings room reflows without horizontal page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 812, height: 375 })
  await mockSettings(page)
  await page.goto('/#/settings')

  const rooms = ['Encoding', 'Files & safety', 'Media servers', 'Download managers', 'Notifications', 'System']

  // The landing grid itself has to fit before any room does.
  const gridFit = await page.locator('main').evaluate((main) => ({
    scrollWidth: main.scrollWidth,
    clientWidth: main.clientWidth,
  }))
  expect(gridFit.scrollWidth).toBeLessThanOrEqual(gridFit.clientWidth)

  for (const room of rooms) {
    await page.getByRole('button', { name: new RegExp(`^${room}`) }).click()
    const fit = await page.locator('main').evaluate((main) => ({
      scrollWidth: main.scrollWidth,
      clientWidth: main.clientWidth,
    }))
    expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth)
    await page.getByRole('button', { name: 'All settings' }).click()
  }
})

test('information tooltips are translated, populated, and readable in every locale', async ({ page }) => {
  // This traverses every tooltip in two rooms across all nine locales.
  test.slow()
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

    // The two rooms that carry the bulk of the tipped fields. Addressed by URL rather than
    // by card position, because the card labels are translated and the order is not the point.
    for (const room of ['encoding', 'files']) {
      await page.goto(`/#/settings/${room}`)
      if (room === 'encoding') await page.locator('.workload-details summary').click()
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

test('control rooms separate processing from connections and keep complete state summaries', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings')
  const processing = page.getByRole('region', { name: 'Processing & protection', exact: true })
  const connections = page.getByRole('region', { name: 'Connections & system', exact: true })
  await expect(processing.getByRole('button', { name: /^Encoding/ })).toBeVisible()
  await expect(processing.getByRole('button', { name: /^Files & safety/ })).toBeVisible()
  await expect(connections.getByRole('button', { name: /^Media servers/ })).toBeVisible()
  await expect(connections.getByRole('button', { name: /^System/ })).toBeVisible()
  await processing.getByRole('button', { name: /^Encoding/ }).click()
  await page.locator('#max-jobs').fill('3')
  await page.getByRole('button', { name: 'All settings' }).click()
  const encoding = processing.getByRole('button', { name: /^Encoding/ })
  await expect(encoding).toContainText('3 at a time')
  await expect(encoding.locator('[data-room-changes]')).toHaveText('1')
  await encoding.click()
  await expect(page.locator('#max-jobs')).toHaveValue('3')
})

test('returning from a room restores keyboard focus to its card', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings')
  await page.getByRole('button', { name: /^Files & safety/ }).click()
  await page.getByRole('button', { name: 'All settings' }).click()
  await expect(page.getByRole('button', { name: /^Files & safety/ })).toBeFocused()
})

test('sidebar language menu fits its labels in expanded and collapsed rails', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings/system')
  for (const collapsed of [false, true]) {
    if (collapsed) await page.getByRole('button', { name: 'Collapse sidebar' }).click()
    await page.getByRole('button', { name: 'Language: English' }).click()
    const menu = page.getByRole('listbox', { name: 'Language' })
    await expect(menu).toBeVisible()
    const bounds = await menu.boundingBox()
    expect(bounds!.width).toBeGreaterThanOrEqual(170)
    expect(bounds!.x).toBeGreaterThanOrEqual(0)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(page.viewportSize()!.width)
    expect(await menu.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    expect(await menu.getByRole('option').first().evaluate(el => {
      const rect = el.getBoundingClientRect()
      return el.contains(document.elementFromPoint(rect.right - 12, rect.top + rect.height / 2))
    })).toBe(true)
    await page.keyboard.press('End')
    await expect(menu.getByRole('option').last()).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(page.getByRole('button', { name: 'Language: English' })).toBeFocused()
  }
})

test('system panels keep a consistent gap before backup and first-run setup', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings/system')
  await expect(page.locator('#global-encoders')).toBeVisible()
  const gaps = await page.locator('[data-config-section]').evaluateAll(sections =>
    sections.slice(1).map((section, i) => section.getBoundingClientRect().top - sections[i].getBoundingClientRect().bottom),
  )
  for (const gap of gaps) expect(gap).toBeGreaterThanOrEqual(20)
})

test('system cards and encoder tiles remain separated and contained at every width', async ({ page }) => {
  await mockSettings(page)
  await page.goto('/#/settings/system')
  await expect(page.locator('#global-encoders')).toBeVisible()
  for (const width of [1920, 1280, 768, 375]) {
    await page.setViewportSize({ width, height: 980 })
    const issues = await page.locator('.settings-detail .grid').evaluateAll(grids => grids.flatMap(grid => {
      const parent = grid.getBoundingClientRect()
      const children = [...grid.children].map(child => child.getBoundingClientRect())
      return children.flatMap((rect, i) => {
        const outside = rect.left < parent.left - 1 || rect.right > parent.right + 1
        const overlap = children.slice(i + 1).some(other =>
          Math.min(rect.right, other.right) - Math.max(rect.left, other.left) > 1 &&
          Math.min(rect.bottom, other.bottom) - Math.max(rect.top, other.top) > 1)
        return outside || overlap ? [{ outside, overlap }] : []
      })
    }))
    expect(issues, `Card layout at ${width}px`).toEqual([])
  }
})

test('settings child pages use the same content width as their overview', async ({ page }) => {
  await mockSettings(page)
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.goto('/#/settings')
  await expect(page.locator('#settings-room-encoding')).toBeVisible()
  const main = await page.locator('.settings-layout').evaluate(el => el.parentElement!.clientWidth)
  for (const room of ['encoding', 'files', 'media-servers', 'download-managers', 'notifications', 'system']) {
    await page.goto(`/#/settings/${room}`)
    await expect(page.locator('.settings-detail-open')).toBeVisible()
    const body = await page.locator('.settings-detail-open').boundingBox()
    const heading = await page.locator('.settings-room-heading').boundingBox()
    expect(body!.width, room).toBeGreaterThanOrEqual(main - 2)
    expect(Math.abs(body!.x - heading!.x), room).toBeLessThan(1)
    expect(Math.abs(body!.width - heading!.width), room).toBeLessThan(1)
  }
})
