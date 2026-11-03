import { expect, test, type Page, type Route } from '@playwright/test'

// The bulk actions of the Queue and the candidate table. These tests cover what the operator
// sees: which rows are picked, what the confirmation says, what the request carries, and what
// the result banner reports — including the rows a batch could not handle.

function job(id: number, status: string, verificationPassed: boolean | null = null) {
  return {
    id,
    mediaFileId: id,
    libraryId: 1,
    relativePath: `Film ${id}.mkv`,
    status,
    priority: 0,
    progress: 1,
    errorMessage: null,
    enqueueReason: null,
    failureCategory: null,
    ffmpegArguments: null,
    appliedAudioFilter: null,
    videoEncoder: 'libx265',
    requestedVideoQuality: 24,
    effectiveVideoQuality: 24,
    videoQualityMode: 'crf',
    qualityRetryCount: 0,
    outputSizeBytes: 1_000,
    verificationPassed,
    verificationReportJson: null,
    verifiedAt: null,
    enqueuedAt: '2026-07-23T11:00:00Z',
    startedAt: null,
    finishedAt: null,
    clearable: false,
    workerName: null,
    remoteStage: null,
    waitingForWorker: false,
  }
}

function candidate(mediaFileId: number, relativePath: string) {
  return {
    mediaFileId,
    libraryId: 1,
    relativePath,
    mediaKind: 'Video',
    codec: 'h264',
    profile: 'ConservativeHevc',
    sizeBytes: 2_000_000_000,
    durationSeconds: 5_400,
    isHdr: false,
    eligible: true,
    reason: 'Re-encode to save space',
  }
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  })
}

const library = {
  id: 1, name: 'Films', path: '/media/films', mediaType: 'Tv', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  normalizeOpusChannelLayout: true,
  keepAudioLanguages: null, keepSubtitleLanguages: null, reencodeLossyAudio: false,
  targetImageFormat: null, imageQuality: null, reencodeLossyImages: false,
  imageDownscaleMode: 'None', imageDownscaleValue: 0, moveOnComplete: false,
  targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: null, minVmafMin: null,
  vmafQualityGateEnabled: false, minVmafCatastrophicMin: null, clipVmafEnabled: null,
  vmafFrameSubsample: null, durationTolerancePercent: 1, requireAudioRetained: true,
  requireSubtitlesRetained: false, requireSizeReduction: true,
  minimumSizeSavingPercent: null, maximumSizeSavingPercent: null,
  audioLoudnessGateEnabled: false, maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false, maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true, minimumImageSsim: 0.95, imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, videoQualityStrategy: 'Fixed',
  lastAutoEnqueueAt: null, fileCount: 1,
}

test('the queue picks rows, retries them in one request, and reports what happened', async ({ page }) => {
  const requests: { path: string; body: unknown }[] = []
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Failed'), job(2, 'Failed'), job(3, 'Queued')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/batch/retry') {
      requests.push({ path, body: route.request().postDataJSON() })
      return json(route, {
        requested: 2,
        succeeded: [1],
        failures: [{ id: 2, code: 'job.retry.invalidState', message: 'Only a failed job can be retried.' }],
      })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await expect(page.getByRole('row', { name: /Film 1\.mkv/ })).toBeVisible()

  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select Film 2.mkv' }).check()
  await expect(page.getByText('2 of 3 selected', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Retry' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Retry 2 jobs?')
  await dialog.getByRole('button', { name: 'Retry' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0].body).toEqual({ ids: [1, 2], higherQuality: false })
  await expect(page.getByText('1 succeeded')).toBeVisible()
  await expect(page.getByText('1 could not be handled')).toBeVisible()
  await expect(page.getByText('#2')).toBeVisible()
})

test('dismissing the confirmation sends nothing and leaves the rows picked', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Failed'), job(2, 'Failed'), job(3, 'Queued')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path.startsWith('/api/jobs/batch/')) {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 0, succeeded: [], failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('button', { name: 'Remove' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()

  // The Cancel button closes the dialog and nothing is sent.
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('dialog')).not.toBeVisible()
  expect(requests).toEqual([])
  await expect(page.getByText('1 of 3 selected', { exact: true })).toBeVisible()

  // Escape does the same.
  await page.getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).not.toBeVisible()
  expect(requests).toEqual([])
})

test('a retry says how many of the picked jobs it will actually retry', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Failed'), job(2, 'Queued')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/batch/retry') {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 1, succeeded: [1], failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select Film 2.mkv' }).check()
  await page.getByRole('button', { name: 'Retry' }).click()

  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Retry 1 job?')
  await expect(dialog).toContainText('Only 1 of the 2 selected jobs can be retried')
  await dialog.getByRole('button', { name: 'Retry' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ ids: [1], higherQuality: false })
})

test('a shift-click picks the rows between the two clicks', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Failed'), job(2, 'Failed'), job(3, 'Failed')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/batch/retry') {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 3, succeeded: [1, 2, 3], failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select Film 3.mkv' }).click({ modifiers: ['Shift'] })
  await expect(page.getByText('3 selected', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Retry' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Retry' }).click()
  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ ids: [1, 2, 3], higherQuality: false })
})

test('the header checkbox picks every row of the list at once', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Failed'), job(2, 'Failed'), job(3, 'Failed')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/batch/retry') {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 3, succeeded: [1, 2, 3], failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await page.getByRole('checkbox', { name: 'Select every job in this list' }).check()
  await expect(page.getByText('3 selected', { exact: true })).toBeVisible()
})

test('a running job is stopped only when the confirmation says it will be', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/diagnostics/captures') return json(route, [])
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/jobs' && route.request().method() === 'GET') {
      return json(route, [job(1, 'Queued'), job(2, 'Failed')])
    }
    if (path === '/api/queue/status') return json(route, {
      canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive',
      runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0,
      runningJobs: 0, hardwareAccelerated: false, freeDiskBytes: 100_000_000_000,
      workRoot: '/work', waitingReason: null,
    })
    if (path === '/api/jobs/batch/remove') {
      requests.push(route.request().postDataJSON())
      return json(route, { requested: 2, succeeded: [1, 2], failures: [] })
    }
    if (path === '/api/replacements') return json(route, [])
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/queue')
  await page.getByRole('checkbox', { name: 'Select Film 1.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select Film 2.mkv' }).check()
  await page.getByRole('button', { name: 'Remove' }).click()

  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Remove 2 jobs from the queue?')
  await expect(dialog).toContainText('1 of them is still active and will be stopped first.')
  await dialog.getByRole('button', { name: 'Remove' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ ids: [1, 2], cancelActive: true })
  await expect(page.getByText('2 succeeded')).toBeVisible()
})
test('the candidate table picks files and excludes them in one request', async ({ page }) => {
  const requests: unknown[] = []
  await page.route('**/api/**', async (route: Route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/status') return json(route, { required: false })
    if (path === '/api/setup') return json(route, { version: 1, completedStep: 5, currentStep: 5, stepCount: 5, completed: true })
    if (path === '/api/libraries') return json(route, [library])
    if (path === '/api/libraries/1') return json(route, library)
    if (path === '/api/library-options') return json(route, {
      mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
      ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings', 'RemuxCleanup', 'TrackCleanup'],
      ruleProfileSpecs: [
        { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
        { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false },
        { profile: 'ScottsSettings', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'TonemapToSdr', videoAudioCodec: 'aac', videoAudioBitrateKbps: 96, downmixToStereo: false },
        { profile: 'RemuxCleanup', codec: null, container: 'mkv', crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false },
        { profile: 'TrackCleanup', codec: null, container: null, crf: null, hdrHandling: 'Exclude', videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false },
      ],
      hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'],
      videoCodecs: ['h264', 'hevc', 'av1'], containers: ['mp4', 'mkv'],
      encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: ['veryslow'],
      imageFormats: ['webp'],
    })
    if (path === '/api/candidates') {
      return json(route, [candidate(10, 'a.mkv'), candidate(11, 'b.mkv'), candidate(12, 'c.mkv')])
    }
    if (path === '/api/candidates/summary') return json(route, [{ libraryId: 1, eligible: 3, skipped: 0 }])
    if (path === '/api/exclusions') return json(route, [])
    if (path === '/api/media/batch/exclude') {
      requests.push(route.request().postDataJSON())
      return json(route, {
        requested: 2,
        succeeded: [10, 11],
        failures: [{ id: 11, code: 'media.notFound', message: 'The file is no longer known.' }],
      })
    }
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' })
  })

  await page.goto('/#/libraries/1/configure/source')
  await page.getByRole('button', { name: /Candidates \(/ }).click()
  await expect(page.getByRole('cell', { name: 'a.mkv', exact: true })).toBeVisible()

  await page.getByRole('checkbox', { name: 'Select a.mkv' }).check()
  await page.getByRole('checkbox', { name: 'Select b.mkv' }).check()
  await expect(page.getByText('2 of 3 selected', { exact: true })).toBeVisible()

  await page.locator('[data-selection-toolbar]').getByRole('button', { name: 'Exclude' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Exclude 2 files?')
  await dialog.getByRole('button', { name: 'Exclude' }).click()

  await expect.poll(() => requests.length).toBe(1)
  expect(requests[0]).toEqual({ mediaFileIds: [10, 11] })
  await expect(page.getByText('2 succeeded')).toBeVisible()
  await expect(page.getByText('1 could not be handled')).toBeVisible()
})
