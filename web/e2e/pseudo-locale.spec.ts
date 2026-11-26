import { expect, test, type Page, type TestInfo } from '@playwright/test'
import { json, mockApp } from './helpers/audit-app'
import { measure, measureControls } from './helpers/layout-measure'

// Every string 40% or more longer than English (the development-only pseudo-locale), so a layout
// that only holds together for short English is caught before a long translation finds it.
const PSEUDO_LOCALE = 'en-XA'

const routes = [
  ['dashboard', '/'],
  ['queue', '/queue'],
  ['inventory', '/inventory'],
  ['quarantine', '/quarantine'],
  ['quarantine review', '/quarantine/1'],
  ['libraries', '/libraries'],
  ['library overview', '/libraries/1/configure'],
  ['library video', '/libraries/1/configure/video'],
  ['library candidates', '/libraries/1/configure/files'],
  ['settings', '/settings'],
  ['settings encoding', '/settings/encoding'],
  ['settings media servers', '/settings/media-servers'],
  ['settings download managers', '/settings/download-managers'],
  ['settings workers', '/settings/workers'],
] as const

const viewports = [
  { name: 'phone', width: 375, height: 812 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'desktop', width: 1440, height: 900 },
] as const

async function usePseudoLocale(page: Page) {
  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' })
  await page.addInitScript(code => {
    localStorage.setItem('optimisarr.theme', 'dark')
    localStorage.setItem('optimisarr:locale', code)
  }, PSEUDO_LOCALE)
}

async function audit(page: Page, testInfo: TestInfo, name: string, findings: string[]) {
  await expect(page.locator('html')).toHaveAttribute('lang', PSEUDO_LOCALE)
  await page.evaluate(() => document.fonts.ready)
  const layout = await measure(page)
  const controls = await measureControls(page)
  const slug = `${name}-${page.viewportSize()!.width}`.replaceAll(/[^a-z0-9]+/gi, '-').toLowerCase()
  await page.screenshot({ path: testInfo.outputPath(`${slug}.png`), animations: 'disabled', fullPage: true })
  const problems = {
    ...(layout.documentOverflow > 2 && { documentOverflow: layout.documentOverflow }),
    ...(layout.mainOverflow > 2 && { mainOverflow: layout.mainOverflow }),
    ...(layout.escapedCards.length && { escapedCards: layout.escapedCards }),
    ...(layout.clippedCards.length && { clippedCards: layout.clippedCards }),
    ...(controls.clippedControls.length && { clippedControls: controls.clippedControls }),
    ...(controls.overlappingControls.length && { overlappingControls: controls.overlappingControls }),
    ...(controls.squeezedFields.length && { squeezedFields: controls.squeezedFields }),
    ...(controls.crushedHeaders.length && { crushedHeaders: controls.crushedHeaders }),
  }
  if (Object.keys(problems).length) findings.push(`${name} @${page.viewportSize()!.width}: ${JSON.stringify(problems)}`)
}

for (const viewport of viewports) {
  test(`long text keeps every page inside the window: ${viewport.name}`, async ({ page }, testInfo) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    await usePseudoLocale(page)
    const unexpected = await mockApp(page)
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    const findings: string[] = []
    for (const [name, route] of routes) {
      await page.goto(`/#${route}`)
      // The page's own words are pseudo text once the locale has loaded: bracketed and accented.
      await expect(page.locator('main')).toContainText('[')
      await expect(page.locator('main h1').first()).toBeVisible()
      await audit(page, testInfo, name, findings)
    }
    expect(pageErrors, 'uncaught UI errors').toEqual([])
    expect([...unexpected], 'unmocked API requests').toEqual([])
    expect(findings, 'overflow, clipped, overlapping or crushed controls under 40% longer text').toEqual([])
  })

  test(`long text keeps first-run setup inside the window: ${viewport.name}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    await usePseudoLocale(page)
    await mockApp(page)
    // Registered after the app's mocks, so these answers win: setup has not been finished.
    await page.route('**/api/setup', route => json(route, { version: 1, completedStep: 0, currentStep: 1, stepCount: 5, completed: false }))
    await page.route('**/api/setup/readiness', route => json(route, {
      databaseAvailable: true, ready: true, platform: 'compose', paths: [], storageRelationships: [],
      tools: [{ name: 'FFmpeg', command: 'ffmpeg', available: true, required: true, version: '7.1', error: null }],
      recommendation: { encoderMode: 'IntelQsv', hardwareDecode: true, vmafTier: 'Keep', scheduleStart: '01:00', scheduleEnd: '06:00', encoderReason: 'intel', vmafReason: 'cpu-cost' },
    }))
    const findings: string[] = []
    await page.goto('/')
    await expect(page.locator('main h1, h1').first()).toContainText('[')
    await audit(page, testInfo, 'setup', findings)
    expect(findings, 'overflow, clipped, overlapping or crushed controls under 40% longer text').toEqual([])
  })
}
