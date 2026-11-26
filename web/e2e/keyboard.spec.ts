import { expect, test, type Page, type Route } from '@playwright/test'
import * as fixtures from '../scripts/docs-fixtures.mjs'

// Keyboard shortcuts only move, find, select and open. These tests prove each one, and prove the
// guards around them: typing in a field, an open dialog and a modified key all leave the page alone.
// The fixtures are the fabricated set the layout audit uses.

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
    if (path.endsWith('/artwork')) return route.fulfill({ contentType: 'image/svg+xml', body: fixtures.artwork(path.split('/')[3]) })
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/health') return json(route, { status: 'healthy', service: 'optimisarr', version: 'audit' })
    if (path === '/api/settings') return json(route, fixtures.settings)
    if (path === '/api/settings/appearance') return json(route, { brandStyle: 'precession' })
    if (path === '/api/diagnostics/capture') return json(route, null)
    if (path === '/api/diagnostics/captures') return json(route, [])
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
    if (path === '/api/results') return json(route, [
      { jobId: 41, mediaFileId: 7, relativePath: 'Show/Season 1/Show - S01E02 - A Long Episode Title WEBDL-1080p.mkv', libraryId: 1, libraryName: 'TV', sourceSizeBytes: 1_282_683_553, outputSizeBytes: 368_922_428, vmafHarmonicMean: 91.3, videoEncoder: 'hevc_qsv', workerName: null, finishedAt: '2026-09-25T04:02:48Z' },
      { jobId: 40, mediaFileId: 8, relativePath: 'Film (2009)/Film (2009) Bluray-1080p.mkv', libraryId: 2, libraryName: 'Film', sourceSizeBytes: 1_229_000_000, outputSizeBytes: 793_000_000, vmafHarmonicMean: 92.3, videoEncoder: 'hevc_videotoolbox', workerName: 'Mac mini', finishedAt: '2026-09-24T09:00:00Z' },
    ])
    if (path === '/api/results/daily') return json(route, Array.from({ length: 30 }, (_, index) => ({
      date: new Date(Date.UTC(2026, 7, 27 + index)).toISOString().slice(0, 10),
      bytesSaved: index % 4 === 0 ? 0 : (index % 7 + 1) * 250_000_000,
      files: index % 4 === 0 ? 0 : index % 7 + 1,
    })))
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
      total: fixtures.files.length, counts: { all: fixtures.files.length, eligible: fixtures.files.length, skipped: 0, unprobed: 0, interlaced: 0 },
    })
    if (/^\/api\/media\/\d+\/plan$/.test(path)) return json(route, fixtures.plan)
    if (path === '/api/settings/dry-run-unlock') return json(route, fixtures.unlockEffects)
    if (path === '/api/replacements') return json(route, fixtures.replacements)
    if (path === '/api/quarantine/duplicates') return json(route, [{ id: '11111111-1111-1111-1111-111111111111', state: 'Quarantined', heldSince: '2026-11-07T10:00:00Z', attentionReason: null, keptPaths: ['/data/films/A very long film title that needs to wrap on a phone (2026)/A very long film title that needs to wrap on a phone (2026).mkv'], copies: [{ path: '/data/films/Backup/A very long film title that needs to wrap on a phone (2026).mkv', quarantinePath: '/data/films/.optimisarr-dedupe/1/Backup/A very long film title that needs to wrap on a phone (2026).mkv', sizeBytes: 4_000_000_000 }] }])
    if (/\/duplicates\/cleanup\/preview$/.test(path)) return json(route, { ready: true, groups: 1, copies: 1, bytesToFree: 1024, leftAlone: { SharedExtra: 2 } })
    if (/\/duplicates\/cleanup$/.test(path)) return json(route, { libraryId: 1, running: false, total: 0, done: 0, moved: 0, bytesHeld: 0, failures: [] })
    if (/^\/api\/replacements\/\d+$/.test(path)) return json(route, fixtures.replacements[Number(path.split('/').pop()) - 1])
    if (path === '/api/workers') return json(route, fixtures.workers)
    if (path === '/api/workers/pairing-code') return route.fulfill({ status: 204 })
    if (path === '/api/activity-watchers') return json(route, [{
      id: 1, name: 'Living room media', type: 'Jellyfin', baseUrl: 'https://media.example.com',
      hasToken: true, enabled: true, refreshOnReplace: true, showViewerNames: true, createdAt: fixtures.when, updatedAt: fixtures.when,
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

async function openApp(page: Page, route: string) {
  const unexpected = await mockApp(page)
  await page.goto(`/#${route}`)
  await expect(page.locator('main h1').first()).toBeVisible()
  return unexpected
}

const hashOf = (page: Page) => page.evaluate(() => window.location.hash)

test('g then a letter goes to each page and names it in the tab', async ({ page }) => {
  await openApp(page, '/')
  for (const [second, hash, title] of [
    ['q', '#/queue', 'Queue'],
    ['i', '#/inventory', 'Inventory'],
    ['r', '#/quarantine', 'Quarantine'],
    ['s', '#/schedule', 'Schedule'],
    ['l', '#/libraries', 'Libraries'],
    [',', '#/settings', 'Settings'],
    ['d', '#/', 'Dashboard'],
  ] as const) {
    await page.keyboard.press('g')
    await page.keyboard.press(second)
    await expect.poll(() => hashOf(page)).toBe(hash)
    await expect(page).toHaveTitle(`${title} · Optimisarr`)
    await expect(page.locator('main h1').first()).toBeVisible()
  }
})

test('a chord that waits too long, or meets another key, does not navigate', async ({ page }) => {
  await openApp(page, '/')
  await page.keyboard.press('g')
  await page.waitForTimeout(1100)
  await page.keyboard.press('q')
  await page.keyboard.press('g')
  await page.keyboard.press('Escape')
  await page.keyboard.press('i')
  await page.keyboard.press('Control+g')
  await page.keyboard.press('q')
  expect(await hashOf(page)).toBe('#/')
})

test('slash focuses the search, and typing there never fires a shortcut', async ({ page }) => {
  await openApp(page, '/inventory')
  const search = page.locator('#inventory-search')
  await page.keyboard.press('/')
  await expect(search).toBeFocused()
  await page.keyboard.type('gqjk?')
  await expect(search).toHaveValue('gqjk?')
  expect(await hashOf(page)).toBe('#/inventory')
  await expect(page.locator('#shortcuts-dialog')).toHaveCount(0)

  // Pressed again, slash selects what is already there so it can be typed over.
  await page.locator('main h1').first().focus()
  await page.keyboard.press('/')
  await expect(search).toBeFocused()
  expect(await search.evaluate((field: HTMLInputElement) => [field.selectionStart, field.selectionEnd])).toEqual([0, 5])
})

test('j, k and x walk and select Inventory rows, and Enter opens the focused one', async ({ page }) => {
  await openApp(page, '/inventory')
  const openers = page.locator('[data-keyboard-list] [data-row-primary]')
  const boxes = page.locator('[data-keyboard-list] tbody .selection-column input[type="checkbox"]')
  await expect(openers.first()).toBeVisible()

  await page.keyboard.press('j')
  await expect(openers.nth(0)).toBeFocused()
  await page.keyboard.press('j')
  await expect(openers.nth(1)).toBeFocused()
  await page.keyboard.press('k')
  await expect(openers.nth(0)).toBeFocused()
  await page.keyboard.press('k')
  await expect(openers.nth(0)).toBeFocused()

  await page.keyboard.press('x')
  await expect(boxes.nth(0)).toBeChecked()
  await expect(openers.nth(0)).toBeFocused()
  await page.keyboard.press('j')
  await page.keyboard.press('x')
  await expect(boxes.nth(1)).toBeChecked()
  await expect(openers.nth(1)).toBeFocused()
  await page.keyboard.press('x')
  await expect(boxes.nth(1)).not.toBeChecked()
  await expect(boxes.nth(0)).toBeChecked()

  await page.keyboard.press('Enter')
  const detail = page.getByRole('dialog')
  await expect(detail).toBeVisible()
  // Nothing behind an open dialog answers a shortcut.
  await page.keyboard.press('g')
  await page.keyboard.press('q')
  await page.keyboard.press('j')
  await page.keyboard.press('?')
  expect(await hashOf(page)).toBe('#/inventory')
  await expect(page.getByRole('dialog')).toHaveCount(1)
  await page.keyboard.press('Escape')
  await expect(detail).toHaveCount(0)
  await expect(openers.nth(1)).toBeFocused()

  // Enter on a row's checkbox opens that row instead of doing nothing.
  await boxes.nth(2).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(boxes.nth(2)).not.toBeChecked()
})

test('j and Enter open a Queue job, and the last row stops k and j at the ends', async ({ page }) => {
  await openApp(page, '/queue')
  const openers = page.locator('[data-keyboard-list] [data-row-primary]')
  await expect(openers.first()).toBeVisible()
  const count = await openers.count()
  await page.keyboard.press('k')
  await expect(openers.nth(count - 1)).toBeFocused()
  await page.keyboard.press('j')
  await expect(openers.nth(count - 1)).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator('#queue-job-dialog')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('#queue-job-dialog')).toHaveCount(0)
  await expect(openers.nth(count - 1)).toBeFocused()
})

test('question mark shows the shortcuts sheet, which offers no action that changes anything', async ({ page }) => {
  await openApp(page, '/')
  await page.keyboard.press('?')
  const sheet = page.getByRole('dialog', { name: 'Keyboard shortcuts' })
  await expect(sheet).toBeVisible()
  await expect(sheet).toContainText('Actions that change, replace, or delete files never have single-key shortcuts.')
  await expect(sheet.locator('dt')).toHaveText([
    'Dashboard', 'Libraries', 'Inventory', 'Queue', 'Quarantine', 'Schedule', 'Settings',
    'Focus search', 'Next row', 'Previous row', 'Select or clear current row', 'Open focused item',
    'Jump to a page, setting, or library', 'Show or hide these shortcuts', 'Close open dialog',
  ])
  await page.keyboard.press('?')
  await expect(sheet).toHaveCount(0)

  await page.locator('[data-open-shortcuts]').click()
  await expect(sheet).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(sheet).toHaveCount(0)
  await expect(page.locator('[data-open-shortcuts]')).toBeFocused()
})

test('Ctrl+K jumps to a settings room or a library, and only navigates', async ({ page }) => {
  await openApp(page, '/')
  await page.keyboard.press('ControlOrMeta+k')
  const jump = page.locator('#quick-jump-dialog')
  await expect(jump).toBeVisible()
  const field = page.getByRole('combobox', { name: 'Jump to a page, setting, or library' })
  await expect(field).toBeFocused()
  await expect(jump.getByRole('option', { name: 'Documentary films' })).toBeVisible()
  await expect(jump).toContainText('Navigation only. Nothing is changed or started from here.')

  const initialActiveId = await field.getAttribute('aria-activedescendant')
  await field.fill('workers')
  await expect(jump.getByRole('option')).toHaveText([/^Remote workers/])
  // Stable item-keyed IDs change aria-activedescendant when filtering changes the first result.
  const filteredActiveId = await field.getAttribute('aria-activedescendant')
  expect(filteredActiveId).toBeTruthy()
  expect(filteredActiveId).not.toBe(initialActiveId)
  // Pressing down on an option without releasing never steals focus from the combobox field.
  await jump.getByRole('option').first().dispatchEvent('mousedown')
  await expect(field).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(jump).toHaveCount(0)
  await expect.poll(() => hashOf(page)).toBe('#/settings/workers')

  await page.keyboard.press('ControlOrMeta+k')
  await field.fill('landscape')
  await expect(jump.getByRole('option')).toHaveText(['Landscape studies'])
  await page.keyboard.press('Enter')
  await expect.poll(() => hashOf(page)).toBe('#/libraries/4/configure')

  // Arrow keys wrap, and the option they land on is the one Enter takes.
  await page.keyboard.press('ControlOrMeta+k')
  await expect(jump.getByRole('option', { name: 'Landscape studies' })).toBeVisible()
  await page.keyboard.press('ArrowUp')
  const last = jump.getByRole('option').last()
  await expect(last).toHaveAttribute('aria-selected', 'true')
  await expect(field).toHaveAttribute('aria-activedescendant', (await last.getAttribute('id'))!)
  await page.keyboard.press('ArrowDown')
  await expect(jump.getByRole('option').first()).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('ControlOrMeta+k')
  await expect(jump).toHaveCount(0)
})

test('Ctrl+K swaps the shortcuts sheet for Quick Jump but never interrupts another dialog', async ({ page }) => {
  await openApp(page, '/inventory')
  await page.keyboard.press('?')
  await page.keyboard.press('ControlOrMeta+k')
  await expect(page.locator('#shortcuts-dialog')).toHaveCount(0)
  await expect(page.locator('#quick-jump-dialog')).toBeVisible()
  await expect(page.locator('#quick-jump-dialog').getByRole('combobox')).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(page.locator('#quick-jump-dialog')).toHaveCount(0)

  await page.locator('[data-keyboard-list] [data-row-primary]').first().click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.keyboard.press('ControlOrMeta+k')
  await expect(page.locator('#quick-jump-dialog')).toHaveCount(0)
})

test('Quick Jump still offers pages and settings when libraries cannot be loaded', async ({ page }) => {
  await mockApp(page)
  await page.route('**/api/libraries', (route: Route) => route.fulfill({ status: 500, body: 'boom' }))
  await page.goto('/#/queue')
  await expect(page.locator('main h1').first()).toBeVisible()
  await page.keyboard.press('ControlOrMeta+k')
  const jump = page.locator('#quick-jump-dialog')
  await expect(jump.getByRole('option', { name: 'Failures' })).toBeVisible()
  await expect(jump.getByRole('group', { name: 'Libraries' })).toHaveCount(0)
  await page.locator('#quick-jump-dialog').getByRole('combobox').fill('fail')
  await jump.getByRole('option', { name: 'Failures' }).click()
  await expect.poll(() => hashOf(page)).toBe('#/queue/failures')

  // Switching back to the Queue tab keeps the URL in step so Quick Jump can reach Failures again.
  await page.getByRole('group', { name: 'Queue views' }).getByRole('button', { name: /^Queue/ }).click()
  await expect.poll(() => hashOf(page)).toBe('#/queue')
  await page.keyboard.press('ControlOrMeta+k')
  await page.locator('#quick-jump-dialog').getByRole('combobox').fill('fail')
  await jump.getByRole('option', { name: 'Failures' }).click()
  await expect.poll(() => hashOf(page)).toBe('#/queue/failures')
})

test('j and x also walk and select a library’s candidates through their checkboxes', async ({ page }) => {
  await openApp(page, '/libraries/1/configure')
  await page.getByRole('button', { name: /^Candidates \(/ }).click()
  const boxes = page.locator('[data-keyboard-list] tbody .selection-column input[type="checkbox"]')
  await expect(boxes.first()).toBeVisible()
  await page.locator('main h1').first().focus()
  await page.keyboard.press('j')
  await expect(boxes.nth(0)).toBeFocused()
  await page.keyboard.press('x')
  await expect(boxes.nth(0)).toBeChecked()
  await page.keyboard.press('j')
  await expect(boxes.nth(1)).toBeFocused()
})
