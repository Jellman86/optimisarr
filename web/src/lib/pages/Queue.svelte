<script lang="ts">
  import InfoTip from '../components/InfoTip.svelte'
  import CapturedDiagnosticTimeline from '../components/CapturedDiagnosticTimeline.svelte'
  import { tick } from 'svelte'
  import { modal } from '../modal'
  import DetailDialog from '../components/DetailDialog.svelte'
  import { isWorkingJob, isJobSuspended, jobLocation, verificationPhase } from '../job-presentation'
  import WorkingJob from '../components/WorkingJob.svelte'
  import PosterGlow from '../components/PosterGlow.svelte'
  import JobProgress from '../components/JobProgress.svelte'
  import JobStages from '../components/JobStages.svelte'
  import MediaCompare from '../components/MediaCompare.svelte'
  import { api, type BatchFailure, type BulkReplacementResult, type DiagnosticCapture, type Job, type JobAttemptSnapshot, type PerceptualSearchSummary, type SoundtrackSearchSummary, type QueueStatus, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize, sizeChangePercent } from '../format'
  import { createJobsConnection, type JobProgress as Telemetry } from '../realtime'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { jobFailureStory } from '../i18n/jobErrors'
  import { router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import { counts as shellStatus } from '../stores/counts.svelte'
  import Icon from '../components/Icon.svelte'
  import OkMark from '../components/OkMark.svelte'
  import SizeChange from '../components/SizeChange.svelte'
  import Banner from '../components/Banner.svelte'
  import PageHeader from '../components/PageHeader.svelte'
  import UsageGraph from '../components/UsageGraph.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import JobResult from '../components/JobResult.svelte'
  import { pictureQualityWasMeasured, plainCheckName } from '../plain-checks'
  import AudioQualityReport from '../components/AudioQualityReport.svelte'
  import SoundtrackQualityReport from '../components/SoundtrackQualityReport.svelte'
  import SoundtrackSearchReport from '../components/SoundtrackSearchReport.svelte'
  import ImagePerceptualReport from '../components/ImagePerceptualReport.svelte'
  import PerceptualSearchReport from '../components/PerceptualSearchReport.svelte'
  import FailuresPanel from '../components/FailuresPanel.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'
  import PlaybackHolds from '../components/PlaybackHolds.svelte'
  import BatchResult from '../components/BatchResult.svelte'
  import ConfirmDialog from '../components/ConfirmDialog.svelte'
  import DataTable from '../components/DataTable.svelte'
  import SelectionCheckbox from '../components/SelectionCheckbox.svelte'
  import SelectionToolbar from '../components/SelectionToolbar.svelte'
  import Segmented from '../components/Segmented.svelte'
  import Chip from '../components/Chip.svelte'
  import FilterBar from '../components/FilterBar.svelte'
  import { createSelection } from '../selection.svelte'

  let jobs = $state<Job[]>([])
  let queueStatus = $state<QueueStatus | null>(null)
  // Live transcode telemetry keyed by job id, pushed over SignalR between reloads.
  let live = $state<Record<number, Telemetry>>({})
  let error = $state<string | null>(null)
  let loading = $state(true)
  let removingId = $state<number | null>(null)
  let replacingId = $state<number | null>(null)
  let replacingAll = $state(false)
  let replacementTarget = $state<Job | 'all' | null>(null)
  let replacementError = $state<string | null>(null)
  let replacementFailures = $state<BulkReplacementResult['failures']>([])
  const replacementPending = $derived(replacingAll || replacingId !== null)

  function closeReplacement() {
    if (!replacementPending) replacementTarget = null
  }

  function replaceAll() {
    replacementError = null
    replacementFailures = []
    replacementTarget = 'all'
  }

  // Set when the dialog is accepting a failed job below its picture-quality target, not replacing a passed one.
  let acceptingBelowTarget = $state(false)

  function replace(job: Job) {
    replacementError = null
    replacementFailures = []
    acceptingBelowTarget = false
    replacementTarget = job
  }

  function acceptBelowTarget(job: Job) {
    replacementError = null
    replacementFailures = []
    acceptingBelowTarget = true
    replacementTarget = job
  }

  async function confirmReplacement() {
    if (!replacementTarget || replacementPending) return
    replacementError = null
    replacementFailures = []
    if (replacementTarget === 'all') await performReplaceAll()
    else await performReplace(replacementTarget, acceptingBelowTarget)
  }

  // A score is rounded down, so 94.96 against 95 never reads as "Scored 95, target was 95".
  function formatScore(value: number, round: 'down' | 'nearest' = 'nearest'): string {
    const shown = round === 'down' ? Math.floor(value * 10) / 10 : value
    return shown.toLocaleString(i18n.locale, { maximumFractionDigits: 1 })
  }

  function belowTargetHeading(job: Job): string {
    const shortfalls = job.belowTarget?.shortfalls ?? []
    if (shortfalls.length === 0) return i18n.m.queue.action_accept_anyway
    return shortfalls.map((shortfall) => t(
      shortfall.target === 'FifthPercentile' ? i18n.m.queue.accept_heading_fifth : i18n.m.queue.accept_heading_harmonic,
      { score: formatScore(shortfall.score, 'down'), target: formatScore(shortfall.minimum) })).join('; ')
  }


  function belowTargetRefusal(refusal: string): string | null {
    if (refusal === 'NotVideo') return i18n.m.queue.accept_refused_not_video
    if (refusal === 'SuperResolution') return i18n.m.queue.accept_refused_super_resolution
    if (refusal === 'SavesToAnotherFolder') return i18n.m.queue.accept_refused_saves_elsewhere
    if (refusal === 'NoVerifiedIdentity') return i18n.m.queue.accept_refused_no_identity
    return null
  }
  let retryingId = $state<number | null>(null)
  let approvingSizeId = $state<number | null>(null)
  let excludingId = $state<number | null>(null)
  let clearingScope = $state<'errored' | 'finished' | null>(null)
  let clearingPending = $state(false)
  let filter = $state<'all' | 'active' | 'review' | 'ready' | 'completed' | 'failed'>('all')
  // Queue (live work) vs Failures (failed jobs grouped by reason, with the captured ffmpeg log).
  // /queue/failures opens straight onto the Failures tab, so the Dashboard can link to the why.
  let activeTab = $state<'queue' | 'failures'>(router.path === '/queue/failures' ? 'failures' : 'queue')

  let selectedJobId = $state<number | null>(null)
  // The lane breakdown starts folded to one row of counts so the list leads the page.
  let lanesOpen = $state(false)
  let toolbarHeight = $state(0)
  // The multi-select over the job table. The operator picks rows and acts on them in bulk; the
  // selection survives a change of filter and a change of page, so a large queue can be worked
  // through without losing the rows already picked.
  let selection = createSelection()
  let batchAction = $state<'retry' | 'remove' | null>(null)
  let batchConfirmOpen = $state(false)
  let batchBusy = $state(false)
  let batchOutcome = $state<{ title: string; succeeded: number; failures: BatchFailure[] } | null>(null)
  let diagnosticCapture = $state<DiagnosticCapture | null>(null)
  let diagnosticLookupPending = $state(false)
  let downloadingDiagnostics = $state(false)
  let diagnosticSummaryCopied = $state(false)
  let diagnosticSummaryText = $state('')
  let diagnosticSummaryError = $state('')
  let captureLookup = 0
  let detailOpener: HTMLElement | null = null
  let loadError = $state<string | null>(null)
  let requestId = 0
  let disposed = false
  let progressRevision = 0
  // Whether replacing is locked for every library, and how long a replaced original is kept.
  // Unknown until the settings arrive; the server refuses a replacement under dry-run either way.
  let dryRun = $state(false)
  let retentionDays = $state<number | null>(null)
  // How long Accept anyway's original stays in Quarantine, worded as JobResult says it for Replace.
  const acceptUndo = $derived(retentionDays === null
    ? i18n.m.jobResult.replace_undo
    : retentionDays === 0
      ? i18n.m.jobResult.replace_undo_kept
      : plural(retentionDays, i18n.m.jobResult.replace_undo_days_one, i18n.m.jobResult.replace_undo_days_other))

  async function loadReplacementSettings() {
    try {
      const settings = await api.settings()
      if (disposed) return
      dryRun = settings.dryRunMode
      retentionDays = settings.replacementQuarantineRetentionDays
    } catch {
      /* The result card then says only what holds whatever the settings are. */
    }
  }
  const progressVersions = new Map<number, number>()

  // Updates arrive over SignalR (jobsChanged + jobProgress). A slow poll is kept
  // only as a safety net and to refresh queue status (free disk, running counts),
  // which is not pushed.
  $effect(() => {
    void load()
    void loadReplacementSettings()
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

  // The status strip pauses and resumes the queue from any page; follow it at once rather than
  // on the next poll, so the paused banner below never contradicts the strip above.
  let seenRevision = shellStatus.revision
  $effect(() => {
    if (shellStatus.revision !== seenRevision) {
      seenRevision = shellStatus.revision
      void load()
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
  function isInProgress(status: string) {
    return isActive(status) && status !== 'ReadyToReplace' && status !== 'AwaitingSizeReview'
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
      // Each filter is one status bucket, so the counts never overlap: work still moving, work
      // waiting on a decision, and finished outcomes.
      case 'active': return isInProgress(job.status)
      case 'review': return job.status === 'AwaitingSizeReview'
      case 'ready': return job.status === 'ReadyToReplace'
      case 'completed': return job.status === 'Completed'
      case 'failed': return job.status === 'Failed'
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
  // What "Clear errored" really removes: the server clears failed and cancelled jobs, except
  // those still holding a rollback, so the button is shown and counted from the same rows.
  let erroredClearable = $derived(jobs.filter(job => (job.status === 'Failed' || job.status === 'Cancelled') && job.clearable).length)
  let counts = $derived({
    all: remainingJobs.length,
    active: remainingJobs.filter(job => isInProgress(job.status)).length,
    review: remainingJobs.filter(job => job.status === 'AwaitingSizeReview').length,
    ready: remainingJobs.filter(job => job.status === 'ReadyToReplace').length,
    completed: remainingJobs.filter(job => job.status === 'Completed').length,
    failed: remainingJobs.filter(job => job.status === 'Failed').length,
  })
  let visibleJobs = $derived(remainingJobs.filter(matchesFilter))

  // Render the table one page at a time so a large queue (thousands of jobs) stays responsive; the
  // chips above still count the whole set. The page is clamped, so it stays valid as jobs change.
  const QUEUE_PAGE_SIZE = 100
  let queuePage = $state(1)
  let queuePageCount = $derived(Math.max(1, Math.ceil(visibleJobs.length / QUEUE_PAGE_SIZE)))
  let queuePageStart = $derived((Math.min(queuePage, queuePageCount) - 1) * QUEUE_PAGE_SIZE)
  let pagedJobs = $derived(visibleJobs.slice(queuePageStart, queuePageStart + QUEUE_PAGE_SIZE))

  // The bulk actions work on the rows the operator picked. A range is chosen over the rows on
  // screen; "select all" covers every row the current filter shows, pages included.
  let filteredJobIds = $derived(visibleJobs.map((job) => job.id))
  let pagedJobIds = $derived(pagedJobs.map((job) => job.id))
  // Every picked job, not only the ones the current filter shows: a selection survives a change
  // of filter, so the actions have to account for the rows that are no longer on screen.
  let selectedJobs = $derived(jobs.filter((job) => selection.isSelected(job.id)))
  // Only a failed or cancelled job can be retried, so a retry covers that part of the selection.
  // The confirmation says so before anything is sent, and the jobs that cannot be retried keep
  // their place in the selection.
  let retryableJobIds = $derived(
    selectedJobs.filter((job) => job.status === 'Failed' || job.status === 'Cancelled').map((job) => job.id),
  )
  // A job the server still counts as active cannot be removed as it stands: the batch has to be
  // told to stop those first, and the operator is told how many rows that will be.
  let selectedActive = $derived(selectedJobs.filter((job) => isActive(job.status)).length)

  function toggleJobSelection(jobId: number, extend: boolean) {
    selection.toggle(jobId, pagedJobIds, extend)
  }

  function toggleSelectAllJobs() {
    if (selection.isEverySelected(filteredJobIds)) selection.remove(filteredJobIds)
    else selection.selectAll(filteredJobIds)
  }

  function askBatch(action: 'retry' | 'remove') {
    batchAction = action
    batchConfirmOpen = true
  }

  async function runBatch() {
    if (!batchAction || batchBusy || selection.isEmpty) return
    const action = batchAction
    batchBusy = true
    try {
      const result = action === 'retry'
        ? await api.batchRetryJobs(retryableJobIds)
        // Only an active job is stopped when the operator is told it will be. Everything else
        // is removed as it stands, and a job that became active meanwhile is refused.
        : await api.batchRemoveJobs(selection.ids, selectedActive > 0)
      batchOutcome = {
        title: action === 'retry' ? i18n.m.queue.batch_result_retry : i18n.m.queue.batch_result_remove,
        succeeded: result.succeeded.length,
        failures: result.failures,
      }
      // The rows that were handled leave the selection; the ones that were refused stay picked,
      // so the operator can act on them again.
      selection.remove(result.succeeded)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_retry
    } finally {
      batchBusy = false
      batchAction = null
      batchConfirmOpen = false
    }
  }

  function selectFilter(key: typeof filter) {
    filter = key
    queuePage = 1
  }

  // The Queue and the Failures tab show different sets of rows, so a pick made on one does not
  // belong to the other: leaving a tab drops the selection instead of carrying rows the
  // operator never saw into a list that is not showing them.
  function selectTab(tab: 'queue' | 'failures') {
    if (tab === activeTab) return
    activeTab = tab
    selection.clear()
  }

  async function showSizeReviews() {
    selectFilter('review')
    await tick()
    const heading = document.getElementById('queue-list-title')
    heading?.focus({ preventScroll: true })
    heading?.scrollIntoView({ block: 'start' })
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
    diagnosticSummaryCopied = false
    diagnosticSummaryText = ''; diagnosticSummaryError = ''
    diagnosticLookupPending = true
    const lookup = ++captureLookup
    error = null
    await tick()
    document.getElementById('queue-detail-title')?.focus({ preventScroll: true })
    void loadDiagnosticCapture(id, lookup)
  }

  async function loadDiagnosticCapture(jobId: number, lookup: number) {
    try {
      const [latest, retained] = await Promise.all([api.diagnosticCapture(), api.diagnosticCaptures(jobId)])
      if (selectedJobId === jobId && captureLookup === lookup)
        diagnosticCapture = (Array.isArray(retained) ? retained[0] : null)
          ?? (latest && (latest.scopedJobId === null || latest.scopedJobId === jobId) ? latest : null)
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

  async function copyDiagnosticSummary() {
    if (!selectedJob || !diagnosticCapture) return
    downloadingDiagnostics = true
    diagnosticSummaryCopied = false; diagnosticSummaryText = ''; diagnosticSummaryError = ''
    try {
      const blob = await api.diagnosticBundle(diagnosticCapture.id, selectedJob.id)
      const bundle = JSON.parse(await blob.text())
      const summary = { manifestId: bundle.manifest.manifestId, schemaVersion: bundle.manifest.schemaVersion,
        collectedAt: bundle.manifest.collectedAt, job: bundle.job, attempts: bundle.attempts,
        participants: bundle.manifest.participants,
        events: bundle.events.slice(-100).map((event: { reasonCode: string; attempt: number; workerId: number | null; receivedAt: string; details?: unknown }) =>
          ({ reasonCode: event.reasonCode, attempt: event.attempt, workerId: event.workerId, receivedAt: event.receivedAt, details: event.details })), omissions: bundle.manifest.omissions }
      // Paths are deliberately excluded even if the operator selected them for the private bundle.
      delete summary.job.path
      const text = JSON.stringify(summary, null, 2)
      try {
        if (!navigator.clipboard?.writeText) { diagnosticSummaryText = text; return }
        await navigator.clipboard.writeText(text)
        diagnosticSummaryCopied = true
      } catch { diagnosticSummaryText = text }
    } catch (cause) { diagnosticSummaryError = cause instanceof Error ? cause.message : String(cause) }
    finally { downloadingDiagnostics = false }
  }

  async function openDiagnosticSettings() {
    await closeDetails()
    router.go('/settings/system#diagnostic-capture')
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

  // What a size change means, in words: a saving, a file that came out the same size, or a
  // file that grew. The direction is in the wording, so a larger output is never dressed up
  // as a saving, and a change that cannot be judged gets no words beside its "—".
  function sizeChangeSentence(change: number | null): string | null {
    if (change === null) return null
    if (change < 0) return t(i18n.m.queue.percent_smaller, { percent: Math.abs(change) })
    if (change > 0) return t(i18n.m.queue.percent_larger, { percent: change })
    return i18n.m.queue.percent_unchanged
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

  function searchReport(job: Job): PerceptualSearchSummary | null {
    const final = fullReport(job)?.perceptualSearch
    if (final) return final
    try {
      const partial = JSON.parse(job.perceptualSearchJson ?? 'null') as PerceptualSearchSummary | null
      return partial?.result?.decision ? partial : null
    } catch { return null }
  }

  function soundtrackSearch(job: Job): SoundtrackSearchSummary | null {
    const final = fullReport(job)?.soundtrackSearch
    if (final) return final
    try { return JSON.parse(job.soundtrackSearchJson ?? 'null') as SoundtrackSearchSummary | null } catch { return null }
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

  // A higher-quality retry is worth offering only when a measured score fell short and the
  // quality search has not already tried its highest setting: otherwise it repeats a known miss.
  function canRetryHigher(job: Job): boolean {
    if (!isVmafOnlyFailure(job) || searchReport(job)?.result?.decision?.fellBack === true) return false
    return (fullReport(job)?.checks ?? []).some((check) => check.outcome === 'Failed' && pictureQualityWasMeasured(check))
  }

  function vmafSamplingLabel(value: string): string {
    if (value === 'Full file') return i18n.m.queue.vmaf_sampling_full
    if (value === 'Three 40-second samples (early, middle and late)') return i18n.m.queue.vmaf_sampling_three
    return value
  }

  async function performReplaceAll() {
    replacingAll = true
    try {
      const result = await api.replaceReadyJobs()
      replacementFailures = result.failures
      selectedJobId = null
      await load()
      if (result.failures.length > 0) {
        replacementError = t(i18n.m.queue.bulk_replace_failed, { count: result.failures.length.toLocaleString() })
      } else {
        replacementTarget = null
        if (result.replaced > 0) router.go('/quarantine')
      }
    } catch (err) {
      replacementError = err instanceof Error ? err.message : i18n.m.queue.error_replace_all
    } finally {
      replacingAll = false
    }
  }

  async function performReplace(job: Job, belowTarget = false) {
    replacingId = job.id
    try {
      if (belowTarget) await api.acceptJobBelowTarget(job.id, job.executionAttempt ?? 0)
      else await api.replaceFromJob(job.id)
      await load()
      replacementTarget = null
      router.go('/quarantine')
    } catch (err) {
      replacementError = err instanceof Error ? err.message : belowTarget ? i18n.m.queue.error_accept : i18n.m.queue.error_replace
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
  // Pending = not-yet-applied work that a reset can safely discard (queued + verified-but-unreplaced).
  let pendingCount = $derived(jobs.filter((j) => j.status === 'Queued' || j.status === 'ReadyToReplace' || j.status === 'AwaitingSizeReview').length)
</script>

<div class="queue-layout">
  <PageHeader title={i18n.m.nav.queue} subtitle={i18n.m.queue.subtitle}>
    {#snippet actions()}
      <Segmented label={i18n.m.queue.view_label} value={activeTab} onselect={selectTab} items={[
        { value: 'queue', label: i18n.m.nav.queue, count: activeCount > 0 ? activeCount : undefined },
        { value: 'failures', label: i18n.m.queue.tab_failures, count: failedCount > 0 ? failedCount : undefined },
      ]} />
    {/snippet}
  </PageHeader>
  {#if activeTab === 'failures'}
    <FailuresPanel />
  {:else}
    {#if counts.review > 0}
      <button class="queue-review-alert card tone-warn focus-ring" onclick={showSizeReviews} aria-controls="queue-list">
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
    {#if queueStatus.playbackHolds?.length}
      <p class="font-semibold">{i18n.m.queue.playback_waiting_title}</p>
      <p class="mt-0.5 text-xs">{i18n.m.queue.playback_waiting_hint}</p>
    {:else}
      {t(i18n.m.queue.dispatch_paused, { reason: (queueStatus.blockedReason ?? '').replace(/\.$/, '') })}
    {/if}
    <PlaybackHolds holds={queueStatus.playbackHolds ?? []} />
  </div>
{:else if queueStatus?.waitingReason}
  <div class="card tone-warn mb-4 p-3 text-sm">
    {t(i18n.m.queue.waiting_window, { reason: queueStatus.waitingReason })}
  </div>
{/if}

    {#if queueStatus?.workloadLanes?.length && (activeCount > 0 || queuedCount > 0)}
      {@const lanes = queueStatus.workloadLanes.filter(lane => lane.lane !== 'Finalization' || lane.active > 0 || lane.waiting > 0)}
      {#if lanes.length > 0}
      <!-- One row of counts, so the list stays high on the screen; the disclosure says what is
           waiting in each lane and why, for when the counts are not enough. -->
      <section class="mb-3" aria-label={i18n.m.queue.lanes_title}>
        <div class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1.5">
          <button type="button" class="btn btn-sm btn-ghost -ml-2.5 font-semibold text-ink-2" aria-expanded={lanesOpen} aria-controls={lanesOpen ? 'queue-lane-details' : undefined} onclick={() => (lanesOpen = !lanesOpen)}><Icon name="chevron" class="h-3.5 w-3.5 transition-transform motion-reduce:transition-none {lanesOpen ? '' : '-rotate-90'}" />{i18n.m.queue.lanes_title}</button>
          <ul class="flex min-w-0 flex-wrap items-center gap-1.5">
            {#each lanes as lane (lane.lane)}
              <li class="min-w-0"><Chip><span>{laneLabel(lane.lane)}</span><strong class="shrink-0 font-semibold tabular-nums text-ink">{lane.active} / {lane.capacity}</strong>{#if lane.waiting > 0}<span aria-hidden="true">·</span><span class="shrink-0 font-semibold tabular-nums text-ink">{t(i18n.m.queue.lane_waiting, { count: lane.waiting })}</span>{/if}</Chip></li>
            {/each}
          </ul>
        </div>
        {#if lanesOpen}
          <div id="queue-lane-details" class="card mt-2 grid gap-3 p-4">
            <p class="text-caption text-ink-3">{i18n.m.queue.lanes_hint}</p>
            <dl class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,14rem),1fr))] gap-x-6 gap-y-3">
              {#each lanes as lane (lane.lane)}
                <div class="min-w-0">
                  <dt class="flex items-baseline justify-between gap-2 text-label font-semibold text-ink-2"><span class="min-w-0">{laneLabel(lane.lane)}</span><span class="flex-none tabular-nums text-ink">{lane.active} / {lane.capacity}</span></dt>
                  <dd class="mt-0.5 text-caption text-ink-3">{t(i18n.m.queue.lane_waiting, { count: lane.waiting })}</dd>
                  {#if lane.reason && lane.waiting > 0}<dd class="mt-1 text-caption text-ink-2 [overflow-wrap:anywhere]">{lane.reason}</dd>{/if}
                </div>
              {/each}
            </dl>
          </div>
        {/if}
      </section>
      {/if}
    {/if}


    {#if !loading && leadJob}
      <section class="grid gap-2" aria-label={i18n.m.queue.working_now}>
        <div class="flex items-baseline gap-2 text-caption"><h2 class="font-semibold text-ink-2">{i18n.m.queue.working_now}</h2><span class="text-ink-3">{processingJobs.length}</span></div>
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
      {@const hasActions = isActive(selectedJob.status) || selectedJob.status === 'Failed' || selectedJob.status === 'Cancelled'}
      {#snippet jobActions()}
        {#if selectedJob.status === 'AwaitingSizeReview'}
          <button class="btn btn-primary" onclick={() => selectedJob && approveSizePreflight(selectedJob)} disabled={approvingSizeId === selectedJob.id}>
            {approvingSizeId === selectedJob.id ? i18n.m.common.loading_short : i18n.m.queue.action_encode_anyway}
          </button>
        {/if}
        {#if selectedJob.status === 'Failed' || selectedJob.status === 'Cancelled'}
          <button class="btn btn-ghost" onclick={() => selectedJob && exclude(selectedJob)} disabled={excludingId === selectedJob.id} title={i18n.m.queue.exclude_title}>{excludingId === selectedJob.id ? i18n.m.queue.action_excluding : i18n.m.queue.action_exclude}</button>
          {#if canRetryHigher(selectedJob)}
            <button class="btn btn-primary" onclick={() => selectedJob && retry(selectedJob, true)} disabled={retryingId === selectedJob.id}>
              {retryingId === selectedJob.id ? i18n.m.queue.action_retrying_ellipsis : i18n.m.queue.action_retry_higher_quality}
            </button>
            <button class="btn" onclick={() => selectedJob && retry(selectedJob)} disabled={retryingId === selectedJob.id}>
              {i18n.m.queue.action_retry_same}
            </button>
          {:else}
            <button class="btn" onclick={() => selectedJob && retry(selectedJob)} disabled={retryingId === selectedJob.id}>
              {retryingId === selectedJob.id ? i18n.m.queue.action_retrying_ellipsis : i18n.m.queue.action_retry}
            </button>
          {/if}
          {#if selectedJob.belowTarget?.eligible}
            <button class="btn" onclick={() => selectedJob && acceptBelowTarget(selectedJob)} disabled={dryRun || replacingId !== null} title={dryRun ? undefined : i18n.m.queue.accept_anyway_title} aria-describedby={dryRun ? 'accept-below-target-locked' : undefined} data-accept-below-target>
              {i18n.m.queue.action_accept_anyway}
            </button>
          {/if}
          <button class="btn btn-danger" onclick={() => selectedJob && stopAndRemove(selectedJob)} disabled={removingId === selectedJob.id}>
            {removingId === selectedJob.id ? i18n.m.queue.action_removing_ellipsis : i18n.m.queue.action_remove_from_queue}
          </button>
        {/if}
        {#if isActive(selectedJob.status)}
          <button class="btn btn-danger" onclick={() => selectedJob && stopAndRemove(selectedJob)} disabled={removingId === selectedJob.id}>
            {removingId === selectedJob.id ? i18n.m.queue.action_stopping : i18n.m.queue.action_stop_remove}
          </button>
        {/if}
      {/snippet}
      {#snippet jobActionNotes()}
        {#if selectedJob.status === 'Failed' && selectedJob.belowTarget}
          {@const refusal = selectedJob.belowTarget.eligible ? null : belowTargetRefusal(selectedJob.belowTarget.refusal)}
          {#if selectedJob.belowTarget.eligible && dryRun}
            <p id="accept-below-target-locked" class="mt-2 text-xs text-ink-3">{i18n.m.jobResult.locked_reason}</p>
          {:else if refusal}
            <p class="mt-2 text-xs text-ink-3" data-accept-below-target-refused>{belowTargetHeading(selectedJob)}. {refusal}</p>
          {/if}
        {/if}
      {/snippet}
      <DetailDialog
        id="queue-job-dialog"
        size="wide"
        title={heroTitle(selectedJob.relativePath) ?? jobName(selectedJob)}
        titleId="queue-detail-title"
        focusableTitle
        eyebrow={i18n.m.queue.job_details}
        eyebrowId="queue-detail-label"
        closeLabel={i18n.m.queue.close_details}
        onclose={closeDetails}
        actions={hasActions ? jobActions : undefined}
        footer={hasActions ? jobActionNotes : undefined}
      >
        {#snippet backdrop()}<PosterGlow mediaFileId={selectedJob.mediaFileId} />{/snippet}
        {#snippet leading()}<Thumbnail mediaFileId={selectedJob.mediaFileId} size="poster" />{/snippet}
        {#snippet meta()}
          <span class="badge {suspended ? 'tone-warn' : badgeClass(selectedJob.status)}">{suspended ? i18n.m.queue.now_paused : selectedJob.finalizing ? i18n.m.queue.phase_finalizing : statusLabel(selectedJob.status)}</span>
          {#if selectedJob.executionAttempt && selectedJob.executionAttempt > 0}<span>{t(i18n.m.queue.attempt_number, { number: selectedJob.executionAttempt })}</span>{/if}
          {#if isWorkingJob(selectedJob)}<span>{detailLocation(selectedJob)}</span>{/if}
          {#if selectedJob.sourceSizeBytes != null && selectedJob.outputSizeBytes != null && selectedJob.outputSizeBytes > 0 && !isWorkingJob(selectedJob)}
            {@const change = sizeChangePercent(selectedJob.sourceSizeBytes, selectedJob.outputSizeBytes)}
            <p class="queue-detail-sizes basis-full text-body tabular-nums">{formatSize(selectedJob.sourceSizeBytes)} → {formatSize(selectedJob.outputSizeBytes)}{' '}<strong class="font-semibold"><SizeChange sourceSizeBytes={selectedJob.sourceSizeBytes} outputSizeBytes={selectedJob.outputSizeBytes} label={sizeChangeSentence(change)} /></strong></p>
          {/if}
        {/snippet}
        {#if selectedJob.status === 'Queued' && selectedJob.waitingForResources}
          <div class="callout tone-warn mb-4 p-4" role="status">
            <p class="font-semibold">{i18n.m.queue.waiting_resources_title}</p>
            <p class="mt-1 text-xs leading-relaxed">{i18n.m.queue.waiting_resources_detail}</p>
          </div>
        {/if}
        {#if error}<Banner kind="error" class="mb-4">{error}</Banner>{/if}
        {#if selectedJob.retryReason === 'SoftwareDecode' && lastAttempt && ['Queued', 'Probing', 'Transcoding', 'Leased', 'AwaitingVerification', 'Verifying'].includes(selectedJob.status)}
          <div class="callout tone-warn mb-4 p-4" role="status">
            <p class="font-semibold">{i18n.m.queue.retry_software_title}</p>
            <p class="mt-1 text-xs leading-relaxed">{t(i18n.m.queue.retry_software_detail, { worker: lastAttempt.workerName ?? i18n.m.dashboard.this_server, encoder: lastAttempt.videoEncoder ?? '—' })}</p>
          </div>
        {/if}
        {#if selectedJob.retryReason === 'AvoidIntelQsv' && lastAttempt && ['Queued', 'Probing', 'Transcoding', 'Leased', 'AwaitingVerification', 'Verifying'].includes(selectedJob.status)}
          <div class="callout tone-warn mb-4 p-4" role="status">
            <p class="font-semibold">{i18n.m.queue.retry_qsv_title}</p>
            <p class="mt-1 text-xs leading-relaxed">{t(i18n.m.queue.retry_qsv_detail, { worker: lastAttempt.workerName ?? i18n.m.dashboard.this_server })}</p>
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
          <JobStages job={selectedJob} queue={queueStatus} />
          {#if detailPhase(selectedJob)}<p class="queue-phase-note">{detailPhase(selectedJob)}</p>{/if}
        {:else if selectedJob.status === 'ReadyToReplace'}
          <JobStages job={selectedJob} queue={queueStatus} />
        {/if}
        {#if selectedJob.status === 'Cancelled' && selectedJob.errorMessage}
          <p class="callout mt-4 p-4 text-sm" role="status" data-testid="job-cancelled-reason">{selectedJob.errorMessage}</p>
        {/if}
        {#if selectedJob.status === 'Failed'}
          {@const story = jobFailureStory(selectedJob.failureCategory, selectedJob.errorMessage, fullReport(selectedJob)?.checks, i18n.m)}
          <div class="callout tone-bad mt-4 p-4" role="status">
            <p class="font-semibold">{story.headline}</p>
            <!-- "Retry at higher quality" is wrong advice once a search has already tried its highest setting. -->
            {#if searchReport(selectedJob)?.result?.decision?.fellBack === true}<p class="mt-1 text-xs leading-relaxed">{i18n.m.quality_first.hint_none_met}</p>
            {:else if story.hint}<p class="mt-1 text-xs leading-relaxed">{story.hint}</p>{/if}
            {#if story.failedChecks.length > 1}<p class="mt-2 text-xs">{t(i18n.m.queue.cause_also, { checks: story.failedChecks.map((name) => plainCheckName(name, i18n.m.checkNames)).join(' · ') })}</p>{/if}
          </div>
          {#if selectedJob.errorMessage}<details open={selectedJob.errorMessage.startsWith('Size saving prediction:')} class="mt-3 text-xs text-ink-3"><summary>{i18n.m.queue.technical_error}</summary><p class="mt-2 whitespace-pre-wrap break-words font-mono">{selectedJob.errorMessage}</p></details>{/if}
        {/if}
        <JobResult job={selectedJob} checks={parseReport(selectedJob)} {dryRun} {retentionDays}
          canRetryHigher={canRetryHigher(selectedJob)}
          replacing={replacingId === selectedJob.id} replaceBlocked={replacingAll || replacingId !== null}
          onreplace={() => selectedJob && replace(selectedJob)} onunlocked={() => (dryRun = false)} />
        {#if selectedJob.status === 'AwaitingSizeReview'}
          <div class="callout tone-warn mb-4 p-4" role="status">
            <p class="font-semibold">{i18n.m.queue.size_review_title}</p>
            <p class="mt-1 text-xs leading-relaxed">{selectedJob.errorMessage ?? i18n.m.queue.size_review_detail}</p>
          </div>
        {/if}
        <div class="queue-detail-specs">
    <!-- Details -->
    <dl class="grid gap-x-8 gap-y-3 text-sm sm:grid-cols-2 ">
      {#if selectedJob.superResolutionRun}
      <div class="flex justify-between gap-4" data-testid="job-super-resolution-run">
        <dt class="text-ink-3">{i18n.m.queue.detail_super_resolution}</dt>
        <dd class="text-right">{selectedJob.superResolutionRun}</dd>
      </div>
      {/if}
      {#if selectedJob.videoEncoder != null || selectedJob.videoQualityMode != null}
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
              <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.queue.detail_quality })} text={i18n.m.queue.quality_effective_tip} />
            {/if}
          {:else}—{/if}
        </dd>
      </div>
      {/if}
      <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_output_size}</dt><dd>{selectedJob.outputSizeBytes != null && selectedJob.outputSizeBytes > 0 ? formatSize(selectedJob.outputSizeBytes) : '—'}</dd></div>
      <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_priority}</dt><dd>{selectedJob.priority}</dd></div>
      <div class="flex justify-between gap-4"><dt class="text-ink-3">{i18n.m.queue.detail_verified}</dt><dd class="text-right">{selectedJob.verifiedAt ? new Date(selectedJob.verifiedAt).toLocaleString() : '—'}</dd></div>
    </dl>

    {#if report?.context?.vmafSampling}
      <p class="mt-3 text-xs text-ink-3">
        <span class="font-medium">{i18n.m.queue.detail_vmaf_sampling}:</span>
        {vmafSamplingLabel(report.context.vmafSampling)}
      </p>
    {/if}

    <!-- The exact ffmpeg command for this job — the "under the hood" view that complements the
         hero's live status. Useful while encoding and for diagnosing a failed job. -->
    {#if selectedJob.ffmpegArguments}
      <details class="mt-4 text-xs text-ink-3">
        <summary class="cursor-pointer">{i18n.m.queue.ffmpeg_command}</summary>
        <pre class="max-h-44 overflow-auto whitespace-pre-wrap break-all rounded-md bg-sunken p-3 font-mono text-caption leading-relaxed text-ink-2">ffmpeg {selectedJob.ffmpegArguments}</pre>
      </details>
    {/if}

    <!-- The audio filter this job's command actually applied, so the operator can see what was
         done to the audio without reading the whole command. -->
    {#if selectedJob.appliedAudioFilter}
      <p class="mt-3 text-xs text-ink-3">
        <span class="font-medium">{i18n.m.queue.applied_audio_filter}:</span>
        <span class="font-mono">{selectedJob.appliedAudioFilter}</span>
      </p>
    {/if}

    <!-- Verification report, when one exists -->
    {#if parseReport(selectedJob)}
      {@const checks = parseReport(selectedJob)}
      <div class="mt-4 border-t border-line-soft pt-4">
        {#if checks}<VerificationChecks {checks} />{/if}
        <AudioQualityReport report={fullReport(selectedJob)?.audioQuality ?? null} />
        <SoundtrackQualityReport report={fullReport(selectedJob)?.soundtrackQuality ?? null} />
        <ImagePerceptualReport report={fullReport(selectedJob)?.imagePerceptualQuality ?? null} />
      </div>
    {/if}
    <PerceptualSearchReport report={searchReport(selectedJob)} active={selectedJob.status === 'Probing' || selectedJob.status === 'Leased'} />
    <SoundtrackSearchReport report={soundtrackSearch(selectedJob)} active={selectedJob.status === 'Probing'} />
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
              {#if selectedJob.retryReason === 'AvoidIntelQsv'}<p class="attempt-reason">{i18n.m.queue.retry_qsv_title}</p>{/if}
            </div>
          </li>
          {#each [...earlierAttempts].reverse() as attempt (attempt.number)}
            <li class="attempt-item">
              <div class="attempt-card">
                <div class="attempt-card-top">
                  <strong>{t(i18n.m.queue.attempt_number, { number: attempt.number })} · {attempt.outcome === 'Failed' ? i18n.m.queue.attempt_failed : i18n.m.queue.attempt_rejected}</strong>
                  <time datetime={attempt.endedAt}>{new Date(attempt.endedAt).toLocaleString()}</time>
                </div>
                <p>{attempt.workerName ?? i18n.m.dashboard.this_server} · {attempt.videoEncoder ?? '—'}{#if attempt.hardwareDecoder}{` · ${attempt.hardwareDecoder}`}{/if}</p>
                {#if attempt.reason}<p class="attempt-reason">{attempt.reason === 'HardwareDecodeCorruption' ? i18n.m.queue.attempt_decode_corruption : attempt.reason === 'IntelQsvTimestamps' ? i18n.m.queue.attempt_qsv_timestamps : attempt.reason === 'WorkerHandback' ? i18n.m.queue.attempt_worker_handback : attempt.reason}</p>{/if}
                {#if (attempt.reason === 'WorkerHandback' || attempt.reason === 'IntelQsvTimestamps') && attempt.processLog}
                  <details class="attempt-checks">
                    <summary>{i18n.m.queue.attempt_ffmpeg_said}</summary>
                    <pre class="attempt-log">{attempt.processLog}</pre>
                  </details>
                {/if}
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

    {#if diagnosticCapture && selectedJob}<CapturedDiagnosticTimeline captureId={diagnosticCapture.id} jobId={selectedJob.id} recording={diagnosticCapture.status === 'Recording'} />{/if}
    <section class="queue-diagnostic-action" aria-label={i18n.m.settings.diagnostics_title}>
      <div>
        <h3>{i18n.m.settings.diagnostics_title}</h3>
        {#if !diagnosticLookupPending}<p>{diagnosticCapture ? (diagnosticCapture.status === 'Recording' ? i18n.m.settings.diagnostics_recording : i18n.m.settings.diagnostics_off) : i18n.m.settings.diagnostics_desc}</p>{/if}
      </div>
      {#if diagnosticLookupPending}
        <span class="queue-diagnostic-pending" role="status">{i18n.m.common.loading_short}</span>
      {:else if diagnosticCapture}
        <button class="btn" disabled={downloadingDiagnostics} onclick={downloadJobDiagnostics}>{i18n.m.settings.diagnostics_download}</button>
        <button class="btn" disabled={downloadingDiagnostics} onclick={copyDiagnosticSummary}>{diagnosticSummaryCopied ? i18n.m.settings.diagnostics_copied : i18n.m.settings.diagnostics_copy_summary}</button>
        {#if diagnosticCapture.retainUntil}<span class="text-xs text-ink-3">{diagnosticCapture.status === 'Recording' ? i18n.m.settings.diagnostics_retention_estimate : i18n.m.settings.diagnostics_retain_until}: {new Date(diagnosticCapture.retainUntil).toLocaleString()}</span>{/if}
      {:else}
        <button class="btn" onclick={openDiagnosticSettings}>{i18n.m.queue.attempt_open_diagnostics}</button>
      {/if}
      {#if diagnosticSummaryError}<p class="basis-full text-sm text-bad" role="alert">{diagnosticSummaryError}</p>{/if}
      {#if diagnosticSummaryText}
        <div class="basis-full min-w-0">
          <p id="diagnostic-copy-help" class="mb-2 text-sm text-ink-2" role="status">{i18n.m.settings.diagnostics_copy_manual}</p>
          <textarea class="input w-full min-w-0 font-mono text-xs" rows="8" readonly value={diagnosticSummaryText}
            aria-label={i18n.m.settings.diagnostics_copy_summary} aria-describedby="diagnostic-copy-help"
            onfocus={event => event.currentTarget.select()} onclick={event => event.currentTarget.select()}></textarea>
        </div>
      {/if}
    </section>

        </div>
        {#if selectedJob.status === 'Transcoding' || selectedJob.status === 'Verifying'}
          <div class="queue-usage-heading">{i18n.m.dashboard.this_server} · {i18n.m.queue.host_usage}</div>
          <div class="queue-usage">
            <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="var(--accent)" />
            {#if selectedJob.status === 'Transcoding'}<UsageGraph label="GPU" data={activity.gpuHistory} current={activity.metrics?.gpuPercent ?? null} color="var(--ink-3)" unavailable={gpuUnavailable} detail={activity.metrics?.gpuEngine} />{/if}
          </div>
        {/if}
        <div class="queue-path"><span>{i18n.m.queue.col_file}</span><p>{selectedJob.relativePath ?? '—'}</p></div>
        {#if selectedJob.acceptedBelowTargetAt}<p class="text-xs text-ink-2" data-accepted-below-target>{i18n.m.queue.accepted_below_target}</p>{/if}
      </DetailDialog>
    {/if}

    <section id="queue-list" aria-labelledby="queue-list-title" style:--queue-toolbar-height={`${toolbarHeight}px`}>
      <div class="queue-section-heading">
        <h2 id="queue-list-title" class="focus-ring" tabindex="-1">{filter === 'review' ? i18n.m.queue.filter_review : i18n.m.queue.next_recent}</h2>
        <!-- The housekeeping the queue used to hide behind a toggle: it is always on show, and it
             only appears when it would do something. -->
        <div class="queue-heading-actions">
          {#if pendingCount > 0}<button class="btn btn-sm btn-ghost" onclick={clearPending} disabled={clearingPending} title={i18n.m.queue.clear_queue_title + (counts.review > 0 ? ` ${t(i18n.m.queue.clear_review_included, { count: counts.review })}` : '')}>{clearingPending ? i18n.m.queue.clearing : t(i18n.m.queue.clear_queue, { count: pendingCount.toLocaleString() })}</button>{/if}
          {#if counts.completed > 0}<button class="btn btn-sm btn-ghost" onclick={() => clear('finished')} disabled={clearingScope !== null || finishedClearable === 0} title={finishedClearable > 0 ? i18n.m.queue.clear_completed_title_available : i18n.m.queue.clear_completed_title_protected}>{clearingScope === 'finished' ? i18n.m.queue.clearing : finishedClearable > 0 ? t(i18n.m.queue.clear_completed, { count: finishedClearable }) : t(i18n.m.queue.completed_protected, { count: finishedProtected })}</button>{/if}
        </div>
      </div>
      {#if jobs.length > 0}
        <!-- One toolbar, two states. The filters and the batch bar share a single grid cell, so
             picking a row replaces the filters where they stood: the table below never moves.
             It stays in view over a long list, with the table's header held just beneath it. -->
        <div class="queue-toolbar" data-queue-toolbar bind:offsetHeight={toolbarHeight}>
          <div class="queue-toolbar-layer" class:queue-toolbar-hidden={selection.count > 0} inert={selection.count > 0} data-queue-filters>
            <FilterBar label={i18n.m.queue.filter_label} value={filter} onselect={selectFilter} items={([
              ['all', i18n.m.queue.filter_all], ['active', i18n.m.queue.filter_active], ['review', i18n.m.queue.filter_review],
              ['ready', i18n.m.queue.filter_ready], ['completed', i18n.m.queue.filter_completed], ['failed', i18n.m.queue.filter_failed],
            ] as const).filter(([key]) => key !== 'review' || counts.review > 0 || filter === 'review').map(([value, label]) => ({ value, label, count: counts[value] }))} />
            <div class="queue-toolbar-actions">
              {#if readyToReplaceCount > 0}{#if dryRun}<span id="replace-all-locked" class="text-xs text-ink-3" data-replace-all-locked>{i18n.m.jobResult.locked_reason}</span>{/if}<button class="btn btn-primary" onclick={replaceAll} disabled={dryRun || replacingAll || replacingId !== null} title={dryRun ? undefined : i18n.m.queue.replace_all_title} aria-describedby={dryRun ? 'replace-all-locked' : undefined}><Icon name="replace" />{replacingAll ? i18n.m.queue.action_replacing : t(i18n.m.queue.replace_all, { count: readyToReplaceCount.toLocaleString() })}</button>{/if}
              {#if erroredClearable > 0}<button class="btn btn-ghost queue-clear-errors" onclick={() => clear('errored')} disabled={clearingScope !== null} title={i18n.m.queue.clear_errored_title}>{clearingScope === 'errored' ? i18n.m.queue.clearing : t(i18n.m.queue.clear_errored, { count: erroredClearable })}</button>{/if}
            </div>
          </div>
          <div class="queue-toolbar-layer" class:queue-toolbar-hidden={selection.count === 0} inert={selection.count === 0} data-queue-batch>
            <SelectionToolbar
              variant="inline"
              count={selection.count}
              total={jobs.length}
              limited={selection.isFull}
              onclear={() => selection.clear()}
            >
              {#snippet actions()}
                <button
                  class="btn btn-ghost"
                  onclick={() => askBatch('retry')}
                  disabled={batchBusy || retryableJobIds.length === 0}
                  title={retryableJobIds.length === 0 ? i18n.m.queue.batch_retry_unavailable : i18n.m.queue.batch_retry_title}
                >
                  <Icon name="retry" class="h-4 w-4" />
                  {i18n.m.queue.batch_retry}
                </button>
                <button
                  class="btn btn-danger"
                  onclick={() => askBatch('remove')}
                  disabled={batchBusy}
                  title={i18n.m.queue.batch_remove_title}
                >
                  <Icon name="trash" class="h-4 w-4" />
                  {i18n.m.queue.batch_remove}
                </button>
              {/snippet}
            </SelectionToolbar>
          </div>
        </div>
      {/if}
      {#if batchOutcome && visibleJobs.length > 0}
        <BatchResult
          title={batchOutcome.title}
          succeeded={batchOutcome.succeeded}
          failures={batchOutcome.failures}
          ondismiss={() => (batchOutcome = null)}
        />
      {/if}
      <DataTable density="compact" rowCount={pagedJobs.length} {loading} error={loadError} labelledby="queue-list-title" footer={visibleJobs.length > 0 ? queuePagination : undefined}>
        {#snippet head()}
          <tr><th scope="col" class="selection-column"><SelectionCheckbox checked={selection.isEverySelected(filteredJobIds)} indeterminate={selection.isSomeSelected(filteredJobIds) && !selection.isEverySelected(filteredJobIds)} label={i18n.m.queue.batch_select_all} onchange={toggleSelectAllJobs} /></th><th scope="col">{i18n.m.queue.col_file}</th><th scope="col" class="w-[37%] sm:w-1/4">{i18n.m.queue.col_status}</th><th scope="col" class="verification-column hidden w-[17%] sm:table-cell">{i18n.m.queue.col_verification}</th><th scope="col" class="action-column hidden w-[15%] sm:table-cell"><span class="sr-only">{i18n.m.queue.view_job}</span></th></tr>
        {/snippet}
        {#each pagedJobs as job (job.id)}
          <tr class:selected-row={selectedJobId === job.id}>
            <td class="selection-column"><SelectionCheckbox checked={selection.isSelected(job.id)} label={t(i18n.m.selection.select_row, { name: jobName(job) })} onchange={(extend) => toggleJobSelection(job.id, extend)} /></td>
            <td><button id={`queue-job-${job.id}`} class="group flex w-full min-w-0 items-center gap-2.5 rounded-md text-left focus-ring max-sm:min-h-(--control-lg) sm:gap-3.5" onclick={(event) => selectRow(job.id, event)} aria-haspopup="dialog" aria-controls={selectedJobId === job.id ? 'queue-job-dialog' : undefined}><Thumbnail mediaFileId={job.mediaFileId} size="xs" /><span class="min-w-0"><strong class="block text-caption font-medium text-ink group-hover:text-accent sm:text-label">{job.relativePath?.split(/[\\/]/).pop() ?? jobName(job)}</strong><small class="mt-1.5 block text-micro text-ink-3">{job.videoEncoder ?? job.enqueueReason ?? '—'}</small></span></button></td>
            <td><span class="badge whitespace-normal {badgeClass(job.status)}">{statusLabel(job.status)}</span>{#if job.status === 'Queued' && job.waitingForResources}<small class="mt-1.5 block text-micro text-warn">{i18n.m.queue.waiting_resources_row}</small>{:else if job.status === 'Queued' && job.waitingForWorker}<small class="mt-1.5 block text-micro text-warn">{i18n.m.queue.waiting_for_worker}</small>{:else if job.status === 'Failed'}<small class="mt-1.5 block text-micro text-bad">{jobFailureStory(job.failureCategory, job.errorMessage, fullReport(job)?.checks, i18n.m).headline}</small>{:else if job.status === 'AwaitingSizeReview'}<small class="mt-1.5 block text-micro text-warn">{i18n.m.queue.size_review_title}</small>{/if}</td>
            <td class="verification-column hidden sm:table-cell">{#if job.verificationPassed !== null}<button class="group inline-flex rounded text-left cursor-pointer items-center gap-1 focus-ring" class:text-ink-2={job.verificationPassed} class:text-bad={!job.verificationPassed && !job.acceptedBelowTargetAt} class:text-warn={!!job.acceptedBelowTargetAt} onclick={(event) => selectRow(job.id, event)}>{#if job.verificationPassed}<OkMark class="h-3 w-3" />{/if}<span class="underline decoration-ink-4/75 underline-offset-[3px] group-hover:decoration-current">{job.verificationPassed ? i18n.m.queue.verify_passed : job.acceptedBelowTargetAt ? i18n.m.quarantine.accepted_below_target : i18n.m.queue.verify_failed}</span></button>{#if job.outputSizeBytes != null && job.outputSizeBytes > 0}<small class="mt-1.5 block text-micro text-ink-3">{formatSize(job.outputSizeBytes)}</small>{/if}{:else}<span class="text-ink-4">—</span>{/if}</td>
            <td class="action-column hidden sm:table-cell">{#if job.status === 'ReadyToReplace' && job.verificationPassed && !job.finalizing && dryRun}<button class="btn btn-sm btn-ghost max-w-full whitespace-normal" onclick={(event) => selectRow(job.id, event)}>{i18n.m.queue.view_job}</button>{:else if job.status === 'ReadyToReplace' && job.verificationPassed && !job.finalizing}<button class="btn btn-sm btn-primary max-w-full whitespace-normal" onclick={() => replace(job)} disabled={replacingAll || replacingId !== null}>{replacingId === job.id ? i18n.m.queue.action_replacing : i18n.m.queue.action_replace}</button>{:else if job.status === 'Failed' || job.status === 'Cancelled'}<button class="btn btn-sm btn-ghost max-w-full whitespace-normal" onclick={(event) => selectRow(job.id, event)}>{i18n.m.queue.view_job}</button>{/if}</td>
          </tr>
        {/each}
        {#snippet empty()}{jobs.length > 0 ? i18n.m.queue.no_matches : i18n.m.queue.empty}{/snippet}
      </DataTable>
    </section>
  {/if}

  <ConfirmDialog
    bind:open={batchConfirmOpen}
    title={batchAction === 'remove'
      ? plural(selection.count, i18n.m.queue.batch_confirm_remove_title_one, i18n.m.queue.batch_confirm_remove_title_other, selection.count.toLocaleString())
      : plural(retryableJobIds.length, i18n.m.queue.batch_confirm_retry_title_one, i18n.m.queue.batch_confirm_retry_title_other, retryableJobIds.length.toLocaleString())}
    confirmLabel={batchAction === 'remove' ? i18n.m.queue.batch_remove : i18n.m.queue.batch_retry}
    danger={batchAction === 'remove'}
    busy={batchBusy}
    onconfirm={runBatch}
    oncancel={() => { batchConfirmOpen = false; batchAction = null }}
  >
    {#snippet body()}
      <p>{batchAction === 'remove' ? i18n.m.queue.batch_confirm_remove_body : i18n.m.queue.batch_confirm_retry_body}</p>
      {#if batchAction === 'remove' && selectedActive > 0}
        <p class="mt-3 rounded-lg border border-warn-line bg-warn-soft p-3 text-warn">
          {plural(
            selectedActive,
            i18n.m.queue.batch_confirm_remove_running_one,
            i18n.m.queue.batch_confirm_remove_running_other,
            selectedActive.toLocaleString(),
          )}
        </p>
      {/if}
      {#if batchAction === 'retry' && retryableJobIds.length < selection.count}
        <p class="mt-3 rounded-lg border border-warn-line bg-warn-soft p-3 text-warn">
          {t(i18n.m.queue.batch_confirm_retry_partial, {
            count: retryableJobIds.length.toLocaleString(),
            total: selection.count.toLocaleString(),
          })}
        </p>
      {/if}
    {/snippet}
  </ConfirmDialog>
</div>

{#snippet queuePagination()}
  <span>{t(i18n.m.queue.range, { start: (queuePageStart + 1).toLocaleString(), end: Math.min(queuePageStart + QUEUE_PAGE_SIZE, visibleJobs.length).toLocaleString(), total: visibleJobs.length.toLocaleString() })}</span>
  {#if queuePageCount > 1}<div class="data-table-pages"><button class="btn btn-ghost btn-icon" onclick={() => goToQueuePage(queuePage - 1)} disabled={queuePage <= 1} aria-label={i18n.m.queue.prev_page}><Icon name="arrow-left" /></button><span>{t(i18n.m.queue.page_of, { page: Math.min(queuePage, queuePageCount), count: queuePageCount })}</span><button class="btn btn-ghost btn-icon" onclick={() => goToQueuePage(queuePage + 1)} disabled={queuePage >= queuePageCount} aria-label={i18n.m.queue.next_page}><Icon name="arrow-right" /></button></div>{/if}
{/snippet}

{#if replacementTarget}
  <dialog id="queue-replacement-dialog" class="app-modal replacement-confirmation" use:modal={closeReplacement} aria-labelledby="replacement-title" aria-busy={replacementPending}>
    <h2 id="replacement-title" class="mb-4 text-lg font-semibold">{replacementTarget !== 'all' && acceptingBelowTarget ? belowTargetHeading(replacementTarget) : i18n.m.queue.action_replace_original}</h2>
    <p class="mb-4 whitespace-pre-line">{replacementTarget === 'all'
      ? t(i18n.m.queue.confirm_replace_all, { count: readyToReplaceCount.toLocaleString() })
      : acceptingBelowTarget ? i18n.m.queue.accept_body : i18n.m.queue.confirm_replace}</p>
    {#if replacementTarget !== 'all' && acceptingBelowTarget}<p class="mb-4" data-accept-undo>{acceptUndo}</p>{/if}
    {#if replacementTarget !== 'all'}
      {@const target = replacementTarget}
      <p class="mb-4 break-words">{jobName(target)}</p>
      <!-- What Replace would put in place, next to what it replaces, before anything moves. -->
      <!-- Unmounted while replacing, so neither file is held open while it moves. -->
      {#if target.mediaKind && target.mediaKind !== 'Unknown' && !replacementPending}
        <div class="mb-5" data-replacement-compare>
          <MediaCompare mediaKind={target.mediaKind} poster={api.jobArtworkUrl(target.id)} playbackNote={i18n.m.shared.playback_note_short}
            left={{ label: i18n.m.shared.original, url: api.mediaContentUrl(target.mediaFileId), sizeBytes: target.sourceSizeBytes }}
            right={{ label: i18n.m.shared.encoded, url: api.jobOutputContentUrl(target.id), sizeBytes: target.outputSizeBytes }} />
        </div>
      {/if}
    {/if}
    {#if replacementError}
      <div role="alert" class="mb-4">
        <Banner kind="error">{replacementError}</Banner>
        {#if replacementFailures.length > 0}
          <ul class="mt-3 space-y-3 text-sm">
            {#each replacementFailures as failure (failure.jobId)}
              <li><strong>{jobs.find(job => job.id === failure.jobId)?.relativePath ?? t(i18n.m.queue.job_fallback, { id: failure.jobId })}</strong><span class="block text-ink-2">{failure.message}</span></li>
            {/each}
          </ul>
        {/if}
      </div>
    {/if}
    <div class="flex justify-end gap-3">
      <button class="btn btn-ghost" onclick={closeReplacement} disabled={replacementPending}>{i18n.m.common.cancel}</button>
      <button class="btn btn-primary" onclick={confirmReplacement} disabled={replacementPending}>{acceptingBelowTarget && replacementTarget !== 'all'
        ? replacementPending ? i18n.m.queue.action_accepting : i18n.m.queue.action_accept_confirm
        : replacementPending ? i18n.m.queue.action_replacing_ellipsis : i18n.m.queue.action_replace}</button>
    </div>
  </dialog>
{/if}

<style>
  .replacement-confirmation { width: min(42rem, calc(100vw - 2rem)); padding: 1.5rem; overflow-y: auto; overflow-wrap: anywhere; }
  .replacement-confirmation:has([data-replacement-compare]) { width: min(64rem, calc(100vw - 2rem)); }
  #queue-list-title { scroll-margin-block-start: .75rem; }
  .queue-review-alert { width: 100%; display: flex; align-items: center; gap: .875rem; margin-bottom: 1.25rem; padding: 1rem 1.125rem; text-align: left; box-shadow: var(--lift-1); transition: transform 180ms ease, box-shadow 180ms ease; }
  .queue-review-alert:hover, .queue-review-alert:focus-visible { transform: translateY(-2px); box-shadow: var(--lift-3); }
  .queue-review-alert > :global(svg) { flex: none; }
  .queue-review-alert span { min-width: 0; flex: 1; display: grid; gap: .25rem; }
  .queue-review-alert strong { color: var(--ink); font-size: .8125rem; }
  .queue-review-alert small { color: var(--ink-3); font-size: .75rem; line-height: 1.45; }
  .queue-section-heading { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: .5rem 1rem; margin: 1.25rem 0 .5rem; }.queue-section-heading h2 { font-size: .875rem; color: var(--ink-2); font-weight: 600; }.queue-heading-actions { display: flex; align-items: center; justify-content: flex-end; gap: .5rem; flex-wrap: wrap; }.queue-idle { display: flex; align-items: center; gap: .75rem; font-size: .8125rem; color: var(--ink-3); padding: 1.25rem; border-radius: .875rem; background: var(--panel); }
  .queue-phase-note { margin-top: .75rem; font-size: .75rem; color: var(--ink-3); }
  .queue-execution-path { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: .75rem; margin-bottom: 1.25rem; padding: 1rem; border: 1px solid var(--divide-soft); border-radius: .75rem; background: var(--sunken); }.queue-execution-path h3 { grid-column: 1/-1; margin: 0; font-size: .75rem; color: var(--ink-3); font-weight: 600; }.queue-execution-path div { min-width: 0; display: grid; gap: .25rem; font-size: .6875rem; color: var(--ink-3); }.queue-execution-path strong { color: var(--ink); font-size: .75rem; font-weight: 600; overflow-wrap: anywhere; }
  @media(max-width: 420px) { .queue-execution-path { grid-template-columns: 1fr; } }
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
  .attempt-card-top time { font-size: .75rem; line-height: 1.4; font-variant-numeric: tabular-nums; color: var(--ink-3); }
  .attempt-card > p { margin-top: .5rem; overflow-wrap: anywhere; }
  .attempt-card .attempt-reason { color: var(--ink-2); }
  .attempt-checks { margin-top: .875rem; border-top: 1px solid var(--divide-soft); padding-top: .75rem; }
  .attempt-checks summary { cursor: pointer; color: var(--accent); font-weight: 600; padding: .25rem 0; }
  .attempt-log { margin-top: .5rem; max-height: 16rem; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font-size: .6875rem; line-height: 1.5; color: var(--ink-2); }
  .queue-diagnostic-action { display: flex; align-items: center; justify-content: space-between; gap: 1rem; margin-top: 1.5rem; border: 1px solid var(--edge); border-radius: .75rem; padding: 1rem; background: var(--raised); box-shadow: var(--lift-1); transition: box-shadow 180ms ease; }
  .queue-diagnostic-action:hover, .queue-diagnostic-action:focus-within { box-shadow: var(--lift-3); }
  .queue-diagnostic-action > div { min-width: 0; }
  .queue-diagnostic-action p { max-width: 44ch; margin-top: .375rem; color: var(--ink-3); font-size: .75rem; line-height: 1.5; }
  .queue-diagnostic-action .btn { flex: none; }
  .queue-diagnostic-pending { flex: none; color: var(--ink-3); font-size: .75rem; }
  .queue-detail-specs:first-child { margin-top: 0; }
  .queue-detail-specs { margin-top: 1.5rem; }.queue-detail-specs > :global(dl) { grid-template-columns: repeat(2, minmax(0, 1fr)); }.queue-detail-specs > :global(dl > div) { min-width: 0; padding: .75rem 0; border-bottom: 1px solid var(--divide-soft); font-size: .8125rem; }.queue-detail-specs :global(dd) { min-width: 0; overflow-wrap: anywhere; }.queue-usage-heading { color: var(--ink-3); font-size: .75rem; margin: 1.5rem 0 .75rem; }.queue-usage { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .75rem; }.queue-path { margin-top: 1.5rem; font-size: .75rem; color: var(--ink-3); }.queue-path p { margin-top: .5rem; font: .6875rem/1.8 ui-monospace, monospace; overflow-wrap: anywhere; }
  /* The filters and the batch bar share one grid cell, sized to the taller of the two, so
     picking a row swaps one for the other and the table below never moves. */
  .queue-toolbar { display: grid; grid-template-columns: minmax(0, 1fr); margin-bottom: .75rem; }
  /* From tablet width up (with vertical room) it sticks at the top of the scroll container on the
     page's own fill, and the table header stacks directly beneath it. Vertical padding keeps focus
     rings clear of the scrollport edge and wrapped rows clear of the table header. */
  @media(min-width: 640px) and (min-height: 540px) { #queue-list { --data-table-sticky-top: var(--queue-toolbar-height); }.queue-toolbar { position: sticky; top: 0; z-index: 20; padding-block: .5rem; background: var(--ground); }#queue-list :global(tbody :is(tr, button, a, input)) { scroll-margin-block-start: calc(var(--queue-toolbar-height) + 2.75rem); } }
  .queue-toolbar-layer { grid-area: 1 / 1; display: flex; align-items: center; justify-content: space-between; gap: .5rem .75rem; flex-wrap: wrap; min-height: 3rem; transition: opacity 180ms ease, transform 180ms ease, visibility 0s linear 180ms; }
  .queue-toolbar-layer:not(.queue-toolbar-hidden) { transition: opacity 180ms ease, transform 180ms ease, visibility 0s; }
  .queue-toolbar-hidden { opacity: 0; visibility: hidden; pointer-events: none; transform: translateY(-.25rem); }
  .queue-toolbar-actions { display: flex; align-items: center; justify-content: flex-end; gap: .5rem; flex-wrap: wrap; }.queue-toolbar-actions .btn { font-size: .75rem; }
  /* Clearing failed and cancelled jobs is quiet until it is pointed at, then it says what it does. */
  .queue-clear-errors { color: var(--ink-3); }.queue-clear-errors:hover { color: var(--bad); background: var(--bad-soft); }
  @media(max-width: 639px) { .queue-detail-specs > :global(dl) { grid-template-columns: 1fr; }.queue-usage { grid-template-columns: 1fr; }.queue-toolbar-actions,.queue-heading-actions { justify-content: flex-start; } }
  @media(max-width: 639px) { .queue-diagnostic-action { align-items: stretch; flex-direction: column; }.queue-diagnostic-action .btn { width: 100%; }.attempt-card-top { align-items: flex-start; flex-direction: column; } }
  @media(prefers-reduced-motion: reduce) { .attempt-card, .queue-diagnostic-action { transition: none; }.queue-toolbar-layer, .queue-toolbar-layer:not(.queue-toolbar-hidden) { transition: none; } }
</style>
