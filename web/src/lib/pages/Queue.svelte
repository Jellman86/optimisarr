<script lang="ts">
  import { tick } from 'svelte'
  import { modal } from '../modal'
  import { isWorkingJob, isJobSuspended, jobLocation, verificationPhase } from '../job-presentation'
  import WorkingJob from '../components/WorkingJob.svelte'
  import JobProgress from '../components/JobProgress.svelte'
  import JobStages from '../components/JobStages.svelte'
  import { api, type DiagnosticCapture, type Job, type JobAttemptSnapshot, type QueueStatus, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize } from '../format'
  import { createJobsConnection, type JobProgress as Telemetry } from '../realtime'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { jobFailureDescription } from '../i18n/jobErrors'
  import { router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import Icon from '../components/Icon.svelte'
  import Banner from '../components/Banner.svelte'
  import UsageGraph from '../components/UsageGraph.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import FailuresPanel from '../components/FailuresPanel.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'

  let jobs = $state<Job[]>([])
  let queueStatus = $state<QueueStatus | null>(null)
  // Live transcode telemetry keyed by job id, pushed over SignalR between reloads.
  let live = $state<Record<number, Telemetry>>({})
  let error = $state<string | null>(null)
  let loading = $state(true)
  let removingId = $state<number | null>(null)
  let replacingId = $state<number | null>(null)
  let replacingAll = $state(false)
  let retryingId = $state<number | null>(null)
  let approvingSizeId = $state<number | null>(null)
  let excludingId = $state<number | null>(null)
  let clearingScope = $state<'errored' | 'finished' | null>(null)
  let clearingPending = $state(false)
  let pauseBusy = $state(false)
  let filter = $state<'all' | 'active' | 'review' | 'completed' | 'failed' | 'verified' | 'verifyFailed'>('all')
  // Queue (live work) vs Failures (failed jobs grouped by reason, with the captured ffmpeg log).
  let activeTab = $state<'queue' | 'failures'>('queue')

  let selectedJobId = $state<number | null>(null)
  let diagnosticCapture = $state<DiagnosticCapture | null>(null)
  let diagnosticLookupPending = $state(false)
  let downloadingDiagnostics = $state(false)
  let captureLookup = 0
  let detailOpener: HTMLElement | null = null
  let loadError = $state<string | null>(null)
  let requestId = 0
  let disposed = false
  let progressRevision = 0
  const progressVersions = new Map<number, number>()

  // Updates arrive over SignalR (jobsChanged + jobProgress). A slow poll is kept
  // only as a safety net and to refresh queue status (free disk, running counts),
  // which is not pushed.
  $effect(() => {
    void load()
    const connection = createJobsConnection({
      onChanged: () => void load(),
      onProgress: applyProgress,
    })
    connection.start().catch(() => {
      /* fall back to the safety poll below if the socket can't connect */
    })
    const timer = setInterval(load, 10000)
    return () => {
      disposed = true
      requestId++
      clearInterval(timer)
      void connection.stop()
    }
  })

  function applyProgress(progress: Telemetry) {
    progressVersions.set(progress.jobId, ++progressRevision)
    live[progress.jobId] = progress
    const job = jobs.find((j) => j.id === progress.jobId)
    if (job) job.progress = progress.progress
  }

  async function load() {
    const request = ++requestId
    const startedAtRevision = progressRevision
    try {
      const [nextJobs, nextStatus] = await Promise.all([api.jobs(), api.queueStatus()])
      if (request !== requestId || disposed) return
      const currentJobs = new Map(jobs.map(job => [job.id, job]))
      for (const nextJob of nextJobs) {
        const current = currentJobs.get(nextJob.id)
        const sameStage = current?.status === nextJob.status && current?.remoteStage === nextJob.remoteStage
        // A poll started before a live update must not rewind it. A new stage,
        // however, owns its own progress and must be allowed to start from zero.
        if (current && sameStage && isWorkingJob(nextJob) && (progressVersions.get(nextJob.id) ?? 0) > startedAtRevision) {
          nextJob.progress = current.progress
        }
        if (!sameStage) delete live[nextJob.id]
      }
      jobs = nextJobs
      queueStatus = nextStatus
      // Drop stale telemetry for jobs that are no longer encoding, here or on a worker.
      const transcoding = new Set(nextJobs.filter((j) => j.status === 'Transcoding' || (j.status === 'Leased' && j.remoteStage === 'Encoding')).map((j) => j.id))
      for (const id of Object.keys(live)) {
        if (!transcoding.has(Number(id))) delete live[Number(id)]
      }
      const present = new Set(nextJobs.map(job => job.id))
      for (const id of progressVersions.keys()) if (!present.has(id)) progressVersions.delete(id)
      loadError = null
    } catch (err) {
      if (request === requestId && !disposed) loadError = err instanceof Error ? err.message : i18n.m.queue.error_load
    } finally {
      if (request === requestId && !disposed) loading = false
    }
  }

  // The job's display name for confirms and the hero, falling back to a job id when the path is unknown.
  function jobName(job: Job): string {
    return job.relativePath ?? t(i18n.m.queue.job_fallback, { id: job.id })
  }

  // The endpoint returns the exact suspension outcome so unsupported/partial states stay honest.
  async function togglePause() {
    if (!queueStatus || pauseBusy) return
    pauseBusy = true
    try {
      queueStatus = queueStatus.manuallyPaused ? await api.resumeQueue() : await api.pauseQueue()
      requestId++
      error = null
    } catch (err) {
      const message = err instanceof Error ? err.message : i18n.m.queue.error_pause
      await load()
      error = message
    } finally {
      pauseBusy = false
    }
  }

  async function stopAndRemove(job: Job) {
    if (!confirm(t(i18n.m.queue.confirm_remove, { name: jobName(job) }))) return
    removingId = job.id
    try {
      if (isActive(job.status)) await api.cancelJob(job.id)
      await api.removeJob(job.id)
      if (selectedJobId === job.id) selectedJobId = null
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_remove
    } finally {
      removingId = null
    }
  }

  const ACTIVE = ['Queued', 'Probing', 'Transcoding', 'Verifying', 'Leased', 'AwaitingVerification', 'ReadyToReplace', 'AwaitingSizeReview']
  function isActive(status: string) {
    return ACTIVE.includes(status)
  }

  async function approveSizePreflight(job: Job) {
    if (!confirm(t(i18n.m.queue.confirm_encode_anyway, { name: jobName(job) }))) return
    approvingSizeId = job.id
    try {
      await api.approveSizePreflight(job.id)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_approve_size
    } finally {
      approvingSizeId = null
    }
  }

  async function retry(job: Job, higherQuality = false) {
    retryingId = job.id
    try {
      await api.retryJob(job.id, higherQuality)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_retry
    } finally {
      retryingId = null
    }
  }

  async function exclude(job: Job) {
    if (!confirm(t(i18n.m.queue.confirm_exclude, { name: jobName(job) }))) return
    excludingId = job.id
    try {
      await api.excludeFile(job.mediaFileId)
      if (selectedJobId === job.id) selectedJobId = null
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_exclude
    } finally {
      excludingId = null
    }
  }

  async function clear(scope: 'errored' | 'finished') {
    clearingScope = scope
    try {
      await api.clearJobs(scope)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_clear
    } finally {
      clearingScope = null
    }
  }

  // Reset the pending queue (e.g. after a rules change): removes queued, size-held and ready-to-replace
  // jobs and cancels anything in flight. No original is touched — ready-to-replace outputs are
  // verified-but-not-applied, so only recomputable work is discarded.
  async function clearPending() {
    const warning = plural(
      pendingCount,
      i18n.m.queue.confirm_clear_pending_one,
      i18n.m.queue.confirm_clear_pending_other,
      pendingCount.toLocaleString(),
    ) + (counts.review > 0 ? `\n\n${t(i18n.m.queue.clear_review_included, { count: counts.review })}` : '')
    if (!confirm(warning)) return
    clearingPending = true
    try {
      await api.clearPendingJobs()
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_clear_queue
    } finally {
      clearingPending = false
    }
  }

  function matchesFilter(job: Job): boolean {
    switch (filter) {
      case 'active': return isActive(job.status)
      case 'review': return job.status === 'AwaitingSizeReview'
      case 'completed': return job.status === 'Completed'
      case 'failed': return job.status === 'Failed'
      // Verification outcome cuts across status (a ready-to-replace job has passed; a job can fail
      // a gate without being status=Failed), so it filters on verificationPassed, not status.
      case 'verified': return job.verificationPassed === true
      case 'verifyFailed': return job.verificationPassed === false
      default: return true
    }
  }

  let processingJobs = $derived(jobs.filter(isWorkingJob))
  // Pin the lead by identity; small progress updates must not reorder cards under the pointer.
  let leadId = $state<number | null>(null)
  $effect(() => { if (!processingJobs.some(job => job.id === leadId)) leadId = processingJobs[0]?.id ?? null })
  let leadJob = $derived(processingJobs.find(job => job.id === leadId) ?? processingJobs[0] ?? null)
  let remainingJobs = $derived(jobs.filter(job => !isWorkingJob(job)))
  let failedCount = $derived(jobs.filter(job => job.status === 'Failed').length)
  let counts = $derived({
    all: remainingJobs.length,
    active: remainingJobs.filter(job => isActive(job.status)).length,
    review: remainingJobs.filter(job => job.status === 'AwaitingSizeReview').length,
    completed: remainingJobs.filter(job => job.status === 'Completed').length,
    failed: remainingJobs.filter(job => job.status === 'Failed').length,
    verified: remainingJobs.filter(job => job.verificationPassed === true).length,
    verifyFailed: remainingJobs.filter(job => job.verificationPassed === false).length,
  })
  let visibleJobs = $derived(remainingJobs.filter(matchesFilter))

  // Render the table one page at a time so a large queue (thousands of jobs) stays responsive; the
  // chips above still count the whole set. The page is clamped, so it stays valid as jobs change.
  const QUEUE_PAGE_SIZE = 100
  let queuePage = $state(1)
  let queuePageCount = $derived(Math.max(1, Math.ceil(visibleJobs.length / QUEUE_PAGE_SIZE)))
  let queuePageStart = $derived((Math.min(queuePage, queuePageCount) - 1) * QUEUE_PAGE_SIZE)
  let pagedJobs = $derived(visibleJobs.slice(queuePageStart, queuePageStart + QUEUE_PAGE_SIZE))

  function selectFilter(key: typeof filter) {
    filter = key
    queuePage = 1
  }

  function goToQueuePage(next: number) {
    queuePage = Math.max(1, Math.min(next, queuePageCount))
  }

  // A hardware encoder is named <codec>_<vendor> (e.g. hevc_nvenc, hevc_qsv, h264_vaapi);
  // anything else (libx265, …) is a CPU/software encoder.
  function isGpuEncoder(encoder: string): boolean {
    return /_(nvenc|qsv|vaapi|amf|videotoolbox)$/.test(encoder)
  }

  // The hero shows the file name as its title (with the path demoted to a small subtitle), rather
  // than the whole relative path. Scene separators become spaces for readability.
  function heroTitle(path: string | null): string | null {
    if (!path) return null
    const base = path.replace(/\\/g, '/').split('/').pop() ?? path
    return base.replace(/\.[^.]+$/, '').replace(/[._]+/g, ' ').trim() || null
  }

  let selectedJob = $derived(jobs.find(job => job.id === selectedJobId) ?? null)

  async function selectRow(id: number, event: MouseEvent) {
    detailOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
    selectedJobId = id
    diagnosticCapture = null
    diagnosticLookupPending = true
    const lookup = ++captureLookup
    error = null
    await tick()
    document.getElementById('queue-detail-title')?.focus({ preventScroll: true })
    void loadDiagnosticCapture(id, lookup)
  }

  async function loadDiagnosticCapture(jobId: number, lookup: number) {
    try {
      const capture = await api.diagnosticCapture()
      if (selectedJobId === jobId && captureLookup === lookup)
        diagnosticCapture = capture && (capture.scopedJobId === null || capture.scopedJobId === jobId)
          ? capture : null
    } catch {
      // The operational job detail still works when enhanced capture is unavailable.
    } finally {
      if (selectedJobId === jobId && captureLookup === lookup) diagnosticLookupPending = false
    }
  }

  async function downloadJobDiagnostics() {
    if (!selectedJob || !diagnosticCapture) return
    const jobId = selectedJob.id
    const captureId = diagnosticCapture.id
    downloadingDiagnostics = true
    error = null
    try {
      const blob = await api.diagnosticBundle(captureId, jobId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `optimisarr-diagnostics-${jobId}-${captureId}.json`
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      downloadingDiagnostics = false
    }
  }

  async function openDiagnosticSettings() {
    await closeDetails()
    router.go('/settings/system')
  }

  async function closeDetails() {
    const id = selectedJobId
    selectedJobId = null
    diagnosticCapture = null
    diagnosticLookupPending = false
    captureLookup++
    await tick()
    const target = detailOpener?.isConnected ? detailOpener : document.getElementById(`working-job-${id}`) ?? document.getElementById(`queue-job-${id}`)
    target?.focus({ preventScroll: true })
  }

  // GPU graph is shown only while the selected job is actually encoding; when the host can't
  // expose GPU stats without elevation the broadcaster reports it unsupported.
  let gpuUnavailable = $derived(
    activity.metrics && !activity.metrics.gpuSupported ? i18n.m.dashboard.gpu_unavailable : null,
  )

  let queuedCount = $derived(jobs.filter(job => job.status === 'Queued').length)
  const LANE_LABELS = $derived({ Video: i18n.m.queue.lane_video, NonVideo: i18n.m.queue.lane_nonvideo, Evidence: i18n.m.queue.lane_evidence, Finalization: i18n.m.queue.lane_finalization, Workers: i18n.m.queue.lane_workers })
  function laneLabel(lane: 'Video' | 'NonVideo' | 'Evidence' | 'Finalization' | 'Workers'): string { return LANE_LABELS[lane] }
  function detailLocation(job: Job): string {
    const location = jobLocation(job)
    return location === 'worker' ? job.workerName ?? i18n.m.queue.lane_workers
      : location === 'transfer' ? i18n.m.queue.location_transfer : i18n.m.dashboard.this_server
  }
  function detailPhase(job: Job): string | null {
    if (job.finalizing) return i18n.m.queue.finalizing_detail
    const phase = verificationPhase(job)
    return phase === 'waiting' ? i18n.m.queue.status_awaitingverification
      : phase === 'evidence' ? i18n.m.queue.phase_evidence
      : phase === 'media' ? i18n.m.queue.phase_media : null
  }

  function badgeClass(status: string): string {
    switch (status) {
      case 'Transcoding':
      case 'Probing':
      case 'Verifying':
      case 'Leased':
        return 'tone-info'
      case 'ReadyToReplace':
      case 'Completed':
        return 'tone-ok'
      case 'Failed':
        return 'tone-bad'
      case 'Cancelled':
        return 'bg-raised text-ink-3'
      default:
        return 'tone-warn'
    }
  }

  // Human-readable, translated label for a job's status enum; falls back to the raw value for any
  // status not yet mapped.
  const STATUS_LABELS: Record<string, string> = $derived({
    Queued: i18n.m.queue.status_queued,
    Probing: i18n.m.queue.status_probing,
    Transcoding: i18n.m.queue.status_transcoding,
    Verifying: i18n.m.queue.status_verifying,
    Leased: i18n.m.queue.status_leased,
    AwaitingVerification: i18n.m.queue.status_awaitingverification,
    AwaitingSizeReview: i18n.m.queue.status_awaitingsizereview,
    ReadyToReplace: i18n.m.queue.status_readytoreplace,
    Completed: i18n.m.queue.status_completed,
    Failed: i18n.m.queue.status_failed,
    Cancelled: i18n.m.queue.status_cancelled,
  })
  function statusLabel(status: string): string {
    return STATUS_LABELS[status] ?? status
  }

  function fullReport(job: Job): VerificationReport | null {
    if (!job.verificationReportJson) return null
    try {
      const report = JSON.parse(job.verificationReportJson) as VerificationReport
      return report.checks ? report : null
    } catch {
      return null
    }
  }

  function parseReport(job: Job): VerificationCheck[] | null {
    return fullReport(job)?.checks ?? null
  }

  function attemptHistory(job: Job): JobAttemptSnapshot[] {
    if (!job.attemptHistoryJson) return []
    try {
      const entries: unknown = JSON.parse(job.attemptHistoryJson)
      return Array.isArray(entries) ? entries as JobAttemptSnapshot[] : []
    } catch {
      return []
    }
  }

  function attemptChecks(attempt: JobAttemptSnapshot): VerificationCheck[] {
    if (!attempt.verificationReportJson) return []
    try {
      return (JSON.parse(attempt.verificationReportJson) as VerificationReport).checks ?? []
    } catch {
      return []
    }
  }

  function isVmafOnlyFailure(job: Job): boolean {
    const failed = fullReport(job)?.checks.filter((check) => check.outcome === 'Failed') ?? []
    return failed.length === 1 && failed[0].name === 'Perceptual quality (VMAF)'
  }

  function vmafSamplingLabel(value: string): string {
    if (value === 'Full file') return i18n.m.queue.vmaf_sampling_full
    if (value === 'Three 40-second samples (early, middle and late)') return i18n.m.queue.vmaf_sampling_three
    return value
  }

  async function replaceAll() {
    if (!confirm(t(i18n.m.queue.confirm_replace_all, { count: readyToReplaceCount.toLocaleString() }))) {
      return
    }
    replacingAll = true
    try {
      const result = await api.replaceReadyJobs()
      selectedJobId = null
      await load()
      if (result.failures.length > 0) {
        error = t(i18n.m.queue.bulk_replace_failed, { count: result.failures.length.toLocaleString() })
      } else if (result.replaced > 0) {
        router.go('/quarantine')
      }
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_replace_all
    } finally {
      replacingAll = false
    }
  }

  async function replace(job: Job) {
    if (!confirm(i18n.m.queue.confirm_replace)) {
      return
    }
    replacingId = job.id
    try {
      await api.replaceFromJob(job.id)
      await load()
      router.go('/quarantine')
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_replace
    } finally {
      replacingId = null
    }
  }

  let activeCount = $derived(jobs.filter((j) => isActive(j.status)).length)
  let readyToReplaceCount = $derived(
    jobs.filter((job) => job.status === 'ReadyToReplace' && job.verificationPassed === true).length,
  )
  // Two clear buckets: finished = completed; errored = failed or cancelled.
  let finishedClearable = $derived(jobs.filter((j) => j.status === 'Completed' && j.clearable).length)
  let finishedProtected = $derived(jobs.filter((j) => j.status === 'Completed' && !j.clearable).length)
  let erroredClearable = $derived(jobs.filter((j) => (j.status === 'Failed' || j.status === 'Cancelled') && j.clearable).length)
  // Pending = not-yet-applied work that a reset can safely discard (queued + verified-but-unreplaced).
  let pendingCount = $derived(jobs.filter((j) => j.status === 'Queued' || j.status === 'ReadyToReplace' || j.status === 'AwaitingSizeReview').length)
</script>

<div class="queue-layout">
  <header class="queue-heading">
    <div><h1 class="page-title">{i18n.m.nav.queue}</h1><p class="page-subtitle">{i18n.m.queue.subtitle}</p></div>
    {#if queueStatus}<button class="btn" class:btn-primary={queueStatus.manuallyPaused} onclick={togglePause} disabled={pauseBusy} aria-busy={pauseBusy} title={queueStatus.manuallyPaused ? i18n.m.queue.resume_queue_title : i18n.m.queue.pause_queue_title}><Icon name={queueStatus.manuallyPaused ? 'play' : 'pause'} />{pauseBusy ? i18n.m.common.loading_short : queueStatus.manuallyPaused ? i18n.m.queue.resume_queue : i18n.m.queue.pause_queue}</button>{/if}
  </header>
  <div class="queue-tabs">
    <button class="focus-ring" aria-pressed={activeTab === 'queue'} onclick={() => (activeTab = 'queue')}>{i18n.m.nav.queue}{#if activeCount > 0}{' '}({activeCount}){/if}</button>
    <button class="focus-ring" aria-pressed={activeTab === 'failures'} onclick={() => (activeTab = 'failures')}>{i18n.m.queue.tab_failures}{#if failedCount > 0}{' '}({failedCount}){/if}</button>
    {#if queueStatus?.freeDiskBytes != null}<span>{i18n.m.schedule.work_disk_free} <strong>{formatSize(queueStatus.freeDiskBytes)}</strong></span>{/if}
  </div>
  {#if activeTab === 'failures'}
    <FailuresPanel />
  {:else}
    {#if counts.review > 0}
      <button class="queue-review-alert card tone-warn focus-ring" onclick={() => selectFilter('review')}>
        <Icon name="warning" />
        <span><strong>{i18n.m.queue.filter_review} · {counts.review}</strong><small>{i18n.m.queue.size_review_detail}</small></span>
        <Icon name="arrow-right" />
      </button>
    {/if}
    {#if loadError}<Banner kind="error" class="mb-4">{loadError}<button class="btn ml-3" onclick={load}>{i18n.m.setup.retry}</button></Banner>{/if}
    {#if error && !selectedJob}<Banner kind="error" class="mb-4">{error}</Banner>{/if}
    {#if queueStatus?.manuallyPaused}
  <!-- The manual pause suspends running encodes, unlike the automatic gates below, which let
       them finish — so it gets its own banner rather than the dispatch_paused wording. The
       reason from the backend states exactly what pausing did on this platform. -->
  <div class="card tone-warn mb-4 flex flex-wrap items-center gap-2 p-3 text-sm">
    <Icon name="pause" class="h-4 w-4 shrink-0" />
    <span>
      {queueStatus.blockedReason}{#if queueStatus.manualPauseMode === 'suspended'}{' '}{i18n.m.queue.paused_manually_hint}{/if}
    </span>
  </div>
{:else if queueStatus && !queueStatus.canStart}
  <div class="card tone-warn mb-4 p-3 text-sm">
    {t(i18n.m.queue.dispatch_paused, { reason: queueStatus.blockedReason ?? '' })}
  </div>
{:else if queueStatus?.waitingReason}
  <div class="card tone-warn mb-4 p-3 text-sm">
    {t(i18n.m.queue.waiting_window, { reason: queueStatus.waitingReason })}
  </div>
{/if}

    {#if queueStatus?.workloadLanes?.length && (activeCount > 0 || queuedCount > 0)}
      <section class="queue-lanes" aria-label={i18n.m.queue.lanes_title}>
        <div class="queue-lanes-heading"><h2>{i18n.m.queue.lanes_title}</h2><p>{i18n.m.queue.lanes_hint}</p></div>
        <div class="queue-lane-grid">
          {#each queueStatus.workloadLanes.filter(lane => lane.lane !== 'Finalization' || lane.active > 0 || lane.waiting > 0) as lane (lane.lane)}
            <div class="queue-lane card" title={lane.reason ?? undefined}>
              <div class="queue-lane-head"><span>{laneLabel(lane.lane)}</span><strong>{lane.active}<span aria-hidden="true"> / </span>{lane.capacity}</strong></div>
              <p>{t(i18n.m.queue.lane_waiting, { count: lane.waiting })}</p>
              {#if lane.reason && lane.waiting > 0}<small>{lane.reason}</small>{/if}
            </div>
          {/each}
        </div>
      </section>
    {/if}


    {#if !loading && leadJob}
      <section class="queue-working" aria-label={i18n.m.queue.working_now}>
        <div class="queue-section-heading"><h2>{i18n.m.queue.working_now}</h2><span>{processingJobs.length}</span></div>
        <WorkingJob job={leadJob} queue={queueStatus} telemetry={live[leadJob.id]} selected={selectedJobId === leadJob.id} onopen={(event) => selectRow(leadJob!.id, event)} />
        {#each processingJobs.filter(job => job.id !== leadJob?.id) as job (job.id)}
          <WorkingJob {job} queue={queueStatus} telemetry={live[job.id]} compact selected={selectedJobId === job.id} onopen={(event) => selectRow(job.id, event)} />
        {/each}
      </section>
    {:else if !loading && jobs.length > 0}
      <p class="queue-idle"><Icon name="pause" />{i18n.m.queue.nothing_processing}{#if queuedCount > 0}{plural(queuedCount, i18n.m.queue.queued_waiting_one, i18n.m.queue.queued_waiting_other, queuedCount.toLocaleString())}{/if}</p>
    {/if}

    {#if selectedJob}
      {@const report = fullReport(selectedJob)}
      {@const suspended = isJobSuspended(selectedJob, queueStatus)}
      {@const earlierAttempts = attemptHistory(selectedJob)}
      {@const lastAttempt = earlierAttempts.at(-1)}
      <dialog id="queue-job-dialog" class="app-modal queue-detail" use:modal={closeDetails} aria-labelledby="queue-detail-label queue-detail-title">
        <header class="queue-detail-heading">
          <div class="queue-detail-poster"><Thumbnail mediaFileId={selectedJob.mediaFileId} size="poster" /></div>
          <div class="queue-detail-identity">
            <p id="queue-detail-label">{i18n.m.queue.job_details}</p>
            <h2 id="queue-detail-title" tabindex="-1">{heroTitle(selectedJob.relativePath) ?? jobName(selectedJob)}</h2>
            <div class="queue-detail-status">
              <span class="badge {suspended ? 'tone-warn' : badgeClass(selectedJob.status)}">{suspended ? i18n.m.queue.now_paused : selectedJob.finalizing ? i18n.m.queue.phase_finalizing : statusLabel(selectedJob.status)}</span>
              {#if selectedJob.executionAttempt && selectedJob.executionAttempt > 0}<span>{t(i18n.m.queue.attempt_number, { number: selectedJob.executionAttempt })}</span>{/if}
              {#if isWorkingJob(selectedJob)}<span>{detailLocation(selectedJob)}</span>{/if}
            </div>
          </div>
          <button class="btn btn-ghost" onclick={closeDetails} aria-label={i18n.m.queue.close_details}><Icon name="x" /></button>
        </header>
        <div class="queue-detail-body">
          {#if error}<Banner kind="error" class="mb-4">{error}</Banner>{/if}
          {#if selectedJob.retryReason === 'SoftwareDecode' && lastAttempt && ['Queued', 'Probing', 'Transcoding', 'Leased', 'AwaitingVerification', 'Verifying'].includes(selectedJob.status)}
            <div class="callout tone-warn mb-4 p-4" role="status">
              <p class="font-semibold">{i18n.m.queue.retry_software_title}</p>
              <p class="mt-1 text-xs leading-relaxed">{t(i18n.m.queue.retry_software_detail, { worker: lastAttempt.workerName ?? i18n.m.dashboard.this_server, encoder: lastAttempt.videoEncoder ?? '—' })}</p>
            </div>
          {/if}
          {#if selectedJob.status !== 'Queued' && selectedJob.status !== 'AwaitingSizeReview'}
            <section class="queue-execution-path" aria-label={i18n.m.queue.execution_path}>
              <h3>{i18n.m.queue.execution_path}</h3>
              <div><span>{i18n.m.queue.step_encode}</span><strong>{selectedJob.workerName ?? i18n.m.dashboard.this_server}</strong></div>
              <div><span>{i18n.m.queue.step_verify}</span><strong>{selectedJob.sidecarVerification ? selectedJob.workerName ?? i18n.m.queue.lane_workers : i18n.m.dashboard.this_server}</strong></div>
              <div><span>{i18n.m.queue.step_replace}</span><strong>{i18n.m.dashboard.this_server}</strong></div>
            </section>
          {/if}
          {#if isWorkingJob(selectedJob)}
            <JobProgress job={selectedJob} queue={queueStatus} telemetry={live[selectedJob.id]} />
            <JobStages job={selectedJob} />
            {#if detailPhase(selectedJob)}<p class="queue-phase-note">{detailPhase(selectedJob)}</p>{/if}
          {/if}
          {#if selectedJob.status === 'Failed'}
            <p class="callout tone-bad mt-4">{jobFailureDescription(selectedJob.failureCategory, i18n.m)}</p>
            {#if selectedJob.errorMessage}<details class="mt-3 text-xs text-ink-3"><summary>{i18n.m.queue.technical_error}</summary><p class="mt-2 whitespace-pre-wrap break-words font-mono">{selectedJob.errorMessage}</p></details>{/if}
          {/if}
          {#if selectedJob.status === 'AwaitingSizeReview'}
            <div class="callout tone-warn mb-4 p-4" role="status">
              <p class="font-semibold">{i18n.m.queue.size_review_title}</p>
              <p class="mt-1 text-xs leading-relaxed">{selectedJob.errorMessage ?? i18n.m.queue.size_review_detail}</p>
            </div>
          {/if}
          <div class="queue-detail-specs">
      <!-- Details -->
      <dl class="grid gap-x-8 gap-y-3 text-sm sm:grid-cols-2 ">
        <div class="flex justify-between gap-4">
          <dt class="text-ink-3">{i18n.m.queue.detail_encoder}</dt>
          <dd class="text-right">{selectedJob.videoEncoder ?? '—'}{#if selectedJob.videoEncoder}<span class="ml-1 text-ink-4">({isGpuEncoder(selectedJob.videoEncoder) ? 'GPU' : 'CPU'})</span>{/if}</dd>
        </div>
        <div class="flex justify-between gap-4">
          <dt class="text-ink-3">{i18n.m.queue.detail_quality}</dt>
          <dd class="text-right">
            {#if selectedJob.videoQualityMode && selectedJob.effectiveVideoQuality != null}
              {selectedJob.videoQualityMode} {selectedJob.effectiveVideoQuality}
              {#if selectedJob.requestedVideoQuality != null && selectedJob.requestedVideoQuality !== selectedJob.effectiveVideoQuality}
                <span class="text-ink-4">({t(i18n.m.queue.quality_requested, { value: selectedJob.requestedVideoQuality })})</span>
              {/if}
            {:else}—{/if}
          </dd>
        </div>
        <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_output_size}</dt><dd>{selectedJob.outputSizeBytes != null ? formatSize(selectedJob.outputSizeBytes) : '—'}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_priority}</dt><dd>{selectedJob.priority}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_verified}</dt><dd class="text-right">{selectedJob.verifiedAt ? new Date(selectedJob.verifiedAt).toLocaleString() : '—'}</dd></div>
      </dl>

      {#if report?.context?.vmafSampling}
        <p class="mt-3 text-xs text-ink-3">
          <span class="font-medium">{i18n.m.queue.detail_vmaf_sampling}:</span>
          {vmafSamplingLabel(report.context.vmafSampling)}
        </p>
      {/if}

      {#if selectedJob.status === 'Failed' && isVmafOnlyFailure(selectedJob)}
        <div class="callout tone-warn mt-4 p-4" role="status" aria-live="polite">
          <div class="flex items-start gap-2">
            <Icon name="warning" class="mt-0.5 h-4 w-4 flex-shrink-0 text-warn" />
            <div>
              <p class="font-semibold">{i18n.m.queue.vmaf_recovery_title}</p>
              <p class="mt-1 text-xs leading-relaxed text-warn-strong">{i18n.m.queue.vmaf_recovery_desc}</p>
            </div>
          </div>
        </div>
      {/if}

      <!-- The exact ffmpeg command for this job — the "under the hood" view that complements the
           hero's live status. Useful while encoding and for diagnosing a failed job. -->
      {#if selectedJob.ffmpegArguments}
        <details class="mt-4 text-xs text-ink-3">
          <summary class="cursor-pointer">{i18n.m.queue.ffmpeg_command}</summary>
          <pre class="max-h-44 overflow-auto whitespace-pre-wrap break-all rounded-md bg-sunken p-3 font-mono text-[11px] leading-relaxed text-ink-2">ffmpeg {selectedJob.ffmpegArguments}</pre>
        </details>
      {/if}

      <!-- Verification report, when one exists -->
      {#if parseReport(selectedJob)}
        {@const checks = parseReport(selectedJob)}
        <div class="mt-4 border-t border-line-soft pt-4 border-line">
          {#if checks}<VerificationChecks {checks} />{/if}
        </div>
      {/if}
      {#if earlierAttempts.length > 0}
        <section class="attempt-timeline" aria-label={i18n.m.queue.attempts}>
          <h3>{i18n.m.queue.attempts}</h3>
          <ol class="attempt-list" aria-label={i18n.m.queue.attempts}>
            <li class="attempt-item current">
              <div class="attempt-card">
                <div class="attempt-card-top">
                  <strong>{t(i18n.m.queue.attempt_number, { number: selectedJob.executionAttempt ?? earlierAttempts.length + 1 })} · {i18n.m.queue.attempt_current}</strong>
                  <span class="badge {suspended ? 'tone-warn' : badgeClass(selectedJob.status)}">{suspended ? i18n.m.queue.now_paused : statusLabel(selectedJob.status)}</span>
                </div>
                <p>{selectedJob.workerName ?? i18n.m.dashboard.this_server} · {selectedJob.videoEncoder ?? '—'}</p>
                {#if selectedJob.retryReason === 'SoftwareDecode'}<p class="attempt-reason">{i18n.m.queue.retry_software_title}</p>{/if}
              </div>
            </li>
            {#each [...earlierAttempts].reverse() as attempt (attempt.number)}
              <li class="attempt-item">
                <div class="attempt-card">
                  <div class="attempt-card-top">
                    <strong>{t(i18n.m.queue.attempt_number, { number: attempt.number })} · {i18n.m.queue.attempt_rejected}</strong>
                    <time datetime={attempt.endedAt}>{new Date(attempt.endedAt).toLocaleString()}</time>
                  </div>
                  <p>{attempt.workerName ?? i18n.m.dashboard.this_server} · {attempt.videoEncoder ?? '—'}{#if attempt.hardwareDecoder} · {attempt.hardwareDecoder}{/if}</p>
                  {#if attempt.reason}<p class="attempt-reason">{attempt.reason === 'HardwareDecodeCorruption' ? i18n.m.queue.attempt_decode_corruption : attempt.reason}</p>{/if}
                  {#if attemptChecks(attempt).length > 0}
                    <details class="attempt-checks">
                      <summary>{i18n.m.queue.attempt_checks}</summary>
                      <div class="mt-3"><VerificationChecks checks={attemptChecks(attempt)} /></div>
                    </details>
                  {/if}
                </div>
              </li>
            {/each}
          </ol>
        </section>
      {/if}

      <section class="queue-diagnostic-action" aria-label={i18n.m.settings.diagnostics_title}>
        <div>
          <h3>{i18n.m.settings.diagnostics_title}</h3>
          {#if !diagnosticLookupPending}<p>{diagnosticCapture ? (diagnosticCapture.status === 'Recording' ? i18n.m.settings.diagnostics_recording : i18n.m.settings.diagnostics_off) + ` · ${diagnosticCapture.eventsStored} ${i18n.m.settings.diagnostics_events}` : i18n.m.settings.diagnostics_desc}</p>{/if}
        </div>
        {#if diagnosticLookupPending}
          <span class="queue-diagnostic-pending" role="status">{i18n.m.common.loading_short}</span>
        {:else if diagnosticCapture}
          <button class="btn min-h-11" disabled={downloadingDiagnostics} onclick={downloadJobDiagnostics}>{i18n.m.settings.diagnostics_download}</button>
        {:else}
          <button class="btn min-h-11" onclick={openDiagnosticSettings}>{i18n.m.queue.attempt_open_diagnostics}</button>
        {/if}
      </section>

          </div>
          {#if selectedJob.status === 'Transcoding' || selectedJob.status === 'Verifying'}
            <div class="queue-usage-heading">{i18n.m.dashboard.this_server} · {i18n.m.queue.host_usage}</div>
            <div class="queue-usage">
              <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="var(--accent)" />
              {#if selectedJob.status === 'Transcoding'}<UsageGraph label="GPU" data={activity.gpuHistory} current={activity.metrics?.gpuPercent ?? null} color="var(--ok)" unavailable={gpuUnavailable} detail={activity.metrics?.gpuEngine} />{/if}
            </div>
          {/if}
          <div class="queue-path"><span>{i18n.m.queue.col_file}</span><p>{selectedJob.relativePath ?? '—'}</p></div>
        </div>
        {#if isActive(selectedJob.status) || selectedJob.status === 'Failed' || selectedJob.status === 'Cancelled'}
        <footer class="queue-detail-actions">
                <!-- Actions -->
      <div class="flex flex-wrap gap-2">
        {#if selectedJob.status === 'ReadyToReplace' && selectedJob.verificationPassed && !selectedJob.finalizing}
          <button class="btn btn-primary px-3 py-1 text-xs" onclick={() => selectedJob && replace(selectedJob)} disabled={replacingAll || replacingId !== null}>
            {replacingId === selectedJob.id ? i18n.m.queue.action_replacing_ellipsis : i18n.m.queue.action_replace_original}
          </button>
        {/if}
        {#if selectedJob.status === 'AwaitingSizeReview'}
          <button class="btn btn-primary min-h-11 px-3 py-1 text-xs" onclick={() => selectedJob && approveSizePreflight(selectedJob)} disabled={approvingSizeId === selectedJob.id}>
            {approvingSizeId === selectedJob.id ? i18n.m.common.loading_short : i18n.m.queue.action_encode_anyway}
          </button>
        {/if}
        {#if selectedJob.status === 'Failed' || selectedJob.status === 'Cancelled'}
          <button class="btn btn-ghost" onclick={() => selectedJob && exclude(selectedJob)} disabled={excludingId === selectedJob.id} title={i18n.m.queue.exclude_title}>{excludingId === selectedJob.id ? i18n.m.queue.action_excluding : i18n.m.queue.action_exclude}</button>
          {#if isVmafOnlyFailure(selectedJob)}
            <button class="btn btn-primary min-h-11 px-3 py-1 text-xs" onclick={() => selectedJob && retry(selectedJob, true)} disabled={retryingId === selectedJob.id}>
              {retryingId === selectedJob.id ? i18n.m.queue.action_retrying_ellipsis : i18n.m.queue.action_retry_higher_quality}
            </button>
            <button class="btn min-h-11 px-3 py-1 text-xs" onclick={() => selectedJob && retry(selectedJob)} disabled={retryingId === selectedJob.id}>
              {i18n.m.queue.action_retry_same}
            </button>
          {:else}
            <button class="btn min-h-9 px-3 py-1 text-xs" onclick={() => selectedJob && retry(selectedJob)} disabled={retryingId === selectedJob.id}>
              {retryingId === selectedJob.id ? i18n.m.queue.action_retrying_ellipsis : i18n.m.queue.action_retry}
            </button>
          {/if}
          <button class="btn btn-danger px-3 py-1 text-xs" onclick={() => selectedJob && stopAndRemove(selectedJob)} disabled={removingId === selectedJob.id}>
            {removingId === selectedJob.id ? i18n.m.queue.action_removing_ellipsis : i18n.m.queue.action_remove_from_queue}
          </button>
        {/if}
        {#if isActive(selectedJob.status)}
          <button class="btn btn-danger px-3 py-1 text-xs" onclick={() => selectedJob && stopAndRemove(selectedJob)} disabled={removingId === selectedJob.id}>
            {removingId === selectedJob.id ? i18n.m.queue.action_stopping : i18n.m.queue.action_stop_remove}
          </button>
        {/if}
      </div>

        </footer>
        {/if}
      </dialog>
    {/if}

    <section aria-label={i18n.m.queue.next_recent}>
      <div class="queue-section-heading"><h2>{i18n.m.queue.next_recent}</h2><span>{remainingJobs.length}</span></div>
      {#if jobs.length > 0}
        <div class="queue-tools">
          <div class="queue-filters">
            {#each [['all', i18n.m.queue.filter_all], ['active', i18n.m.queue.filter_active], ['review', i18n.m.queue.filter_review], ['completed', i18n.m.queue.filter_completed], ['failed', i18n.m.queue.filter_failed], ['verified', i18n.m.queue.filter_verified], ['verifyFailed', i18n.m.queue.filter_verify_failed]] as [key, label]}
              <button class="focus-ring" aria-pressed={filter === key} onclick={() => selectFilter(key as typeof filter)}>{label} · {counts[key as keyof typeof counts]}</button>
            {/each}
          </div>
          <div class="queue-bulk">
            {#if readyToReplaceCount > 0}<button class="btn btn-primary" onclick={replaceAll} disabled={replacingAll || replacingId !== null} title={i18n.m.queue.replace_all_title}><Icon name="replace" />{replacingAll ? i18n.m.queue.action_replacing : t(i18n.m.queue.replace_all, { count: readyToReplaceCount.toLocaleString() })}</button>{/if}
            {#if pendingCount > 0 || erroredClearable > 0 || counts.completed > 0}<details class="queue-management"><summary class="focus-ring">{i18n.m.queue.manage_queue}</summary><div>
              {#if pendingCount > 0}<button class="btn btn-ghost" onclick={clearPending} disabled={clearingPending} title={i18n.m.queue.clear_queue_title + (counts.review > 0 ? ` ${t(i18n.m.queue.clear_review_included, { count: counts.review })}` : '')}>{clearingPending ? i18n.m.queue.clearing : t(i18n.m.queue.clear_queue, { count: pendingCount.toLocaleString() })}</button>{/if}
              {#if erroredClearable > 0}<button class="btn btn-ghost" onclick={() => clear('errored')} disabled={clearingScope !== null} title={i18n.m.queue.clear_errored_title}>{clearingScope === 'errored' ? i18n.m.queue.clearing : t(i18n.m.queue.clear_errored, { count: erroredClearable })}</button>{/if}
              {#if counts.completed > 0}<button class="btn btn-ghost" onclick={() => clear('finished')} disabled={clearingScope !== null || finishedClearable === 0} title={finishedClearable > 0 ? i18n.m.queue.clear_completed_title_available : i18n.m.queue.clear_completed_title_protected}>{clearingScope === 'finished' ? i18n.m.queue.clearing : finishedClearable > 0 ? t(i18n.m.queue.clear_completed, { count: finishedClearable }) : t(i18n.m.queue.completed_protected, { count: finishedProtected })}</button>{/if}
            </div></details>{/if}
          </div>
        </div>
      {/if}
      {#if loading}<div class="queue-empty" role="status">{i18n.m.common.loading_short}</div>
      {:else if visibleJobs.length > 0}
        <div class="queue-table-surface"><table class="queue-table">
          <thead><tr><th scope="col">{i18n.m.queue.col_file}</th><th scope="col">{i18n.m.queue.col_status}</th><th scope="col" class="verification-column">{i18n.m.queue.col_verification}</th><th scope="col" class="action-column"><span class="sr-only">{i18n.m.queue.view_job}</span></th></tr></thead>
          <tbody>{#each pagedJobs as job (job.id)}
            <tr class:selected-row={selectedJobId === job.id}>
              <td><button id={`queue-job-${job.id}`} class="queue-file focus-ring" onclick={(event) => selectRow(job.id, event)} aria-haspopup="dialog" aria-controls={selectedJobId === job.id ? 'queue-job-dialog' : undefined}><Thumbnail mediaFileId={job.mediaFileId} /><span><strong>{job.relativePath?.split(/[\\/]/).pop() ?? jobName(job)}</strong><small>{job.videoEncoder ?? job.enqueueReason ?? '—'}</small></span></button></td>
              <td><span class="badge {badgeClass(job.status)}">{statusLabel(job.status)}</span>{#if job.status === 'Queued' && job.waitingForWorker}<small class="queue-row-note text-warn">{i18n.m.queue.waiting_for_worker}</small>{:else if job.status === 'Failed'}<small class="queue-row-note text-bad">{jobFailureDescription(job.failureCategory, i18n.m)}</small>{:else if job.status === 'AwaitingSizeReview'}<small class="queue-row-note text-warn">{i18n.m.queue.size_review_title}</small>{/if}</td>
              <td class="verification-column">{#if job.verificationPassed !== null}<button class="queue-verification focus-ring" class:text-ok={job.verificationPassed} class:text-bad={!job.verificationPassed} onclick={(event) => selectRow(job.id, event)}>{job.verificationPassed ? i18n.m.queue.verify_passed : i18n.m.queue.verify_failed}</button>{#if job.outputSizeBytes != null}<small class="queue-row-note text-ink-3">{formatSize(job.outputSizeBytes)}</small>{/if}{:else}<span class="text-ink-4">—</span>{/if}</td>
              <td class="action-column">{#if job.status === 'ReadyToReplace' && job.verificationPassed && !job.finalizing}<button class="btn btn-primary" onclick={() => replace(job)} disabled={replacingAll || replacingId !== null}>{replacingId === job.id ? i18n.m.queue.action_replacing : i18n.m.queue.action_replace}</button>{:else if job.status === 'Failed' || job.status === 'Cancelled'}<button class="btn btn-ghost" onclick={(event) => selectRow(job.id, event)}>{i18n.m.queue.view_job}</button>{/if}</td>
            </tr>
          {/each}</tbody>
        </table></div>
        <div class="queue-pagination"><span>{t(i18n.m.queue.range, { start: (queuePageStart + 1).toLocaleString(), end: Math.min(queuePageStart + QUEUE_PAGE_SIZE, visibleJobs.length).toLocaleString(), total: visibleJobs.length.toLocaleString() })}</span>
          {#if queuePageCount > 1}<div><button class="btn btn-ghost" onclick={() => goToQueuePage(queuePage - 1)} disabled={queuePage <= 1} aria-label={i18n.m.queue.prev_page}><Icon name="arrow-left" /></button><span>{t(i18n.m.queue.page_of, { page: Math.min(queuePage, queuePageCount), count: queuePageCount })}</span><button class="btn btn-ghost" onclick={() => goToQueuePage(queuePage + 1)} disabled={queuePage >= queuePageCount} aria-label={i18n.m.queue.next_page}><Icon name="arrow-right" /></button></div>{/if}
        </div>
      {:else if !loadError}<p class="queue-empty">{jobs.length > 0 ? i18n.m.queue.no_matches : i18n.m.queue.empty}</p>{/if}
    </section>
  {/if}
</div>

<style>
  .queue-review-alert { width: 100%; display: flex; align-items: center; gap: .875rem; margin-bottom: 1.25rem; padding: 1rem 1.125rem; text-align: left; box-shadow: var(--lift-1); transition: transform 180ms ease, box-shadow 180ms ease; }
  .queue-review-alert:hover, .queue-review-alert:focus-visible { transform: translateY(-2px); box-shadow: var(--lift-3); }
  .queue-review-alert > :global(svg) { flex: none; }
  .queue-review-alert span { min-width: 0; flex: 1; display: grid; gap: .25rem; }
  .queue-review-alert strong { color: var(--ink); font-size: .8125rem; }
  .queue-review-alert small { color: var(--ink-3); font-size: .75rem; line-height: 1.45; }
  .queue-layout { max-width: 72rem; margin-inline: auto; }.queue-heading { display: flex; justify-content: space-between; align-items: flex-start; gap: 1.5rem; flex-wrap: wrap; margin-bottom: 1.5rem; }.queue-heading > div { flex: 1; min-width: 15rem; }.queue-heading .page-subtitle { max-width: 45rem; }
  .queue-tabs { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem; margin-bottom: 1.5rem; border-bottom: 1px solid var(--divide-soft); }.queue-tabs button { padding: .75rem 1rem; font-size: .8125rem; color: var(--ink-3); }.queue-tabs button[aria-pressed=true] { color: var(--accent); box-shadow: 0 2px 0 var(--accent); }.queue-tabs > span { margin-left: auto; color: var(--ink-3); font-size: .6875rem; padding: .5rem 0; }.queue-tabs strong { margin-left: .5rem; font-weight: 500; color: var(--ink-2); font-variant-numeric: tabular-nums; }
  .queue-section-heading { display: flex; justify-content: space-between; align-items: center; gap: 1rem; margin: 1.5rem 0 1rem; }.queue-section-heading h2 { font-size: .875rem; color: var(--ink-2); font-weight: 600; }.queue-section-heading span { font-size: .75rem; color: var(--ink-3); }.queue-working { display: grid; gap: .75rem; }.queue-working .queue-section-heading { margin: 0 0 .25rem; }.queue-idle { display: flex; align-items: center; gap: .75rem; font-size: .8125rem; color: var(--ink-3); padding: 1.25rem; border-radius: .875rem; background: var(--panel); }
  .queue-lanes { margin-bottom: 1.5rem; }.queue-lanes-heading { display: flex; align-items: baseline; flex-wrap: wrap; gap: .25rem 1rem; margin-bottom: .75rem; }.queue-lanes-heading h2 { font-size: .8125rem; font-weight: 600; color: var(--ink-2); }.queue-lanes-heading p { font-size: .6875rem; color: var(--ink-3); }.queue-lane-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 15rem), 1fr)); gap: .625rem; }.queue-lane { min-width: 0; padding: .875rem 1rem; transition: transform .18s ease, box-shadow .18s ease; }.queue-lane:hover { transform: translateY(-2px); box-shadow: var(--lift-2); }.queue-lane-head { display: flex; justify-content: space-between; align-items: baseline; gap: .5rem; color: var(--ink-2); font-size: .75rem; }.queue-lane-head strong { flex: none; font-size: 1rem; color: var(--ink); font-variant-numeric: tabular-nums; }.queue-lane p { color: var(--ink-3); font-size: .6875rem; margin-top: .4rem; }.queue-lane small { display: block; color: var(--ink-3); font-size: .6875rem; line-height: 1.45; margin-top: .625rem; overflow-wrap: anywhere; }.queue-phase-note { margin-top: .75rem; font-size: .75rem; color: var(--ink-3); }
  .queue-execution-path { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: .75rem; margin-bottom: 1.25rem; padding: 1rem; border: 1px solid var(--divide-soft); border-radius: .75rem; background: var(--sunken); }.queue-execution-path h3 { grid-column: 1/-1; margin: 0; font-size: .75rem; color: var(--ink-3); font-weight: 600; }.queue-execution-path div { min-width: 0; display: grid; gap: .25rem; font-size: .6875rem; color: var(--ink-3); }.queue-execution-path strong { color: var(--ink); font-size: .75rem; font-weight: 600; overflow-wrap: anywhere; }
  @media(max-width: 900px) { .queue-lane-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  @media(max-width: 420px) { .queue-lane-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }.queue-lane { padding: .75rem; } }
  @media(max-width: 340px) { .queue-lane-grid { grid-template-columns: 1fr; } }
  @media(max-width: 420px) { .queue-execution-path { grid-template-columns: 1fr; } }
  .queue-detail { width: 52rem; grid-template-rows: auto minmax(0, 1fr) auto; }
  .queue-detail-heading { position: relative; display: flex; align-items: center; gap: 1.5rem; padding: 1.5rem 2rem; background: linear-gradient(135deg, var(--raised), var(--panel)); }
  .queue-detail-poster { flex-shrink: 0; overflow: hidden; border-radius: .625rem; box-shadow: var(--lift-3); }
  .queue-detail-poster :global([data-thumbnail]) { width: 5rem; height: 7.5rem; }
  .queue-detail-identity { min-width: 0; padding-right: 1.75rem; }
  .queue-detail-heading p { font-size: .6875rem; color: var(--ink-3); margin-bottom: .625rem; }
  .queue-detail-heading h2 { font-size: clamp(1.125rem, 3vw, 1.5rem); line-height: 1.3; font-weight: 650; letter-spacing: -.025em; overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  .queue-detail-heading .btn { position: absolute; right: .75rem; top: .75rem; min-height: 2.75rem; min-width: 2.75rem; }
  .queue-detail-status { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem .75rem; margin-top: .75rem; font-size: .75rem; color: var(--ink-3); overflow-wrap: anywhere; }
  .queue-detail-body { min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; padding: 1.5rem 2rem; }
  .attempt-timeline { margin-top: 1.5rem; border-top: 1px solid var(--divide-soft); padding-top: 1.25rem; }
  .attempt-timeline h3, .queue-diagnostic-action h3 { color: var(--ink); font-size: .875rem; font-weight: 650; }
  .attempt-list { list-style: none; margin: 1rem 0 0; padding: 0 0 0 .375rem; }
  .attempt-item { position: relative; border-left: 1px solid var(--divide-soft); padding: 0 0 1rem 1.25rem; }
  .attempt-item:last-child { border-left-color: transparent; padding-bottom: 0; }
  .attempt-item::before { content: ''; position: absolute; top: 1rem; left: -.375rem; width: .6875rem; height: .6875rem; border-radius: 50%; background: var(--ink-4); box-shadow: 0 0 0 .25rem var(--panel); }
  .attempt-item.current::before { background: var(--accent); }
  .attempt-card { min-width: 0; border: 1px solid var(--edge); border-radius: .75rem; padding: 1rem; background: var(--raised); color: var(--ink-3); font-size: .75rem; line-height: 1.5; box-shadow: var(--lift-1); transition: box-shadow 180ms ease; }
  .attempt-item.current .attempt-card { background: var(--lit); }
  .attempt-card:hover, .attempt-card:focus-within { box-shadow: var(--lift-3); }
  .attempt-card-top { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: .5rem 1rem; }
  .attempt-card-top strong { color: var(--ink); font-size: .8125rem; }
  .attempt-card-top time { font: .6875rem/1.4 ui-monospace, monospace; color: var(--ink-3); }
  .attempt-card > p { margin-top: .5rem; overflow-wrap: anywhere; }
  .attempt-card .attempt-reason { color: var(--ink-2); }
  .attempt-checks { margin-top: .875rem; border-top: 1px solid var(--divide-soft); padding-top: .75rem; }
  .attempt-checks summary { cursor: pointer; color: var(--accent); font-weight: 600; padding: .25rem 0; }
  .queue-diagnostic-action { display: flex; align-items: center; justify-content: space-between; gap: 1rem; margin-top: 1.5rem; border: 1px solid var(--edge); border-radius: .75rem; padding: 1rem; background: var(--raised); box-shadow: var(--lift-1); transition: box-shadow 180ms ease; }
  .queue-diagnostic-action:hover, .queue-diagnostic-action:focus-within { box-shadow: var(--lift-3); }
  .queue-diagnostic-action > div { min-width: 0; }
  .queue-diagnostic-action p { max-width: 44ch; margin-top: .375rem; color: var(--ink-3); font-size: .75rem; line-height: 1.5; }
  .queue-diagnostic-action .btn { flex: none; }
  .queue-diagnostic-pending { flex: none; color: var(--ink-3); font-size: .75rem; }
  .queue-detail-specs:first-child { margin-top: 0; }
  .queue-detail-specs { margin-top: 1.5rem; }.queue-detail-specs :global(dl) { grid-template-columns: repeat(2, minmax(0, 1fr)); }.queue-detail-specs :global(dl > div) { min-width: 0; padding: .75rem 0; border-bottom: 1px solid var(--divide-soft); font-size: .8125rem; }.queue-detail-specs :global(dd) { min-width: 0; overflow-wrap: anywhere; }.queue-detail-actions { padding: 1rem 2rem; background: var(--raised); box-shadow: inset 0 1px 0 var(--divide-soft); }.queue-detail-actions > div { justify-content: flex-end; }.queue-detail-actions :global(.btn) { min-height: 2.75rem; }.queue-usage-heading { color: var(--ink-3); font-size: .75rem; margin: 1.5rem 0 .75rem; }.queue-usage { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .75rem; }.queue-path { margin-top: 1.5rem; font-size: .75rem; color: var(--ink-3); }.queue-path p { margin-top: .5rem; font: .6875rem/1.8 ui-monospace, monospace; overflow-wrap: anywhere; }
  .queue-tools { display: flex; flex-direction: column; gap: .75rem; margin-bottom: 1rem; }.queue-filters { display: flex; flex-wrap: wrap; gap: .25rem; }.queue-filters button { padding: .625rem .75rem; font-size: .75rem; color: var(--ink-3); border-radius: .5rem; }.queue-filters button[aria-pressed=true] { background: var(--raised); color: var(--ink); box-shadow: var(--lift-1); }.queue-bulk { display: flex; align-items: flex-start; justify-content: flex-end; gap: .75rem; flex-wrap: wrap; }.queue-bulk .btn { font-size: .75rem; }.queue-management { font-size: .75rem; color: var(--ink-3); }.queue-management summary { padding: .75rem; cursor: pointer; border-radius: .5rem; }.queue-management > div { display: flex; flex-wrap: wrap; padding: .5rem; gap: .5rem; background: var(--panel); border-radius: .75rem; max-width: 100%; }
  .queue-table-surface { border-radius: .875rem; background: var(--panel); box-shadow: var(--lift-1); overflow: clip; }.queue-table { width: 100%; table-layout: fixed; border-collapse: collapse; text-align: left; }.queue-table th { background: var(--raised); color: var(--ink-3); padding: .875rem 1rem; font-size: .6875rem; font-weight: 500; }.queue-table th:first-child { width: 43%; }.queue-table th:nth-child(2) { width: 25%; }.queue-table th:nth-child(3) { width: 17%; }.queue-table th:last-child { width: 15%; }.queue-table td { padding: 1rem; border-top: 1px solid var(--divide-soft); font-size: .75rem; color: var(--ink-2); overflow-wrap: anywhere; }.queue-table .badge { white-space: normal; }.queue-table tr:hover td,.queue-table .selected-row td { background: var(--lit); }.queue-file { display: flex; align-items: center; width: 100%; min-width: 0; gap: .875rem; text-align: left; border-radius: .375rem; }.queue-file > span { min-width: 0; }.queue-file strong { display: block; color: var(--ink); font-size: .8125rem; line-height: 1.5; font-weight: 500; overflow-wrap: anywhere; }.queue-file:hover strong { color: var(--accent); }.queue-file small { display: block; font-size: .6875rem; margin-top: .375rem; color: var(--ink-3); overflow-wrap: anywhere; }.queue-row-note { display: block; font-size: .6875rem; margin-top: .375rem; }.queue-verification { border-radius: .25rem; text-align: left; }.action-column .btn { font-size: .75rem; padding: .5rem .75rem; max-width: 100%; white-space: normal; }
  .queue-empty { padding: 2rem 1.5rem; background: var(--panel); border-radius: .875rem; text-align: center; font-size: .8125rem; color: var(--ink-3); }.queue-pagination { display: flex; justify-content: space-between; align-items: center; gap: 1rem; margin-top: 1rem; color: var(--ink-3); font-size: .75rem; }.queue-pagination > div { display: flex; align-items: center; gap: .5rem; }
  @media(max-width: 639px) { .queue-heading { gap: 1rem; }.queue-tabs > span { width: 100%; margin: 0; }.queue-tabs button { padding: .625rem .75rem; }.verification-column,.action-column { display: none; }.queue-table th:first-child { width: 63%; }.queue-table th:nth-child(2) { width: 37%; }.queue-table th,.queue-table td { padding: .875rem .75rem; }.queue-file { gap: .625rem; }.queue-file strong { font-size: .75rem; }.queue-detail-heading,.queue-detail-body,.queue-detail-actions { padding: 1rem; }.queue-detail-specs :global(dl) { grid-template-columns: 1fr; }.queue-usage { grid-template-columns: 1fr; }.queue-filters button { font-size: .6875rem; padding: .625rem; }.queue-bulk { justify-content: flex-start; }.queue-detail-actions :global(.btn) { flex: 1; }.queue-pagination { flex-wrap: wrap; } }
  @media(max-width: 639px) { .queue-diagnostic-action { align-items: stretch; flex-direction: column; }.queue-diagnostic-action .btn { width: 100%; }.attempt-card-top { align-items: flex-start; flex-direction: column; } }
  @media(prefers-reduced-motion: reduce) { .attempt-card, .queue-diagnostic-action { transition: none; } }
  @media(max-width: 639px) {
    .queue-detail-heading { gap: 1rem; }
    .queue-detail-poster :global([data-thumbnail]) { width: 3.5rem; height: 5.25rem; }
    .queue-detail-actions > div { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .queue-detail-actions :global(.btn:only-child) { grid-column: 1/-1; }
    .queue-detail-actions :global(.btn) { white-space: normal; }
  }
  @media(max-height: 540px) {
    .queue-detail-heading { gap: 1rem; padding: .75rem 1rem; }
    .queue-detail-poster :global([data-thumbnail]) { width: 2.5rem; height: 3.75rem; }
    .queue-detail-heading h2 { -webkit-line-clamp: 1; line-clamp: 1; font-size: 1.125rem; }
    .queue-detail-heading p { margin-bottom: .25rem; }
    .queue-detail-status { margin-top: .375rem; }
    .queue-detail-body { padding: 1rem; }
    .queue-detail-actions { padding: .5rem 1rem; }
  }
</style>
