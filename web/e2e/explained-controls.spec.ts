import { expect, test } from '@playwright/test'
import { json, mockApp } from './helpers/audit-app'
import * as fixtures from '../scripts/docs-fixtures.mjs'

// What a control does, and why one is unavailable, is on screen or a tap away: never only in a
// hover title, which a touch screen cannot show and a screen reader may not read.

test('the pause control explains itself a tap away, and only in the open strip on a phone', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/queue')
  const strip = page.getByRole('region', { name: 'State', exact: true })
  const pause = strip.getByRole('button', { name: 'Pause queue' })
  await expect(pause).toBeVisible()
  await expect(pause).not.toHaveAttribute('title')
  await expect(pause).toHaveAccessibleDescription(/Stop new jobs and automatic replacement/)
  const help = strip.getByRole('button', { name: 'More information' })
  await expect(help).toBeVisible()
  await help.click()
  await expect(strip.getByRole('tooltip')).toBeVisible()

  await page.setViewportSize({ width: 375, height: 812 })
  await expect(help).toBeHidden()
  // Still described for a screen reader while the strip is closed.
  await expect(pause).toHaveAccessibleDescription(/Stop new jobs and automatic replacement/)
  await strip.getByRole('button', { name: 'Status details' }).click()
  await expect(help).toBeVisible()
})

test('queue housekeeping explains each clear, and a disabled retry says why', async ({ page }) => {
  await mockApp(page)
  await page.goto('/#/queue')
  const clearErrored = page.getByRole('button', { name: /^Clear errored/ })
  await expect(clearErrored).not.toHaveAttribute('title')
  await expect(clearErrored).toHaveAccessibleDescription(/Originals are untouched/)
  await expect(page.getByRole('button', { name: /^About: Clear errored/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /^About: Clear completed/ })).toBeVisible()

  const queued = fixtures.jobs.find(job => job.status === 'Queued')!
  await page.getByRole('checkbox', { name: `Select ${queued.relativePath}` }).check()
  const retry = page.locator('[data-queue-batch]').getByRole('button', { name: 'Retry' })
  await expect(retry).toBeDisabled()
  await expect(page.locator('[data-batch-retry-unavailable]')).toHaveText('Only failed or cancelled jobs can be retried.')
  await expect(retry).toHaveAccessibleDescription('Only failed or cancelled jobs can be retried.')
})

test('connection badges carry their meaning beside them', async ({ page }) => {
  await mockApp(page)
  await page.route('**/api/activity-watchers', route => json(route, [{
    id: 1, name: 'Living room media', type: 'Jellyfin', baseUrl: 'https://media.example.com',
    hasToken: false, enabled: true, refreshOnReplace: true, showViewerNames: false,
    createdAt: fixtures.when, updatedAt: fixtures.when,
  }]))
  await page.goto('/#/settings/media-servers')
  const row = page.getByRole('listitem').filter({ hasText: 'Living room media' })
  await expect(row.locator('[title]')).toHaveCount(0)
  for (const badge of ['refresh', 'viewers hidden', 'no token']) {
    await expect(row.getByRole('button', { name: `About: ${badge}` })).toBeVisible()
  }
  await expect(row.getByRole('button', { name: 'About: no token' })).toHaveAccessibleDescription(/Optimisarr cannot query this server/)
})
