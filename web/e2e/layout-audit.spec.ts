import { expect, test, type Page, type TestInfo } from '@playwright/test'
import { writeFile } from 'node:fs/promises'
import { mockApp } from './helpers/audit-app'
import { measure, type Layout } from './helpers/layout-measure'

// A route inventory, rather than a handful of screenshots. These fixtures are fabricated;
// the audit never needs a running Optimisarr server or a user's library.
const routes = [
  ['dashboard', '/'],
  ['libraries', '/libraries'],
  ['library overview', '/libraries/1/configure'],
  ['library files', '/libraries/1/configure/files'],
  ['library video', '/libraries/1/configure/video'],
  ['library audio', '/libraries/1/configure/audio'],
  ['library checks', '/libraries/1/configure/checks'],
  ['library schedule', '/libraries/1/configure/schedule'],
  ['photo images', '/libraries/4/configure/images'],
  ['photo checks', '/libraries/4/configure/checks'],
  ['inventory', '/inventory'],
  ['optimise file', '/inventory/optimise/1'],
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

async function waitForAuditedContent(page: Page, name: string) {
  const selector: Record<string, string> = {
    'settings media servers': '#global-media-servers label input[type="checkbox"]',
    'settings notifications': '#global-notifications label input[type="checkbox"]',
    'library video': '#lib-codec',
    'optimise file': '[data-one-file-plan]',
    'photo images': '#lib-image-quality',
    'personal quality check': 'main select',
  }
  if (selector[name]) await expect(page.locator(selector[name]).first()).toBeVisible()
}

for (const viewport of [
  { name: 'wide desktop dark', width: 2048, height: 1167, theme: 'dark' },
  { name: 'ultrawide light', width: 2560, height: 1440, theme: 'light' },
  { name: 'small phone dark', width: 375, height: 812, theme: 'dark' },
]) {
  test(`library pages fill the available content width: ${viewport.name}`, async ({ page }, testInfo) => {
    test.setTimeout(120_000)
    await page.setViewportSize(viewport)
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), viewport.theme)
    const unexpected = await mockApp(page)

    async function expectFullWidth() {
      const measurements = await page.locator('main').evaluate(main => {
        const bounds = main.getBoundingClientRect()
        const style = getComputedStyle(main)
        const left = bounds.left + Number.parseFloat(style.paddingLeft)
        const right = bounds.left + main.clientWidth - Number.parseFloat(style.paddingRight)
        const frame = main.querySelector<HTMLElement>('.page-frame')!
        return [frame, ...frame.querySelectorAll<HTMLElement>(':scope > header, :scope > .status-strip, [data-library-workflow]')]
          .map(element => {
            const box = element.getBoundingClientRect()
            return { element: element.className, leftGap: box.left - left, rightGap: right - box.right }
          })
      })
      for (const measurement of measurements) {
        expect(Math.abs(measurement.leftGap), JSON.stringify(measurement)).toBeLessThanOrEqual(2)
        expect(Math.abs(measurement.rightGap), JSON.stringify(measurement)).toBeLessThanOrEqual(2)
      }
    }

    for (const [name, route] of [...routes.filter(([, route]) => route.startsWith('/libraries') && !route.endsWith('/quality-check')), ['new library', '/libraries/new']]) {
      await page.goto(`/#${route}`)
      await expect(page.locator('main h1')).toBeVisible()
      if (route !== '/libraries') await expect(page.locator('[data-library-workflow]')).toBeVisible()
      await expectFullWidth()
      const layout = await measure(page)
      expect(layout.documentOverflow, name).toBeLessThanOrEqual(2)
      expect(layout.mainOverflow, name).toBeLessThanOrEqual(2)
      expect(layout.escapedCards, name).toEqual([])
      expect(layout.clippedCards, name).toEqual([])
      if (['library overview', 'library files', 'library checks'].includes(name)) {
        await page.screenshot({ path: testInfo.outputPath(`${name.replaceAll(' ', '-')}.png`), animations: 'disabled' })
      }
    }

    await page.goto('/#/libraries/1/configure')
    await expect(page.locator('[data-library-workflow]')).toBeVisible()
    for (const tab of ['Candidates', 'Excluded']) {
      await page.getByRole('button', { name: new RegExp(`^${tab} \\(`) }).click()
      await expect(page.locator('main h1')).toHaveText(tab)
      await expectFullWidth()
    }
    expect([...unexpected], 'unmocked API requests').toEqual([])
  })
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
        const columns = await page.locator('.dashboard-grid').evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)
        expect(columns, 'dashboard panels reflow at 200% text').toBeLessThanOrEqual(2)
      }
      if (viewport.name === 'large text' && name === 'settings workers') {
        const columns = await page.locator('.worker-details').first().evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)
        expect(columns, 'worker details stack at 200% text').toBe(1)
      }
      samples[name] = layout
      const slug = name.replaceAll(/[^a-z0-9]+/gi, '-').toLowerCase()
      if (viewport.name === 'desktop dark' || viewport.name === 'phone dark'
        || (viewport.name === 'large text' && ['dashboard', 'settings workers', 'personal quality check', 'settings media servers', 'photo images'].includes(name))) {
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
  // Nine locales across six pages need a total budget beyond one ordinary page test.
  test.setTimeout(120_000)
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
        'library video', 'photo images', 'personal quality check'].includes(name))) {
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

test('settings list rows light up on hover and keep a visible focus ring inside their card', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/settings')
  const row = page.locator('[data-settings-room]').first()
  await expect(row).toBeVisible()
  const before = await row.evaluate(element => getComputedStyle(element).backgroundColor)
  await row.hover()
  await expect.poll(() => row.evaluate(element => getComputedStyle(element).backgroundColor)).not.toBe(before)
  await page.mouse.move(0, 0)
  await page.keyboard.press('Tab')
  await row.focus()
  await expect(row).toBeFocused()
  // The card clips its rows, so the ring is drawn inside the row's edge rather than outside it.
  const outline = await row.evaluate(element => {
    const style = getComputedStyle(element)
    return { style: style.outlineStyle, offset: parseFloat(style.outlineOffset) }
  })
  expect(outline.style).toBe('solid')
  expect(outline.offset).toBeLessThan(0)
})

for (const probeViewport of [{ name: 'desktop', width: 1440, height: 900, theme: 'dark' }, { name: 'phone', width: 390, height: 844, theme: 'light' }]) {
  test(`accessible names and text contrast ${probeViewport.name}`, async ({ page }, testInfo) => {
    test.setTimeout(240_000)
    const { default: AxeBuilder } = await import('@axe-core/playwright')
    await page.setViewportSize(probeViewport)
    await page.emulateMedia({ colorScheme: probeViewport.theme as 'dark' | 'light', reducedMotion: 'reduce' })
    await page.addInitScript(theme => localStorage.setItem('optimisarr.theme', theme), probeViewport.theme)
    const unexpected = await mockApp(page)
    const results = []
    for (const [name, route] of routes) {
      await page.goto(`/#${route}`)
      await expect(page.locator('main')).toBeVisible()
      await waitForAuditedContent(page, name)
      await page.evaluate(() => document.fonts.ready)
      const scan = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
      expect(scan.violations, `${name}: ${JSON.stringify(scan.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))}`).toEqual([])
      results.push({ name, route, violations: scan.violations, incomplete: scan.incomplete.map(x => ({ id: x.id, nodes: x.nodes.length })), passes: scan.passes.length })
    }
    await writeFile(testInfo.outputPath(`axe-${probeViewport.name}.json`), JSON.stringify({ viewport: probeViewport, results, unexpected: [...unexpected] }, null, 2))
    expect([...unexpected]).toEqual([])
  })
}
