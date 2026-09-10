import { expect, test, type Page, type Route } from '@playwright/test'

const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 0,
  remoteWorkersEnabled: false,
}

const webhookTarget = {
  id: 1,
  name: 'Webhook alerts',
  type: 'Webhook',
  url: 'https://hooks.example.test/optimisarr',
  hasToken: false,
  enabled: true,
  notifyOnReplacement: true,
  notifyOnFailure: true,
  createdAt: '2026-07-25T00:00:00Z',
  updatedAt: '2026-07-25T00:00:00Z',
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function mockSettings(page: Page, targets: unknown[]) {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const method = route.request().method()
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, {
      version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true,
    })
    if (path === '/api/health') return json(route, { status: 'healthy', service: 'optimisarr', version: 'test' })
    if (path === '/api/settings') return json(route, settings)
    if (path === '/api/settings/cleanup') return json(route, {
      retentionDays: 0, dryRunMode: true, failedOutputCount: 0, failedOutputBytes: 0,
      quarantinedOriginalCount: 0, quarantinedOriginalBytes: 0, planToken: 'test', totalCount: 0, totalBytes: 0,
    })
    if (path === '/api/activity-watchers' || path === '/api/arr-connections') return json(route, [])
    if (path === '/api/notification-targets' && method === 'GET') return json(route, targets)
    return json(route, {})
  })
}

async function openNotifications(page: Page) {
  await page.goto('/#/settings')
  await page.getByRole('tab', { name: 'Notifications' }).click()
}

test('notification validation shows the provider-specific API reason', async ({ page }) => {
  await mockSettings(page, [])
  await page.route('**/api/notification-targets', async (route) => {
    if (route.request().method() !== 'POST') return route.fallback()
    return json(route, {
      code: 'notification.validation',
      error: 'A valid Telegram bot token is required.',
    }, 400)
  })
  await openNotifications(page)

  await page.getByLabel('Name').fill('telegrm')
  await page.getByLabel('Type').selectOption('Telegram')
  await page.getByLabel('Chat ID').fill('1234567890')
  await page.getByLabel('Bot token').fill('invalid token')
  await page.getByRole('button', { name: 'Add target' }).click()

  await expect(page.getByText('A valid Telegram bot token is required.')).toBeVisible()
})

test('a saved notification target can send a test notification', async ({ page }) => {
  let testRequests = 0
  await mockSettings(page, [webhookTarget])
  await page.route('**/api/notification-targets/1/test', async (route) => {
    testRequests += 1
    return json(route, { ok: true, error: null })
  })
  await openNotifications(page)

  await page.getByRole('button', { name: 'Test', exact: true }).click()

  await expect(page.getByText('Test notification sent.')).toBeVisible()
  expect(testRequests).toBe(1)
})
