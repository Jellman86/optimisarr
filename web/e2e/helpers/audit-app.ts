import type { Page, Route } from '@playwright/test'
import * as fixtures from '../../scripts/docs-fixtures.mjs'

// The whole app on fabricated fixtures, for tests that walk many routes: the layout audit and the
// phone layout checks. Every API path a route asks for is answered here; anything else is
// collected and answered 404, so a test can fail on requests it did not expect.
export function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

export async function mockApp(page: Page) {
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
      total: fixtures.files.length, counts: { all: fixtures.files.length, eligible: fixtures.files.length, skipped: 0, unprobed: 0 },
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
