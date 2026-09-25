import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test'
import { writeFile } from 'node:fs/promises'
import * as fixtures from '../scripts/docs-fixtures.mjs'

// A route inventory, rather than a handful of screenshots. These fixtures are fabricated;
// the audit never needs a running Optimisarr server or a user's library.
const routes = [
  ['dashboard', '/'],
  ['libraries', '/libraries'],
  ['library overview', '/libraries/1/configure'],
  ['library source', '/libraries/1/configure/source'],
  ['library source advanced', '/libraries/1/configure/source/advanced'],
  ['library encode', '/libraries/1/configure/encode'],
  ['library quality', '/libraries/1/configure/encode/quality'],
  ['library video', '/libraries/1/configure/encode/video'],
  ['library video advanced', '/libraries/1/configure/encode/video/advanced'],
  ['library audio', '/libraries/1/configure/encode/audio'],
  ['library audio advanced', '/libraries/1/configure/encode/audio/advanced'],
  ['library verification', '/libraries/1/configure/verify'],
  ['library verification advanced', '/libraries/1/configure/verify/advanced'],
  ['library automation', '/libraries/1/configure/automate'],
  ['photo encoding', '/libraries/4/configure/encode/images'],
  ['photo encoding advanced', '/libraries/4/configure/encode/images/advanced'],
  ['inventory', '/inventory'],
  ['queue', '/queue'],
  ['quarantine', '/quarantine'],
  ['quarantine review', '/quarantine/1'],
  ['schedule', '/schedule'],
  ['settings overview', '/settings'],
  ['settings encoding', '/settings/encoding'],
  ['settings files', '/settings/files'],
  ['settings media servers', '/settings/media-servers'],
  ['settings download managers', '/settings/download-managers'],
  ['settings notifications', '/settings/notifications'],
  ['settings workers', '/settings/workers'],
  ['settings system', '/settings/system'],
  ['personal quality check', '/libraries/1/quality-check'],
] as const

const viewports = [
  { name: 'desktop dark', width: 1440, height: 900, theme: 'dark' },
  { name: 'wide desktop light', width: 1600, height: 900, theme: 'light' },
  { name: 'laptop light', width: 1024, height: 768, theme: 'light' },
  { name: 'tablet landscape', width: 812, height: 375, theme: 'dark' },
  { name: 'phone dark', width: 390, height: 844, theme: 'dark' },
  { name: 'small phone light', width: 320, height: 720, theme: 'light' },
  { name: 'large text', width: 1440, height: 900, theme: 'light', fontScale: 2 },
] as const

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockApp(page: Page) {
  const unexpected = new Set<string>()
  await page.route('**/hubs/jobs/negotiate**', route => json(route, {
    negotiateVersion: 1, connectionId: 'layout-audit', connectionToken: 'layout-audit',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }))
  await page.routeWebSocket('**/hubs/jobs?*', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) socket.send('{}\u001e')
    })
  })
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/thumbnail')) return route.fulfill({ contentType: 'image/svg+xml', body: fixtures.artwork(path.split('/')[3]) })
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/health') return json(route, { status: 'healthy', service: 'optimisarr', version: 'audit' })
    if (path === '/api/settings') return json(route, fixtures.settings)
    if (path === '/api/diagnostics/capture') return json(route, null)
    if (path === '/api/settings/cleanup') return json(route, {
      retentionDays: 14, dryRunMode: true, failedOutputCount: 1, failedOutputBytes: 2e9,
      quarantinedOriginalCount: 0, quarantinedOriginalBytes: 0, planToken: 'layout-audit',
      totalCount: 1, totalBytes: 2e9,
    })
    if (path === '/api/system/tools') return json(route, { tools: fixtures.tools })
    if (path === '/api/system/hardware') return json(route, { hardware: fixtures.hardware })
    if (path === '/api/stats') return json(route, fixtures.stats)
    if (path === '/api/jobs') return json(route, fixtures.jobs)
    if (path === '/api/jobs/failures') return json(route, [])
    if (path === '/api/queue/status') return json(route, fixtures.queue)
    if (path === '/api/libraries') return json(route, fixtures.libraries)
    if (path === '/api/library-options') return json(route, fixtures.options)
    if (/^\/api\/libraries\/\d+\/access$/.test(path)) return json(route, {
      path: fixtures.library.path, exists: true, readable: true, writable: true, ok: true,
      message: 'Ready', issue: 'none', fileSystemId: 'audit', mountId: '1',
      mountPoint: '/data', fileSystemType: 'ext4', availableBytes: 680e9,
      totalBytes: 2e12, atomicWithWork: true, atomicWithQuarantine: true,
    })
    if (path === '/api/candidates/summary') return json(route, fixtures.libraries.map(library => ({
      libraryId: library.id, eligible: 7, skipped: library.fileCount - 7,
    })))
    if (path === '/api/candidates') return json(route, fixtures.candidates)
    if (path === '/api/exclusions') return json(route, [])
    if (path === '/api/inventory') return json(route, {
      items: fixtures.files.map(file => ({ file, eligible: true, reason: 'Eligible for optimisation.' })),
      total: fixtures.files.length, counts: { all: fixtures.files.length, eligible: fixtures.files.length, skipped: 0, unprobed: 0 },
    })
    if (path === '/api/replacements') return json(route, fixtures.replacements)
    if (/^\/api\/replacements\/\d+$/.test(path)) return json(route, fixtures.replacements[Number(path.split('/').pop()) - 1])
    if (path === '/api/workers') return json(route, fixtures.workers)
    if (path === '/api/workers/pairing-code') return route.fulfill({ status: 204 })
    if (path === '/api/activity-watchers') return json(route, [{
      id: 1, name: 'Living room media', type: 'Jellyfin', baseUrl: 'https://media.example.com',
      hasToken: true, enabled: true, refreshOnReplace: true, createdAt: fixtures.when, updatedAt: fixtures.when,
    }])
    if (path === '/api/arr-connections') return json(route, [{
      id: 1, name: 'Film imports', type: 'Radarr', baseUrl: 'https://films.example.com',
      hasApiKey: true, enabled: true, createdAt: fixtures.when, updatedAt: fixtures.when,
    }])
    if (path === '/api/notification-targets') return json(route, [{
      id: 1, name: 'Media updates', type: 'Webhook', url: 'https://notifications.example.com/audit',
      hasToken: false, enabled: true, notifyOnReplacement: true, notifyOnFailure: true,
      createdAt: fixtures.when, updatedAt: fixtures.when,
    }])
    if (path.endsWith('/calibration/sources')) return json(route, [{
      mediaFileId: 1, relativePath: fixtures.files[0].relativePath,
      durationSeconds: 4200, width: 1920, height: 1080, mediaKind: 'Video', isHdr: false,
    }])
    if (path.includes('/stream') || path.endsWith('/content')) return route.fulfill({ status: 204 })
    unexpected.add(path)
    return json(route, {}, 404)
  })
  return unexpected
}

type Layout = {
  documentOverflow: number
  mainOverflow: number
  escapedCards: string[]
  clippedCards: string[]
  narrowRows: string[]
}

async function measure(page: Page): Promise<Layout> {
  return page.locator('main').evaluate(main => {
    // Fractional grid rounding can leave 1–3px in scrollWidth without clipping a pixel.
    const epsilon = 4
    const mainBox = main.getBoundingClientRect()
    const cards = [...new Set(main.querySelectorAll<HTMLElement>('.card, [data-config-section]'))]
    const visible = (element: HTMLElement) => {
      const box = element.getBoundingClientRect()
      return box.width > 0 && box.height > 0 && getComputedStyle(element).visibility !== 'hidden'
    }
    const escapedCards = cards.filter(visible).filter(card => {
      const box = card.getBoundingClientRect()
      return box.left < mainBox.left - epsilon || box.right > mainBox.right + epsilon
    }).map(card => card.id || card.className.split(' ').slice(0, 3).join('.'))
    const clippedCards = cards.filter(visible).filter(card => {
      const overflow = getComputedStyle(card).overflowX
      return !['auto', 'scroll'].includes(overflow) && card.scrollWidth > card.clientWidth + epsilon
    }).flatMap(card => {
      const box = card.getBoundingClientRect()
      const offenders = [...card.querySelectorAll<HTMLElement>('*')].filter(visible).filter(child => {
        const childBox = child.getBoundingClientRect()
        return childBox.right > box.right + epsilon || childBox.left < box.left - epsilon
      }).slice(0, 3).map(child => child.id || `${child.tagName.toLowerCase()}.${child.className.toString().split(' ').slice(0, 2).join('.')}`)
      // scrollWidth can include browser-specific fractional text/paint extents. A child
      // actually crossing the card edge distinguishes clipping from those false positives.
      return offenders.length
        ? [`${card.id || card.className.split(' ').slice(0, 3).join('.')} (+${card.scrollWidth - card.clientWidth}px; ${offenders.join(', ')})`]
        : []
    })

    // Text and numeric inputs are intentionally capped for readable line length. Toggle rows
    // and section dividers are not: the switch should align with its enclosing card's right edge.
    const narrowRows: string[] = []
    for (const id of ['global-replacement', 'global-notifications', 'global-media-servers', 'global-download-managers']) {
      const section = main.querySelector<HTMLElement>(`#${id}`)
      if (!section) continue
      for (const input of section.querySelectorAll<HTMLInputElement>('label input[type="checkbox"]')) {
        const label = input.closest<HTMLElement>('label')
        if (!label || !visible(label)) continue
        const parent = id === 'global-replacement'
          ? section.querySelector<HTMLElement>(':scope > div:last-child')
          : label.closest<HTMLElement>('.rounded-lg.border')
        if (!parent) continue
        const box = parent.getBoundingClientRect()
        if (box.width < 700) continue
        const expectedRight = box.right - Number.parseFloat(getComputedStyle(parent).paddingRight || '0')
        const gap = expectedRight - label.getBoundingClientRect().right
        if (gap > 24) narrowRows.push(`${id}: ${label.textContent?.trim().replace(/\s+/g, ' ').slice(0, 60)} (${Math.round(gap)}px unused)`)
      }
    }
    return {
      documentOverflow: Math.max(0, document.documentElement.scrollWidth - window.innerWidth),
      mainOverflow: Math.max(0, main.scrollWidth - main.clientWidth),
      escapedCards,
      clippedCards,
      narrowRows,
    }
  })
}

async function waitForAuditedContent(page: Page, name: string) {
  const selector: Record<string, string> = {
    'settings media servers': '#global-media-servers label input[type="checkbox"]',
    'settings notifications': '#global-notifications label input[type="checkbox"]',
    'library video advanced': '#lib-codec',
    'photo encoding advanced': '#lib-image-quality',
    'personal quality check': 'main select',
  }
  if (selector[name]) await expect(page.locator(selector[name]).first()).toBeVisible()
}

for (const viewport of viewports) {
  test(`layout inventory: ${viewport.name}`, async ({ page }, testInfo: TestInfo) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    await page.emulateMedia({ colorScheme: viewport.theme, reducedMotion: 'reduce' })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), viewport.theme)
    const unexpected = await mockApp(page)
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    const findings: string[] = []
    const samples: Record<string, Layout> = {}
    for (const [name, route] of routes) {
      await page.goto(`/#${route}`)
      await expect(page.locator('main')).toBeVisible()
      await waitForAuditedContent(page, name)
      await page.evaluate(() => document.fonts.ready)
      if ('fontScale' in viewport) {
        await page.locator('html').evaluate((element, scale) => { element.style.fontSize = `${scale * 100}%` }, viewport.fontScale)
      }
      const layout = await measure(page)
      if (viewport.name === 'large text' && name === 'dashboard') {
        const columns = await page.locator('.telemetry-grid').evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)
        expect(columns, 'dashboard metrics reflow at 200% text').toBeLessThanOrEqual(2)
      }
      if (viewport.name === 'large text' && name === 'settings workers') {
        const columns = await page.locator('.worker-details').first().evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)
        expect(columns, 'worker details stack at 200% text').toBe(1)
      }
      samples[name] = layout
      const slug = name.replaceAll(/[^a-z0-9]+/gi, '-').toLowerCase()
      if (viewport.name === 'desktop dark' || viewport.name === 'phone dark'
        || (viewport.name === 'large text' && ['dashboard', 'settings workers', 'personal quality check', 'settings media servers', 'photo encoding advanced'].includes(name))) {
        await page.screenshot({ path: testInfo.outputPath(`${slug}.png`), animations: 'disabled' })
      }
      if (layout.documentOverflow > 2 || layout.mainOverflow > 2 || layout.escapedCards.length || layout.clippedCards.length || layout.narrowRows.length) {
        findings.push(`${name}: ${JSON.stringify(layout)}`)
        const path = testInfo.outputPath(`${slug}-finding.png`)
        await page.screenshot({ path, animations: 'disabled' })
        await testInfo.attach(slug, { path, contentType: 'image/png' })
      }
    }
    const reportPath = testInfo.outputPath('layout-measurements.json')
    await writeFile(reportPath, JSON.stringify({ viewport, samples, unexpected: [...unexpected], pageErrors }, null, 2))
    await testInfo.attach('layout-measurements', { path: reportPath, contentType: 'application/json' })
    expect(pageErrors, 'uncaught UI errors').toEqual([])
    expect([...unexpected], 'unmocked API requests').toEqual([])
    expect(findings, 'overflow, escaped cards, and narrow control rows').toEqual([])
  })
}

test('translated controls stay inside their cards at phone width', async ({ page }, testInfo) => {
  const unexpected = await mockApp(page)
  await page.setViewportSize({ width: 320, height: 720 })
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.goto('/')
  const findings: string[] = []
  for (const locale of ['en', 'de', 'es', 'fr', 'it', 'ja', 'pt', 'ru', 'zh']) {
    await page.evaluate(code => localStorage.setItem('optimisarr:locale', code), locale)
    await page.reload()
    for (const [name, route] of routes.filter(([name]) =>
      ['settings overview', 'settings media servers', 'settings notifications',
        'library video advanced', 'photo encoding advanced', 'personal quality check'].includes(name))) {
      await page.goto(`/#${route}`)
      await expect(page.locator('main')).toBeVisible()
      await waitForAuditedContent(page, name)
      await expect(page.locator('html')).toHaveAttribute('lang', locale)
      await page.evaluate(() => document.fonts.ready)
      const layout = await measure(page)
      if (layout.documentOverflow > 2 || layout.mainOverflow > 2 || layout.escapedCards.length || layout.clippedCards.length) {
        findings.push(`${locale} ${name}: ${JSON.stringify(layout)}`)
        const path = testInfo.outputPath(`${locale}-${name.replaceAll(' ', '-')}.png`)
        await page.screenshot({ path, animations: 'disabled' })
        await testInfo.attach(`${locale}-${name}`, { path, contentType: 'image/png' })
      }
    }
  }
  expect([...unexpected], 'unmocked API requests').toEqual([])
  expect(findings, 'translated layout overflow').toEqual([])
})

test('detail surfaces fit the viewport and leave the page scrollable', async ({ page }, testInfo) => {
  await mockApp(page)
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport)
    for (const [route, opener] of [
      ['/inventory', 'button:has-text("Lumen Coast.mkv")'],
      ['/queue', '[aria-label="Working now"] button:has-text("View job")'],
    ]) {
      await page.goto(`/#${route}`)
      await page.locator(opener).first().click()
      const dialog = page.getByRole('dialog')
      await expect(dialog).toBeVisible()
      const box = await dialog.boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(-2)
      expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width + 2)
      expect(box!.height).toBeLessThanOrEqual(viewport.height + 2)
      expect(await dialog.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(2)
      await testInfo.attach(`${route.slice(1)}-${viewport.width}`, {
        body: await page.screenshot({ animations: 'disabled' }), contentType: 'image/png',
      })
      await page.keyboard.press('Escape')
    }
  }
})

test('interactive cards retain their hover lift and keyboard focus', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/libraries/1/configure')
  const card = page.locator('.card-interactive').first()
  await expect(card).toBeVisible()
  const before = await card.evaluate(element => getComputedStyle(element).boxShadow)
  await card.hover()
  await expect.poll(() => card.evaluate(element => getComputedStyle(element).boxShadow)).not.toBe(before)
  await card.focus()
  await expect(card).toBeFocused()
})
