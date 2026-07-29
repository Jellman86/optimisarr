import { expect, test, type Page, type Route } from '@playwright/test'

const media = {
  id: 7,
  libraryId: 1,
  relativePath: 'Animation/Example Film.mkv',
  sizeBytes: 4_000_000_000,
  status: 'Probed',
  mediaKind: 'Video',
  container: 'matroska',
  videoCodec: 'h264',
  width: 1_920,
  height: 1_080,
  durationSeconds: 1_296.91,
  audioCodecs: 'aac',
  audioLanguages: 'eng',
  audioTrackCount: 1,
  subtitleTrackCount: 42,
  probedAt: '2026-07-28T12:00:00Z',
  probeError: null,
  optimisedMarker: null,
}

const completedPreview = {
  jobId: 41,
  mediaFileId: 7,
  mediaKind: 'Video',
  status: 'Completed',
  progress: 1,
  errorMessage: null,
  original: {
    sizeBytes: media.sizeBytes,
    container: 'matroska',
    videoCodec: 'h264',
    width: 1_920,
    height: 1_080,
    durationSeconds: media.durationSeconds,
    audioChannels: 2,
    audioCodec: 'aac',
    audioBitrateKbps: 192,
  },
  encoded: {
    sizeBytes: 180_000_000,
    container: 'matroska',
    videoCodec: 'hevc',
    width: 1_920,
    height: 1_080,
    durationSeconds: 60.018,
    audioChannels: 2,
    audioCodec: 'aac',
    audioBitrateKbps: 128,
  },
  savingPercent: 2.8,
  clipped: true,
  clipStartSeconds: 618,
  clipDurationSeconds: 60,
  verificationPassed: true,
  verificationReportJson: JSON.stringify({ checks: [] }),
}

async function mockPreview(page: Page, preview: unknown = completedPreview) {
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') {
      return json(route, {
        version: 1,
        completedStep: 5,
        currentStep: 5,
        stepCount: 5,
        completed: true,
      })
    }
    if (path === '/api/libraries') {
      return json(route, [{
        id: 1,
        name: 'Films',
        path: '/media/films',
        mediaType: 'Film',
        ruleProfile: 'ConservativeHevc',
        enabled: true,
        fileCount: 1,
      }])
    }
    if (path === '/api/inventory') {
      return json(route, {
        items: [{ file: media, eligible: true, reason: 'Ready to optimise' }],
        total: 1,
        counts: { all: 1, eligible: 1, skipped: 0, unprobed: 0 },
      })
    }
    if (path === '/api/media/7/preview' && route.request().method() === 'POST') {
      return json(route, { jobId: 41 })
    }
    if (path === '/api/preview/41' && route.request().method() === 'GET') {
      return json(route, preview)
    }
    if (path === '/api/preview/41' && route.request().method() === 'DELETE') {
      return route.fulfill({ status: 204 })
    }
    if (path === '/api/media/7/content' || path === '/api/preview/41/content') {
      return route.fulfill({ status: 200, contentType: 'video/mp4', body: '' })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

async function openCompletedPreview(page: Page) {
  await mockPreview(page)
  await page.goto('/#/inventory')
  await page.getByText('Example Film.mkv', { exact: true }).click()
  await page.getByRole('button', { name: 'Preview', exact: true }).click()
  await expect(page.getByText('Encoded (sample)', { exact: true })).toBeVisible()
}

async function installMediaClocks(page: Page) {
  await page.locator('video').evaluateAll((videos) => {
    videos.forEach((element, index) => {
      const player = element as HTMLVideoElement
      let currentTime = 0
      let paused = true
      let playbackRate = 1
      const duration = index === 0 ? 1_296.91 : 60.018
      Object.defineProperties(player, {
        duration: { configurable: true, get: () => duration },
        currentTime: {
          configurable: true,
          get: () => currentTime,
          set: (value: number) => {
            currentTime = value
            player.dispatchEvent(new Event('seeking'))
          },
        },
        paused: { configurable: true, get: () => paused },
        playbackRate: {
          configurable: true,
          get: () => playbackRate,
          set: (value: number) => {
            playbackRate = value
            player.dispatchEvent(new Event('ratechange'))
          },
        },
      })
      player.play = async () => {
        paused = false
        player.dispatchEvent(new Event('play'))
      }
      player.pause = () => {
        paused = true
        player.dispatchEvent(new Event('pause'))
      }
      ;(player as HTMLVideoElement & { setClock: (value: number) => void }).setClock =
        (value: number) => { currentTime = value }
      player.dispatchEvent(new Event('loadedmetadata'))
    })
  })
}

test('preview players share one source-relative timeline through native controls', async ({ page }) => {
  await openCompletedPreview(page)
  const videos = page.locator('video')
  await expect(videos).toHaveCount(2)
  await expect(videos.nth(0)).toHaveAttribute('src', '/api/media/7/content')
  await expect(videos.nth(1)).toHaveAttribute('src', '/api/preview/41/content')

  await installMediaClocks(page)
  await expect.poll(() => videos.evaluateAll((items) =>
    items.map((item) => (item as HTMLVideoElement).currentTime))).toEqual([648, 30])

  await videos.nth(0).evaluate((element) => {
    (element as HTMLVideoElement).currentTime = 660
  })
  await expect.poll(() => videos.nth(1).evaluate((element) =>
    (element as HTMLVideoElement).currentTime)).toBe(42)

  await videos.nth(0).evaluate((element) => {
    (element as HTMLVideoElement).playbackRate = 1.5
  })
  await expect.poll(() => videos.nth(1).evaluate((element) =>
    (element as HTMLVideoElement).playbackRate)).toBe(1.5)

  await page.getByRole('button', { name: 'Play both' }).click()
  await expect.poll(() => videos.evaluateAll((items) =>
    items.map((item) => (item as HTMLVideoElement).paused))).toEqual([false, false])

  await videos.evaluateAll((items) => {
    const original = items[0] as HTMLVideoElement & { setClock: (value: number) => void }
    const encoded = items[1] as HTMLVideoElement & { setClock: (value: number) => void }
    original.setClock(663)
    encoded.setClock(44.5)
    original.dispatchEvent(new Event('timeupdate'))
  })
  await expect.poll(() => videos.nth(1).evaluate((element) =>
    (element as HTMLVideoElement).currentTime)).toBe(45)

  await videos.nth(1).evaluate((element) => {
    (element as HTMLVideoElement).pause()
  })
  await expect.poll(() => videos.evaluateAll((items) =>
    items.map((item) => (item as HTMLVideoElement).paused))).toEqual([true, true])
})

test('completed comparison remains usable when minimised and at a narrow desktop viewport', async ({ page }) => {
  await page.setViewportSize({ width: 760, height: 720 })
  await openCompletedPreview(page)

  await expect(page.getByRole('button', { name: 'Play both' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Download' })).toHaveCount(2)
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)

  await page.getByRole('button', { name: 'Minimise' }).click()
  await expect(page.getByText('Preview · Ready')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Expand' })).toBeVisible()
})

test('adaptive preparation names its real work instead of appearing stuck at zero', async ({ page }) => {
  await mockPreview(page, {
    ...completedPreview,
    status: 'Probing',
    progress: 0.001,
    original: null,
    encoded: null,
    savingPercent: null,
    clipped: false,
    clipStartSeconds: null,
    clipDurationSeconds: null,
    verificationPassed: null,
    verificationReportJson: null,
  })
  await page.goto('/#/inventory')
  await page.getByText('Example Film.mkv', { exact: true }).click()
  await page.getByRole('button', { name: 'Preview', exact: true }).click()

  await expect(page.getByText('Selecting quality… 1%', { exact: true })).toBeVisible()
})
