import { expect, test } from '@playwright/test'

for (const width of [1440, 390]) {
  test(`playback posters and square album covers have quiet stable fallbacks at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.clock.install()
    const requests: string[] = []
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    const holds = ['Movie', 'Episode', 'Track', 'Movie'].map((kind, index) => ({
      watcher: ['Plex', 'Jellyfin', 'Emby', 'Plex'][index], kind,
      title: ['A Film', 'Pilot', 'A Song', 'No Artwork'][index], series: kind === 'Episode' ? 'A Show' : null,
      season: 1, episode: 2, year: 2026, artist: kind === 'Track' ? 'An Artist' : null,
      user: null, device: null, paused: index === 2,
      artworkUrl: index === 3 ? null : `/api/playback/${index}/artwork`,
    }))
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (path.startsWith('/api/playback/')) {
        requests.push(path)
        return route.fulfill(path.includes('/1/') ? { status: 404 } : {
          contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="180"><rect width="120" height="180" fill="#375d79"/></svg>',
        })
      }
      const body = path === '/api/auth/status' ? { required: false }
        : path === '/api/setup' ? { completed: true, completedStep: 5, stepCount: 5 }
        : path === '/api/queue/status' ? {
          canStart: false, manuallyPaused: false, blockedReason: 'Paused while media servers are active (4 streams).',
          runningJobs: 0, maxConcurrentJobs: 1, freeDiskBytes: null, playbackHolds: holds,
        } : path === '/api/settings' ? { libraryScanIntervalHours: 6 } : []
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
    })
    await page.goto('/#/schedule')
    const playing = page.getByRole('list', { name: 'Playing now' })
    await expect(playing.locator('[data-thumbnail]')).toHaveCount(4)
    await expect(playing.locator('img.opacity-100')).toHaveCount(2)
    await expect(playing.getByRole('listitem').nth(1).locator('img')).toHaveCount(0)
    await expect(playing.getByRole('listitem').nth(3).locator('img')).toHaveCount(0)
    const album = await playing.locator('[data-shape="square"]').boundingBox()
    expect(album?.width).toBe(album?.height)
    await expect(playing).toContainText('A Song by An Artist')
    await expect(playing).toContainText('(paused)')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    const before = await playing.boundingBox()
    const initialRequests = [...requests]
    await page.clock.fastForward(15000)
    await expect(playing.locator('img.opacity-100')).toHaveCount(2)
    expect(await playing.boundingBox()).toEqual(before)
    expect(requests).toEqual(initialRequests)
    holds[1].artworkUrl = '/api/playback/5/artwork'
    await page.clock.fastForward(15000)
    await expect(playing.locator('img.opacity-100')).toHaveCount(3)
    await expect(page.getByRole('region', { name: 'State', exact: true }).getByRole('link')).toHaveAttribute('href', '#/queue')
    await page.getByRole('region', { name: 'State', exact: true }).getByRole('link').click()
    await expect(page).toHaveURL(/#\/queue$/)
    await expect(page.getByRole('list', { name: 'Playing now' }).locator('[data-thumbnail]')).toHaveCount(4)
    expect(errors).toEqual([])
  })
}
