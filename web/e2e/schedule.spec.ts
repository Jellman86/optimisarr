import { expect, test } from '@playwright/test'

test('schedule explains the dispatch gate and each library window without mislabelling a policy block as a pause', async ({ page }) => {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/auth/status' ? { required: false }
      : path === '/api/setup' ? { completed: true, completedStep: 5, stepCount: 5 }
      : path === '/api/queue/status' ? {
        canStart: false, manuallyPaused: false, blockedReason: 'Work disk below the configured minimum.',
        waitingReason: null, runningJobs: 3, maxConcurrentJobs: 1,
        libraryScanIntervalHours: 6, freeDiskBytes: 1024 ** 3,
        workloadLanes: [
          { lane: 'Video', capacity: 1, active: 1, waiting: 1 },
          { lane: 'NonVideo', capacity: 1, active: 1, waiting: 0 },
          { lane: 'Evidence', capacity: 2, active: 1, waiting: 0 },
          { lane: 'Finalization', capacity: 2, active: 0, waiting: 0 },
          { lane: 'Workers', capacity: 8, active: 1, waiting: 0 },
        ],
      }
      : path === '/api/libraries' ? [
        { id: 4, name: 'Films', enabled: true, autoEnqueueEnabled: true,
          autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '00:00',
          autoReplace: true, lastAutoEnqueueAt: null },
        { id: 5, name: 'Archive', enabled: false, autoEnqueueEnabled: true,
          autoEnqueueWindowStart: '00:00', autoEnqueueWindowEnd: '00:00',
          autoReplace: false, lastAutoEnqueueAt: null },
        { id: 6, name: 'Series', enabled: true, autoEnqueueEnabled: true,
          autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '06:00',
          autoReplace: false, lastAutoEnqueueAt: null },
      ] : null
    await route.fulfill({ status: body === null ? 404 : 200, contentType: 'application/json', body: JSON.stringify(body ?? {}) })
  })

  await page.clock.setFixedTime(new Date('2026-09-17T12:00:30Z'))
  await page.goto('/#/schedule')
  const dispatch = page.getByRole('region', { name: 'Dispatch status' })
  await expect(dispatch).toContainText('New jobs waiting')
  await expect(dispatch).not.toContainText('Paused')
  await expect(dispatch).toContainText('Work disk below the configured minimum.')
  await expect(dispatch).toContainText('3 / 4')
  const windows = page.getByRole('region', { name: 'Auto-optimise windows' })
  await expect(windows.locator('article')).toHaveCount(3)
  await expect(windows.locator('article').filter({ hasText: 'Films' })).toContainText('In window')
  await expect(windows.locator('article').filter({ hasText: 'Archive' })).toContainText(/disabled/i)
  await expect(windows.locator('article').filter({ hasText: 'Series' })).toContainText('Outside window')
  await windows.locator('article').filter({ hasText: 'Films' }).getByRole('button', { name: 'Configure' }).click()
  await expect(page).toHaveURL(/#\/libraries\/4\/configure$/)
})
