import { expect, test, type Locator, type Page } from '@playwright/test'
import * as fixtures from '../scripts/docs-fixtures.mjs'
import { json, mockApp } from './helpers/audit-app'

// A phone reads a list row as two lines, keeps the status strip to one, and keeps every bar
// pinned to the bottom clear of the home indicator. A tablet keeps the desktop table.
const PHONE = { width: 375, height: 812 }
const TABLET = { width: 768, height: 1024 }
const HOME_INDICATOR = 34

type List = { name: string, route: string, list: string, reveal?: (page: Page) => Promise<void> }
const lists: List[] = [
  { name: 'Queue', route: '/queue', list: '#queue-list' },
  { name: 'Inventory', route: '/inventory', list: '[data-keyboard-list]' },
  { name: 'Quarantine', route: '/quarantine', list: '[data-quarantine-list]' },
  {
    name: 'Candidates', route: '/libraries/1/configure', list: '[data-keyboard-list]',
    reveal: page => page.getByRole('button', { name: /^Candidates \(/ }).click(),
  },
  { name: 'Recent results', route: '/', list: 'section.results' },
]

async function open(page: Page, route: string, viewport: { width: number, height: number }) {
  await page.setViewportSize(viewport)
  await page.emulateMedia({ reducedMotion: 'reduce' })
  const unexpected = await mockApp(page)
  await page.goto(`/#${route}`)
  await expect(page.locator('main h1')).toBeVisible()
  return unexpected
}

async function openList(page: Page, { route, list, reveal }: List, viewport: { width: number, height: number }) {
  const unexpected = await open(page, route, viewport)
  await reveal?.(page)
  const table = page.locator(list).locator('table')
  await expect(table.locator('tbody tr').first()).toBeVisible()
  return { table, unexpected }
}

/**
 * Where each visible cell of every body row sits, relative to its row, and where the row's title
 * starts: past its artwork when it has some.
 */
async function rowCells(table: Locator) {
  return table.locator('tbody tr').evaluateAll(rows => rows.map(row => {
    const rowBox = row.getBoundingClientRect()
    const cells = [...row.querySelectorAll<HTMLElement>(':scope > td:not(.selection-column)')]
      .filter(cell => cell.getBoundingClientRect().width > 0)
      .map(cell => {
        const box = cell.getBoundingClientRect()
        return { primary: cell.classList.contains('data-table-primary'), top: box.top - rowBox.top, bottom: box.bottom - rowBox.top, left: box.left - rowBox.left, right: rowBox.right - box.right }
      })
    const artwork = row.querySelector('.data-table-primary [data-thumbnail]')
    const title = artwork?.nextElementSibling ?? row.querySelector('.data-table-primary')
    const titleLeft = title ? title.getBoundingClientRect().left - rowBox.left : 0
    return { display: getComputedStyle(row).display, cells, titleLeft }
  }))
}

async function horizontalOverflow(page: Page) {
  return page.locator('main').evaluate(main => Math.max(document.documentElement.scrollWidth - window.innerWidth, main.scrollWidth - main.clientWidth))
}

/** Stand in for a phone with a home indicator: Chromium reports the inset to `env()`. */
async function emulateHomeIndicator(page: Page) {
  const session = await page.context().newCDPSession(page)
  await session.send('Emulation.setSafeAreaInsetsOverride', { insets: { top: 0, left: 0, right: 0, bottom: HOME_INDICATOR } })
}

for (const entry of lists) {
  const { name } = entry
  test(`${name} rows read as two lines on a phone: the file first, its state and figures under it`, async ({ page }) => {
    const { table, unexpected } = await openList(page, entry, PHONE)

    const rows = await rowCells(table)
    expect(rows.length).toBeGreaterThan(1)
    for (const { display, cells, titleLeft } of rows) {
      expect(display).toBe('flex')
      const primary = cells.filter(cell => cell.primary)
      const rest = cells.filter(cell => !cell.primary)
      expect(primary, 'one cell names the row').toHaveLength(1)
      expect(rest.length, 'something follows the name').toBeGreaterThan(0)
      // The name has the first line to itself, whatever its column on a wider screen.
      for (const cell of rest) expect(cell.top).toBeGreaterThanOrEqual(primary[0].bottom - 1)
      // Everything else shares the second line: no cell is pushed onto a third.
      expect(Math.max(...rest.map(cell => cell.top)), 'one second line').toBeLessThan(Math.min(...rest.map(cell => cell.bottom)))
      // The second line starts under the title, not under its artwork or the row's checkbox.
      expect(Math.abs(rest[0].left - titleLeft)).toBeLessThanOrEqual(1)
    }
    expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0)

    // Still a table underneath: rows, cells and headers keep their roles.
    await expect(table.getByRole('row').first()).toBeVisible()
    expect(await table.getByRole('columnheader').count()).toBeGreaterThan(0)
    expect(await table.getByRole('cell').count()).toBeGreaterThan(0)
    expect([...unexpected]).toEqual([])
  })

  test(`${name} keeps the one-line table rows on a tablet`, async ({ page }) => {
    const { table } = await openList(page, entry, TABLET)
    for (const { display, cells } of await rowCells(table)) {
      expect(display).toBe('table-row')
      // Every cell shares the row's full height: no cell is pushed onto a line of its own.
      expect(new Set(cells.map(cell => Math.round(cell.top))).size).toBe(1)
    }
    expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0)
  })
}

test('a replaced file reads as its name over one line of status and size change on a phone', async ({ page }) => {
  const { table } = await openList(page, lists.find(list => list.name === 'Quarantine')!, PHONE)
  // The fixture holds a spare duplicate too, which is when the table names its rows' kind; every
  // row of this table is an optimisation, so a phone leaves that word out rather than spend a line on it.
  await expect(table.locator('tbody tr').first().getByText('Optimisation')).toBeHidden()
  for (const { cells } of await rowCells(table)) {
    for (const cell of cells.filter(cell => !cell.primary)) expect(cell.bottom - cell.top, 'a single line of text').toBeLessThanOrEqual(24)
  }
})

test('a header of plain labels steps aside on a phone, and still names the columns to a screen reader', async ({ page }) => {
  for (const entry of lists.filter(list => list.name === 'Quarantine' || list.name === 'Recent results')) {
    const { table } = await openList(page, entry, PHONE)
    const head = (await table.locator('thead').boundingBox())!
    expect(head.height, entry.name).toBeLessThanOrEqual(1)
    expect(await table.getByRole('columnheader').count(), entry.name).toBeGreaterThan(1)
  }
  // A header that selects or sorts keeps those on screen; its plain labels step aside instead of
  // running together.
  for (const entry of lists.filter(list => list.name === 'Queue' || list.name === 'Candidates')) {
    const { table } = await openList(page, entry, PHONE)
    await expect(table.locator('thead').getByRole('checkbox')).toBeVisible()
    for (const label of await table.locator('thead th:not(.selection-column)').all()) {
      if (await label.locator('.table-sort').count()) continue
      const box = await label.boundingBox()
      expect(box === null || box.width <= 1, entry.name).toBe(true)
    }
    expect(await table.getByRole('columnheader').count(), entry.name).toBeGreaterThan(1)
  }
})

test('a row’s checkbox answers a thumb anywhere in a 44px square on a phone', async ({ page }) => {
  const { table } = await openList(page, lists.find(list => list.name === 'Inventory')!, PHONE)
  const cell = table.locator('tbody tr').first().locator('.selection-column')
  const box = cell.getByRole('checkbox')
  await expect(box).toHaveAccessibleName(/^Select /)
  const visible = (await box.boundingBox())!
  expect(visible.width).toBeLessThanOrEqual(20)
  const target = (await cell.locator('label').boundingBox())!
  expect(target.width).toBeGreaterThanOrEqual(44)
  expect(target.height).toBeGreaterThanOrEqual(44)
  // The checkbox sits in the middle of its target.
  expect(Math.abs(visible.x + visible.width / 2 - (target.x + target.width / 2))).toBeLessThanOrEqual(1)
  expect(Math.abs(visible.y + visible.height / 2 - (target.y + target.height / 2))).toBeLessThanOrEqual(1)
  // A tap in the target's corner, well clear of the box itself, still selects the row.
  await page.mouse.click(target.x + 3, target.y + 3)
  await expect(box).toBeChecked()
  await page.mouse.click(target.x + target.width - 3, target.y + target.height - 3)
  await expect(box).not.toBeChecked()
})

test('the phone header still sorts, selects every row, and sticks while the list scrolls', async ({ page }) => {
  await open(page, '/inventory', PHONE)
  const table = page.locator('[data-keyboard-list] table')
  const size = table.getByRole('columnheader', { name: 'Size' })
  await expect(size).toHaveAttribute('aria-sort', 'none')
  await size.getByRole('button').click()
  await expect(size).not.toHaveAttribute('aria-sort', 'none')

  // Every header label stays on one line: a sort button never wraps inside its own cell.
  for (const button of await table.locator('thead .table-sort').all()) {
    const lines = await button.evaluate(element => {
      const range = document.createRange()
      range.selectNodeContents(element)
      return new Set([...range.getClientRects()].map(rect => Math.round(rect.top))).size
    })
    expect(lines).toBe(1)
  }

  await table.locator('thead').getByRole('checkbox').check()
  for (const box of await table.locator('tbody .selection-column input[type="checkbox"]').all()) await expect(box).toBeChecked()

  const main = page.locator('main')
  await main.evaluate(element => element.scrollTo(0, element.scrollHeight))
  const [head, mainBox] = [(await table.locator('thead').boundingBox())!, (await main.boundingBox())!]
  expect(Math.abs(head.y - mainBox.y)).toBeLessThanOrEqual(1)
})

test('j, k, x and Enter still walk and open rows in the two-line phone list', async ({ page }) => {
  await open(page, '/inventory', PHONE)
  const openers = page.locator('[data-keyboard-list] [data-row-primary]')
  const boxes = page.locator('[data-keyboard-list] tbody .selection-column input[type="checkbox"]')
  await expect(openers.first()).toBeVisible()
  await page.locator('main h1').click()
  await page.keyboard.press('j')
  await page.keyboard.press('j')
  await expect(openers.nth(1)).toBeFocused()
  await page.keyboard.press('k')
  await expect(openers.nth(0)).toBeFocused()
  await page.keyboard.press('x')
  await expect(boxes.nth(0)).toBeChecked()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('dialog')).toBeVisible()
})

test('on a phone the status strip keeps to one line, and opens to show the rest', async ({ page }) => {
  const waiting = '1418 job(s) waiting for the TV optimise window (00:00–05:00), and 12 more waiting for the Films optimise window (01:00–06:00) on this server'
  await page.setViewportSize(PHONE)
  const unexpected = await mockApp(page)
  // The audit fixtures are encoding; hold the queue in a window with a long reason instead.
  await page.route('**/api/queue/status', route => json(route, { ...fixtures.queue, runningJobs: 0, canStart: false, waitingReason: waiting }))
  await page.route('**/api/jobs**', route => json(route, []))
  await page.goto('/#/inventory')
  const strip = page.getByRole('region', { name: 'State' })
  const reason = strip.locator('.status-strip-reason')
  await expect(reason).toHaveText(waiting)

  // One line: as tall as its pause button and padding, with the reason cut short on screen.
  const toggle = strip.getByRole('button', { name: 'Status details' })
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  const collapsed = (await strip.boundingBox())!
  expect(collapsed.height).toBeLessThanOrEqual(64)
  await expect(reason).toHaveCSS('text-overflow', 'ellipsis')
  expect(await reason.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true)
  // The figures step aside on screen but are still there to be read, and the pause button keeps its name.
  await expect(strip.getByText('Running here')).toBeAttached()
  await expect(strip.getByRole('button', { name: 'Pause queue' })).toBeVisible()
  expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0)

  // A different reason in the next poll does not move the page.
  await page.unroute('**/api/queue/status')
  await page.route('**/api/queue/status', route => json(route, { ...fixtures.queue, runningJobs: 0, canStart: false, waitingReason: 'Waiting' }))
  await page.reload()
  await expect(reason).toHaveText('Waiting')
  expect((await strip.boundingBox())!.height).toBe(collapsed.height)

  // Opened, everything is on screen as on a wider window.
  await page.unroute('**/api/queue/status')
  await page.route('**/api/queue/status', route => json(route, { ...fixtures.queue, runningJobs: 0, canStart: false, waitingReason: waiting }))
  await page.reload()
  const closedToggle = (await toggle.boundingBox())!
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  // The toggle stays where the thumb left it, on the first line beside the state.
  const openToggle = (await toggle.boundingBox())!
  expect(Math.abs(openToggle.x - closedToggle.x)).toBeLessThanOrEqual(2)
  expect(openToggle.y).toBeLessThan((await strip.getByText('Running here').boundingBox())!.y)
  await expect(strip.getByText('Running here')).toBeVisible()
  await expect(strip.getByRole('button', { name: 'Pause queue' })).toContainText('Pause queue')
  expect(await reason.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(0)
  expect((await reason.boundingBox())!.height).toBeGreaterThan(40)
  expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0)
  expect([...unexpected]).toEqual([])
})

test('on a phone the status strip opens on a failed pause, and closes again when asked', async ({ page }) => {
  await open(page, '/inventory', PHONE)
  await page.route('**/api/queue/pause', route => json(route, { message: 'The queue could not be paused.' }, 500))
  const strip = page.getByRole('region', { name: 'State' })
  const toggle = strip.getByRole('button', { name: 'Status details' })
  await expect(toggle).not.toHaveAttribute('aria-controls')
  await strip.getByRole('button', { name: 'Pause queue' }).click()
  await expect(strip.getByRole('alert')).toBeVisible()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  expect((await strip.boundingBox())!.height).toBeLessThanOrEqual(64)
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await expect(strip.getByRole('alert')).toBeVisible()
})

test('on a phone the status strip keeps its height when playback holds the queue with artwork', async ({ page }) => {
  await page.setViewportSize(PHONE)
  await mockApp(page)
  const held = (artworkUrl: string | null) => ({
    ...fixtures.queue, runningJobs: 0, canStart: false, workloadLanes: [],
    blockedReason: 'Paused while Living room media is active (1 stream).',
    playbackHolds: [{ watcher: 'Living room media', year: null, artist: null, paused: false, kind: 'Episode', title: 'Pilot',
      series: 'Example Show', season: 2, episode: 5, user: 'alex', device: 'Living Room TV', artworkUrl }],
  })
  await page.route('**/api/queue/status', route => json(route, held(null)))
  await page.goto('/#/inventory')
  const strip = page.getByRole('region', { name: 'State' })
  await expect(strip.getByText('Holding', { exact: true })).toBeVisible()
  const without = (await strip.boundingBox())!.height

  await page.unroute('**/api/queue/status')
  await page.route('**/api/queue/status', route => json(route, held('/api/playback/1/artwork')))
  await page.reload()
  await expect(strip.locator('[data-thumbnail]')).toBeVisible()
  expect((await strip.boundingBox())!.height).toBe(without)
})

test('on a tablet the status strip shows everything without a toggle', async ({ page }) => {
  await open(page, '/inventory', TABLET)
  const strip = page.getByRole('region', { name: 'State' })
  await expect(strip.getByText('Running here')).toBeVisible()
  await expect(strip.getByRole('button', { name: 'Status details' })).toBeHidden()
  await expect(strip.getByRole('button', { name: 'Pause queue' })).toContainText('Pause queue')
})

for (const viewport of [PHONE, TABLET]) {
  test(`bottom bars sit above the home indicator and never hide the content before them at ${viewport.width}px`, async ({ page }) => {
    await emulateHomeIndicator(page)
    await open(page, '/quarantine/1', viewport)
    const main = page.locator('main')
    const bar = page.locator('[data-decision-bar]')
    await expect(bar).toBeVisible()
    await expect(bar).toHaveCSS('position', 'sticky')

    // Pinned while the evidence scrolls by, but above the inset rather than under it.
    await main.evaluate(element => element.scrollTo(0, 0))
    const pinned = (await bar.boundingBox())!
    expect(pinned.y + pinned.height).toBeLessThanOrEqual(viewport.height - HOME_INDICATOR + 1)
    expect(pinned.y + pinned.height).toBeGreaterThan(viewport.height - HOME_INDICATOR - 2)

    // At the end of the page the bar settles under the last of the evidence instead of over it.
    await main.evaluate(element => element.scrollTo(0, element.scrollHeight))
    const settled = (await bar.boundingBox())!
    const before = (await bar.evaluate(element => {
      const box = (element.previousElementSibling as HTMLElement).getBoundingClientRect()
      return { bottom: box.bottom }
    }))
    expect(before.bottom).toBeLessThanOrEqual(settled.y + 1)
    expect(settled.y + settled.height).toBeLessThanOrEqual(viewport.height - HOME_INDICATOR + 1)

    // The library editor's save bar, once there is something to save, keeps the same promise.
    await page.goto('/#/libraries/1/configure/files')
    await page.getByLabel('Name', { exact: true }).fill('Films archive')
    const save = page.locator('[data-library-actions]')
    await expect(save).toHaveCSS('position', 'sticky')
    await main.evaluate(element => element.scrollTo(0, 0))
    const saveBox = (await save.boundingBox())!
    expect(saveBox.y + saveBox.height).toBeLessThanOrEqual(viewport.height - HOME_INDICATOR + 1)
    await main.evaluate(element => element.scrollTo(0, element.scrollHeight))
    const last = await save.evaluate(element => (element.previousElementSibling as HTMLElement).getBoundingClientRect().bottom)
    expect(last).toBeLessThanOrEqual((await save.boundingBox())!.y + 1)
  })
}

test('a hint fixed to the bottom of a phone clears the home indicator', async ({ page }) => {
  await emulateHomeIndicator(page)
  await open(page, '/settings/encoding', PHONE)
  const tip = page.locator('[role="tooltip"].fixed').first()
  await expect(tip).toBeAttached()
  await expect(tip).toHaveCSS('bottom', `${16 + HOME_INDICATOR}px`)
  // However long, it stops below the top of the window rather than running under it.
  await expect(tip).toHaveCSS('max-height', `${PHONE.height - 32 - HOME_INDICATOR}px`)
})
