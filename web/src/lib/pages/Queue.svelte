<script lang="ts">
  import { api, type Job, type QueueStatus, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize, formatDuration } from '../format'
  import { createJobsConnection, type JobProgress } from '../realtime'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { jobFailureDescription } from '../i18n/jobErrors'
  import { router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import Icon from '../components/Icon.svelte'
  import Banner from '../components/Banner.svelte'
  import BottomSheet from '../components/BottomSheet.svelte'
  import UsageGraph from '../components/UsageGraph.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import FailuresPanel from '../components/FailuresPanel.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'

  let jobs = $state<Job[]>([])
  let queueStatus = $state<QueueStatus | null>(null)
  // Live transcode telemetry keyed by job id, pushed over SignalR between reloads.
  let live = $state<Record<number, JobProgress>>({})
  let error = $state<string | null>(null)
  let loading = $state(true)
  let cancellingId = $state<number | null>(null)
  let removingId = $state<number | null>(null)
  let replacingId = $state<number | null>(null)
  let retryingId = $state<number | null>(null)
  let excludingId = $state<number | null>(null)
  let clearingScope = $state<'errored' | 'finished' | null>(null)
  let clearingPending = $state(false)
  let expandedId = $state<number | null>(null)
  let filter = $state<'all' | 'active' | 'completed' | 'failed' | 'verified' | 'verifyFailed'>('all')
  // Queue (live work) vs Failures (failed jobs grouped by reason, with the captured ffmpeg log).
  let activeTab = $state<'queue' | 'failures'>('queue')

  // The job open in the detail bottom sheet, and whether the sheet shows full content.
  let selectedJobId = $state<number | null>(null)
  let sheetExpanded = $state(true)
  let sheetHeight = $state(0)

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
      clearInterval(timer)
      void connection.stop()
    }
  })

  function applyProgress(progress: JobProgress) {
    live[progress.jobId] = progress
    const job = jobs.find((j) => j.id === progress.jobId)
    if (job) job.progress = progress.progress
  }

  async function load() {
    try {
      const [nextJobs, nextStatus] = await Promise.all([api.jobs(), api.queueStatus()])
      jobs = nextJobs
      queueStatus = nextStatus
      // Drop stale telemetry for jobs that are no longer transcoding.
      const transcoding = new Set(nextJobs.filter((j) => j.status === 'Transcoding').map((j) => j.id))
      for (const id of Object.keys(live)) {
        if (!transcoding.has(Number(id))) delete live[Number(id)]
      }
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_load
    } finally {
      loading = false
    }
  }

  function speedLabel(speed: number | null): string {
    return speed == null ? '' : `${speed.toFixed(speed < 10 ? 2 : 1)}×`
  }

  function etaLabel(seconds: number | null): string {
    if (seconds == null) return ''
    return seconds < 60
      ? t(i18n.m.queue.eta_seconds, { seconds: Math.round(seconds) })
      : t(i18n.m.queue.eta_duration, { duration: formatDuration(seconds) })
  }

  // The job's display name for confirms and the hero, falling back to a job id when the path is unknown.
  function jobName(job: Job): string {
    return job.relativePath ?? t(i18n.m.queue.job_fallback, { id: job.id })
  }

  function telemetryLabel(progress: JobProgress | undefined): string {
    if (!progress) return ''
    return [speedLabel(progress.speed), etaLabel(progress.etaSeconds)].filter(Boolean).join(' · ')
  }

  async function cancel(job: Job) {
    cancellingId = job.id
    try {
      await api.cancelJob(job.id)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.queue.error_cancel
    } finally {
      cancellingId = null
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

  const ACTIVE = ['Queued', 'Probing', 'Transcoding', 'Verifying', 'ReadyToReplace']
  function isActive(status: string) {
    return ACTIVE.includes(status)
  }

  async function retry(job: Job) {
    retryingId = job.id
    try {
      await api.retryJob(job.id)
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

  // Reset the pending queue (e.g. after a rules change): removes all queued and ready-to-replace
  // jobs and cancels anything in flight. No original is touched — ready-to-replace outputs are
  // verified-but-not-applied, so only recomputable work is discarded.
  async function clearPending() {
    if (!confirm(
      plural(
        pendingCount,
        i18n.m.queue.confirm_clear_pending_one,
        i18n.m.queue.confirm_clear_pending_other,
        pendingCount.toLocaleString(),
      ),
    )) return
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
      case 'completed': return job.status === 'Completed'
      case 'failed': return job.status === 'Failed'
      // Verification outcome cuts across status (a ready-to-replace job has passed; a job can fail
      // a gate without being status=Failed), so it filters on verificationPassed, not status.
      case 'verified': return job.verificationPassed === true
      case 'verifyFailed': return job.verificationPassed === false
      default: return true
    }
  }

  let counts = $derived({
    all: jobs.length,
    active: jobs.filter((j) => isActive(j.status)).length,
    completed: jobs.filter((j) => j.status === 'Completed').length,
    failed: jobs.filter((j) => j.status === 'Failed').length,
    verified: jobs.filter((j) => j.verificationPassed === true).length,
    verifyFailed: jobs.filter((j) => j.verificationPassed === false).length,
  })
  let visibleJobs = $derived(jobs.filter((j) => matchesFilter(j)))

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

  function heroFolder(path: string | null): string | null {
    if (!path) return null
    const parts = path.replace(/\\/g, '/').split('/')
    parts.pop()
    return parts.join('/') || null
  }

  // The job currently open in the detail sheet; resolved live so its progress/status stay
  // fresh, and the sheet auto-closes if the job is cleared from the list.
  let selectedJob = $derived(jobs.find((j) => j.id === selectedJobId) ?? null)

  function selectRow(id: number) {
    if (selectedJobId === id) {
      selectedJobId = null
    } else {
      selectedJobId = id
      sheetExpanded = true
    }
  }

  // GPU graph is shown only while the selected job is actually encoding; when the host can't
  // expose GPU stats without elevation the broadcaster reports it unsupported.
  let gpuUnavailable = $derived(
    activity.metrics && !activity.metrics.gpuSupported ? i18n.m.dashboard.gpu_unavailable : null,
  )

  // The "now processing" hero: jobs actively doing work right now (usually one, since the
  // default concurrency is 1). Queued count feeds the idle state.
  let processingJobs = $derived(
    jobs.filter((j) => j.status === 'Transcoding' || j.status === 'Probing' || j.status === 'Verifying'),
  )
  let queuedCount = $derived(jobs.filter((j) => j.status === 'Queued').length)
  // Whether the hero backdrop image resolved for a job id (from a connected media server). A 404
  // (no server / no match) leaves it false and the hero stays plain.
  let artworkLoaded = $state<Record<number, boolean>>({})

  // The table scrolls internally and fills the space below the page chrome; when the detail
  // sheet is open its measured height is subtracted so rows stay reachable above it (the same
  // behaviour as Inventory). The chrome above the table is variable (dispatch card, filters,
  // banner), so the container's top is measured rather than assumed.
  let tableScrollEl = $state<HTMLElement | null>(null)
  let tableMaxHeight = $state('65vh')

  $effect(() => {
    // Re-measure whenever the layout above the table, or the sheet height, can change.
    void sheetHeight
    void selectedJobId
    void filter
    void jobs.length
    void loading
    void queueStatus
    void error
    const el = tableScrollEl
    if (!el) return
    const measure = () => {
      const top = el.getBoundingClientRect().top
      const sheetSub = selectedJob ? sheetHeight : 0
      // Leave room for the bottom padding and the "N jobs" caption below the table.
      const available = window.innerHeight - top - 48 - sheetSub
      tableMaxHeight = `${Math.max(200, Math.round(available))}px`
    }
    const raf = requestAnimationFrame(measure)
    window.addEventListener('resize', measure)
    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('resize', measure)
    }
  })

  function badgeClass(status: string): string {
    switch (status) {
      case 'Transcoding':
      case 'Probing':
      case 'Verifying':
        return 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300'
      case 'ReadyToReplace':
      case 'Completed':
        return 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400'
      case 'Failed':
        return 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
      case 'Cancelled':
        return 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'
      default:
        return 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'
    }
  }

  // Human-readable, translated label for a job's status enum; falls back to the raw value for any
  // status not yet mapped.
  const STATUS_LABELS: Record<string, string> = $derived({
    Queued: i18n.m.queue.status_queued,
    Probing: i18n.m.queue.status_probing,
    Transcoding: i18n.m.queue.status_transcoding,
    Verifying: i18n.m.queue.status_verifying,
    ReadyToReplace: i18n.m.queue.status_readytoreplace,
    Completed: i18n.m.queue.status_completed,
    Failed: i18n.m.queue.status_failed,
    Cancelled: i18n.m.queue.status_cancelled,
  })
  function statusLabel(status: string): string {
    return STATUS_LABELS[status] ?? status
  }

  function parseReport(job: Job): VerificationCheck[] | null {
    if (!job.verificationReportJson) return null
    try {
      const report = JSON.parse(job.verificationReportJson) as VerificationReport
      return report.checks ?? null
    } catch {
      return null
    }
  }

  function toggle(job: Job) {
    if (!parseReport(job)) return
    expandedId = expandedId === job.id ? null : job.id
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
  // Two clear buckets: finished = completed; errored = failed or cancelled.
  let finishedClearable = $derived(jobs.filter((j) => j.status === 'Completed' && j.clearable).length)
  let finishedProtected = $derived(jobs.filter((j) => j.status === 'Completed' && !j.clearable).length)
  let erroredClearable = $derived(jobs.filter((j) => (j.status === 'Failed' || j.status === 'Cancelled') && j.clearable).length)
  // Pending = not-yet-applied work that a reset can safely discard (queued + verified-but-unreplaced).
  let pendingCount = $derived(jobs.filter((j) => j.status === 'Queued' || j.status === 'ReadyToReplace').length)
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.queue}</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    {i18n.m.queue.subtitle}
    {#if activeCount > 0}<span class="text-slate-400">{t(i18n.m.queue.active_suffix, { count: activeCount })}</span>{/if}
  </p>
</header>

<!-- Queue | Failures: live work and the diagnostics view for failed jobs live together, so the
     sidebar stays lean and job views are in one place. -->
<div class="mb-5 flex gap-1 border-b border-slate-200 dark:border-slate-700">
  <button
    class="-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === 'queue'
      ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
      : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}"
    onclick={() => (activeTab = 'queue')}
  >
    {i18n.m.nav.queue}{#if activeCount > 0} ({activeCount}){/if}
  </button>
  <button
    class="-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === 'failures'
      ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
      : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}
      {counts.failed > 0 && activeTab !== 'failures' ? '!text-red-600 dark:!text-red-400' : ''}"
    onclick={() => (activeTab = 'failures')}
  >
    {i18n.m.queue.tab_failures}{#if counts.failed > 0} ({counts.failed}){/if}
  </button>
</div>

{#if activeTab === 'failures'}
  <FailuresPanel />
{:else}

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

<!-- Hero: what's being processed right now, with live progress, CPU/GPU usage, and a backdrop
     pulled from a connected media server (when available). -->
{#if !loading && processingJobs.length > 0}
  {@const heroId = processingJobs[0].id}
  {@const heroArt = artworkLoaded[heroId]}
  <div class="card relative mb-4 overflow-hidden">
    <img
      src="/api/jobs/{heroId}/artwork"
      alt=""
      class="pointer-events-none absolute inset-0 h-full w-full object-cover transition-opacity duration-700 {heroArt ? 'opacity-20 dark:opacity-30' : 'opacity-0'}"
      onload={() => (artworkLoaded[heroId] = true)}
      onerror={() => (artworkLoaded[heroId] = false)}
    />
    {#if heroArt}
      <div class="pointer-events-none absolute inset-0 bg-gradient-to-r from-white via-white/80 to-white/40 dark:from-slate-900 dark:via-slate-900/80 dark:to-slate-900/40"></div>
    {/if}
    <div class="relative space-y-4 p-4">
        {#each processingJobs as job (job.id)}
          {@const telemetry = live[job.id]}
          {@const gpu = job.videoEncoder ? isGpuEncoder(job.videoEncoder) : false}
          <div>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <div class="min-w-0">
                <div class="text-[11px] font-semibold uppercase tracking-wide text-cyan-600 dark:text-cyan-400">
                  {job.status === 'Transcoding' ? i18n.m.queue.now_encoding : job.status === 'Probing' ? i18n.m.queue.now_probing : i18n.m.queue.now_verifying}
                </div>
                <div class="truncate font-medium text-slate-800 dark:text-slate-100" title={job.relativePath ?? ''}>
                  {heroTitle(job.relativePath) ?? jobName(job)}
                </div>
                {#if heroFolder(job.relativePath)}
                  <div class="truncate text-xs text-slate-400 dark:text-slate-500">{heroFolder(job.relativePath)}</div>
                {/if}
                {#if job.enqueueReason}
                  <div class="truncate text-xs text-slate-400 dark:text-slate-500" title={job.enqueueReason}>{job.enqueueReason}</div>
                {/if}
              </div>
              {#if job.videoEncoder}
                <span class="badge {gpu ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
                  {gpu ? 'GPU' : 'CPU'} · {job.videoEncoder}
                </span>
              {/if}
            </div>

            {#if job.status === 'Transcoding'}
              <div class="mt-3 flex items-center gap-3">
                <div class="progress-track h-2 flex-1"><div class="progress-fill" style="width: {Math.round(job.progress * 100)}%"></div></div>
                <span class="w-12 text-right text-sm font-semibold tabular-nums text-slate-600 dark:text-slate-300">{Math.round(job.progress * 100)}%</span>
              </div>
              {#if telemetry}
                <div class="mt-1.5 flex gap-4 text-xs tabular-nums text-slate-400">
                  {#if telemetry.fps != null}<span>{telemetry.fps.toFixed(0)} fps</span>{/if}
                  {#if telemetry.speed != null}<span>{speedLabel(telemetry.speed)}</span>{/if}
                  {#if telemetry.etaSeconds != null}<span>{etaLabel(telemetry.etaSeconds)}</span>{/if}
                </div>
              {/if}
              <div class="mt-3 grid gap-3 sm:grid-cols-2">
                <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="rgb(56,189,248)" />
                <UsageGraph
                  label="GPU"
                  data={activity.gpuHistory}
                  current={activity.metrics?.gpuPercent ?? null}
                  color="rgb(34,197,94)"
                  unavailable={gpuUnavailable}
                  detail={activity.metrics?.gpuEngine}
                />
              </div>
            {:else}
              {#if job.status === 'Verifying' && job.progress > 0}
                <div class="mt-3 flex items-center gap-3">
                  <div class="progress-track h-2 flex-1"><div class="progress-fill" style="width: {Math.round(job.progress * 100)}%"></div></div>
                  <span class="w-12 text-right text-sm font-semibold tabular-nums text-slate-600 dark:text-slate-300">{Math.round(job.progress * 100)}%</span>
                </div>
              {:else}
                <div class="mt-3 progress-track"><div class="progress-indeterminate"></div></div>
              {/if}
              <div class="mt-1.5 text-xs text-sky-600 dark:text-sky-400">{job.status === 'Probing' ? i18n.m.queue.probing_source : i18n.m.queue.verifying_output}</div>
              {#if job.status === 'Verifying'}
                <!-- VMAF scoring is CPU-only, so show just the CPU graph to convey the high load. -->
                <div class="mt-3">
                  <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="rgb(56,189,248)" />
                </div>
              {/if}
            {/if}
          </div>
        {/each}
      </div>
  </div>
{:else if !loading && jobs.length > 0}
  <div class="card mb-4 p-4">
    <div class="flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
      <Icon name="pause" class="h-4 w-4 text-slate-400" />
      <span>{i18n.m.queue.nothing_processing}{#if queuedCount > 0}{plural(queuedCount, i18n.m.queue.queued_waiting_one, i18n.m.queue.queued_waiting_other, queuedCount.toLocaleString())}{/if}</span>
    </div>
    <!-- The waiting-window reason is shown once, by the dispatch-state card below; don't repeat it here. -->
  </div>
{/if}

<!-- Only surface dispatch state when it needs attention (paused, or a backlog stalled on a
     closed window). The healthy "ready · N running · work free" line was noise, so it's dropped. -->
{#if queueStatus && !queueStatus.canStart}
  <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">
    {t(i18n.m.queue.dispatch_paused, { reason: queueStatus.blockedReason ?? '' })}
  </div>
{:else if queueStatus?.waitingReason}
  <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">
    {t(i18n.m.queue.waiting_window, { reason: queueStatus.waitingReason })}
  </div>
{/if}

{#if !loading && jobs.length > 0}
  <div class="mb-4 flex flex-wrap items-center gap-2">
    {#each [['all', i18n.m.queue.filter_all], ['active', i18n.m.queue.filter_active], ['completed', i18n.m.queue.filter_completed], ['failed', i18n.m.queue.filter_failed], ['verified', i18n.m.queue.filter_verified], ['verifyFailed', i18n.m.queue.filter_verify_failed]] as [key, label]}
      <button
        class="badge cursor-pointer border {filter === key
          ? 'border-cyan-300 bg-cyan-100 text-cyan-700 dark:border-cyan-800 dark:bg-cyan-900/40 dark:text-cyan-300'
          : 'border-transparent bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700'}
          {key === 'failed' && counts.failed > 0 && filter !== 'failed' ? '!text-red-600 dark:!text-red-400' : ''}"
        onclick={() => selectFilter(key as typeof filter)}
      >
        {label} · {counts[key as keyof typeof counts]}
      </button>
    {/each}
    <div class="ml-auto flex items-center gap-2">
      {#if pendingCount > 0}
        <button
          class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs"
          onclick={clearPending}
          disabled={clearingPending}
          title={i18n.m.queue.clear_queue_title}
        >
          <Icon name="trash" class="h-4 w-4" />
          {clearingPending ? i18n.m.queue.clearing : t(i18n.m.queue.clear_queue, { count: pendingCount.toLocaleString() })}
        </button>
      {/if}
      {#if erroredClearable > 0}
        <button
          class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs"
          onclick={() => clear('errored')}
          disabled={clearingScope !== null}
          title={i18n.m.queue.clear_errored_title}
        >
          <Icon name="trash" class="h-4 w-4" />
          {clearingScope === 'errored' ? i18n.m.queue.clearing : t(i18n.m.queue.clear_errored, { count: erroredClearable })}
        </button>
      {/if}
      {#if counts.completed > 0}
        <button
          class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs"
          onclick={() => clear('finished')}
          disabled={clearingScope !== null || finishedClearable === 0}
          title={finishedClearable > 0 ? i18n.m.queue.clear_completed_title_available : i18n.m.queue.clear_completed_title_protected}
        >
          <Icon name="trash" class="h-4 w-4" />
          {clearingScope === 'finished' ? i18n.m.queue.clearing : finishedClearable > 0 ? t(i18n.m.queue.clear_completed, { count: finishedClearable }) : t(i18n.m.queue.completed_protected, { count: finishedProtected })}
        </button>
      {/if}
    </div>
  </div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
{:else if visibleJobs.length > 0}
  <div class="card overflow-hidden">
    <div
      bind:this={tableScrollEl}
      class="overflow-auto"
      style="max-height: {tableMaxHeight}; transition: max-height 0.3s ease-out;"
    >
    <table class="w-full text-sm">
      <thead class="sticky top-0 z-10 border-b border-slate-200 bg-white text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">{i18n.m.queue.col_status}</th>
          <th class="px-4 py-3">{i18n.m.queue.col_file}</th>
          <th class="w-32 px-4 py-3 sm:w-48">{i18n.m.queue.col_progress}</th>
          <th class="hidden px-4 py-3 md:table-cell">{i18n.m.queue.col_verification}</th>
          <th class="hidden px-4 py-3 lg:table-cell">{i18n.m.queue.col_priority}</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each pagedJobs as job (job.id)}
          {@const checks = parseReport(job)}
          <tr
            class="cursor-pointer text-slate-700 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800/50 {selectedJobId === job.id ? 'bg-slate-50 dark:bg-slate-900/40' : ''}"
            onclick={() => selectRow(job.id)}
          >
            <td class="px-4 py-2"><span class="badge {badgeClass(job.status)}">{statusLabel(job.status)}</span></td>
            <td class="max-w-[40vw] px-4 py-2 sm:max-w-xs">
              <div class="truncate font-mono text-xs" title={job.relativePath ?? ''}>{job.relativePath ?? '—'}</div>
              {#if job.enqueueReason}
                <div class="mt-0.5 truncate text-xs text-slate-400 dark:text-slate-500" title={job.enqueueReason}>{job.enqueueReason}</div>
              {/if}
              {#if job.videoEncoder}
                {@const gpu = isGpuEncoder(job.videoEncoder)}
                <span
                  class="badge mt-1 {gpu ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}"
                  title={i18n.m.queue.encoder_title}
                >{gpu ? 'GPU' : 'CPU'} · {job.videoEncoder}</span>
              {/if}
            </td>
            <td class="px-4 py-2">
              {#if job.status === 'Transcoding'}
                <div class="space-y-1">
                  <div class="flex items-center gap-2">
                    <div class="progress-track">
                      <div class="progress-fill" style="width: {Math.round(job.progress * 100)}%"></div>
                    </div>
                    <span class="w-9 text-right text-xs tabular-nums text-slate-500">{Math.round(job.progress * 100)}%</span>
                  </div>
                  {#if telemetryLabel(live[job.id])}
                    <div class="text-[11px] tabular-nums text-slate-400">{telemetryLabel(live[job.id])}</div>
                  {/if}
                </div>
              {:else if job.status === 'Probing' || job.status === 'Verifying'}
                <div class="space-y-1">
                  {#if job.status === 'Verifying' && job.progress > 0}
                    <div class="flex items-center gap-2">
                      <div class="progress-track"><div class="progress-fill" style="width: {Math.round(job.progress * 100)}%"></div></div>
                      <span class="w-9 text-right text-xs tabular-nums text-slate-500">{Math.round(job.progress * 100)}%</span>
                    </div>
                  {:else}
                    <div class="progress-track"><div class="progress-indeterminate"></div></div>
                  {/if}
                  <div class="text-[11px] text-sky-600 dark:text-sky-400">{job.status === 'Probing' ? i18n.m.queue.stage_probing : i18n.m.queue.stage_verifying}</div>
                </div>
              {:else if job.status === 'Queued'}
                <span class="text-xs text-slate-400">{i18n.m.queue.stage_waiting}</span>
              {:else if job.status === 'Failed'}
                <div class="flex items-start gap-1 text-xs text-red-600 dark:text-red-400" title={jobFailureDescription(job.failureCategory, i18n.m)}>
                  <Icon name="warning" class="h-3.5 w-3.5 mt-0.5 flex-shrink-0" />
                  <span class="line-clamp-2">{jobFailureDescription(job.failureCategory, i18n.m)}</span>
                </div>
              {:else}
                <span class="text-xs text-slate-400">—</span>
              {/if}
            </td>
            <td class="hidden px-4 py-2 md:table-cell">
              {#if checks}
                <button
                  class="text-xs font-medium hover:underline {job.verificationPassed ? 'text-emerald-600 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'}"
                  onclick={(e) => { e.stopPropagation(); toggle(job) }}
                >
                  {job.verificationPassed ? i18n.m.queue.verify_passed : i18n.m.queue.verify_failed}
                  {#if job.outputSizeBytes}<span class="text-slate-400"> · {formatSize(job.outputSizeBytes)}</span>{/if}
                  <span class="text-slate-400">{expandedId === job.id ? ' ▾' : ' ▸'}</span>
                </button>
              {:else}
                <span class="text-xs text-slate-400">—</span>
              {/if}
            </td>
            <td class="hidden px-4 py-2 text-xs lg:table-cell">{job.priority}</td>
            <td class="px-4 py-2 text-right">
              <div class="flex justify-end gap-1">
                {#if job.status === 'ReadyToReplace' && job.verificationPassed}
                  <button class="btn btn-primary inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={(e) => { e.stopPropagation(); replace(job) }} disabled={replacingId === job.id}>
                    <Icon name="replace" class="h-3.5 w-3.5" />
                    {replacingId === job.id ? i18n.m.queue.action_replacing : i18n.m.queue.action_replace}
                  </button>
                {/if}
                {#if job.status === 'Failed' || job.status === 'Cancelled'}
                  <button class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={(e) => { e.stopPropagation(); retry(job) }} disabled={retryingId === job.id} title={i18n.m.queue.retry_title}>
                    <Icon name="retry" class="h-3.5 w-3.5" />
                    {retryingId === job.id ? i18n.m.queue.action_retrying : i18n.m.queue.action_retry}
                  </button>
                  <button class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={(e) => { e.stopPropagation(); exclude(job) }} disabled={excludingId === job.id} title={i18n.m.queue.exclude_title}>
                    <Icon name="ban" class="h-3.5 w-3.5" />
                    {excludingId === job.id ? i18n.m.queue.action_excluding : i18n.m.queue.action_exclude}
                  </button>
                  <button class="btn btn-danger inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={(e) => { e.stopPropagation(); stopAndRemove(job) }} disabled={removingId === job.id} title={i18n.m.queue.remove_title}>
                    <Icon name="trash" class="h-3.5 w-3.5" />
                    {removingId === job.id ? i18n.m.queue.action_removing : i18n.m.queue.action_remove}
                  </button>
                {/if}
                {#if isActive(job.status)}
                  <button class="btn btn-danger inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={(e) => { e.stopPropagation(); stopAndRemove(job) }} disabled={removingId === job.id}>
                    <Icon name="x" class="h-3.5 w-3.5" />
                    {removingId === job.id ? i18n.m.queue.action_stopping : i18n.m.queue.action_stop_remove}
                  </button>
                {/if}
              </div>
            </td>
          </tr>
          {#if expandedId === job.id && checks}
            <tr class="bg-slate-50 dark:bg-slate-900/40">
              <td colspan="6" class="px-4 py-3">
                <VerificationChecks {checks} />
              </td>
            </tr>
          {/if}
        {/each}
      </tbody>
    </table>
    </div>
  </div>
  <div class="mt-2 flex items-center justify-between text-xs text-slate-400">
    <span>
      {t(i18n.m.queue.range, {
        start: (queuePageStart + 1).toLocaleString(),
        end: Math.min(queuePageStart + QUEUE_PAGE_SIZE, visibleJobs.length).toLocaleString(),
        total: visibleJobs.length.toLocaleString(),
      })}{visibleJobs.length !== jobs.length ? t(i18n.m.queue.range_total_suffix, { total: jobs.length.toLocaleString() }) : ''}
    </span>
    {#if queuePageCount > 1}
      <div class="flex items-center gap-2">
        <button class="btn px-2 py-1 text-xs" onclick={() => goToQueuePage(queuePage - 1)} disabled={queuePage <= 1} aria-label={i18n.m.queue.prev_page}>‹</button>
        <span>{t(i18n.m.queue.page_of, { page: Math.min(queuePage, queuePageCount), count: queuePageCount })}</span>
        <button class="btn px-2 py-1 text-xs" onclick={() => goToQueuePage(queuePage + 1)} disabled={queuePage >= queuePageCount} aria-label={i18n.m.queue.next_page}>›</button>
      </div>
    {/if}
  </div>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    {i18n.m.queue.empty}
  </div>
{/if}

<!-- Detail bottom sheet: slides up on row selection, with live progress, telemetry, and a
     CPU/GPU usage graph while the job is encoding. -->
<BottomSheet open={selectedJob !== null} bind:expanded={sheetExpanded} bind:height={sheetHeight} onclose={() => (selectedJobId = null)}>
  {#snippet header()}
    {#if selectedJob}
      <div class="flex min-w-0 items-center gap-2">
        <span class="badge flex-shrink-0 {badgeClass(selectedJob.status)}">{statusLabel(selectedJob.status)}</span>
        <p class="min-w-0 flex-1 truncate font-mono text-xs text-slate-700 dark:text-slate-200" title={selectedJob.relativePath ?? ''}>
          {selectedJob.relativePath ?? '—'}
        </p>
      </div>
    {/if}
  {/snippet}
  {#snippet children()}
    {#if selectedJob}
      {@const telemetry = live[selectedJob.id]}
      <!-- Live progress/stage spans the full width; the box art lives beside the detail/verification
           body below, not up here. -->
      <div class="mb-4">
        {#if selectedJob.status === 'Transcoding'}
          <div class="flex items-center gap-3">
            <div class="progress-track h-2 flex-1"><div class="progress-fill" style="width: {Math.round(selectedJob.progress * 100)}%"></div></div>
            <span class="w-12 text-right text-sm font-semibold tabular-nums text-slate-600 dark:text-slate-300">{Math.round(selectedJob.progress * 100)}%</span>
          </div>
          {#if telemetry}
            <div class="mt-1.5 flex gap-4 text-xs tabular-nums text-slate-400">
              {#if telemetry.fps != null}<span>{telemetry.fps.toFixed(0)} fps</span>{/if}
              {#if telemetry.speed != null}<span>{speedLabel(telemetry.speed)}</span>{/if}
              {#if telemetry.etaSeconds != null}<span>{etaLabel(telemetry.etaSeconds)}</span>{/if}
            </div>
          {/if}
        {:else if selectedJob.status === 'Probing' || selectedJob.status === 'Verifying'}
          <div class="progress-track"><div class="progress-indeterminate"></div></div>
          <p class="mt-1.5 text-xs text-sky-600 dark:text-sky-400">{selectedJob.status === 'Probing' ? i18n.m.queue.stage_probing_full : i18n.m.queue.stage_verifying_full}</p>
        {:else if selectedJob.status === 'Failed'}
          <p class="text-sm text-red-600 dark:text-red-400">{jobFailureDescription(selectedJob.failureCategory, i18n.m)}</p>
          {#if selectedJob.errorMessage}
            <details class="mt-2 text-xs text-slate-500 dark:text-slate-400">
              <summary class="cursor-pointer">{i18n.m.queue.technical_error}</summary>
              <p class="mt-1 whitespace-pre-line break-words font-mono text-[11px] text-red-600 dark:text-red-400">{selectedJob.errorMessage}</p>
            </details>
          {/if}
        {:else}
          <p class="text-sm text-slate-500 dark:text-slate-400">{statusLabel(selectedJob.status)}</p>
        {/if}
      </div>

      <!-- The hero panel above already shows this job's live CPU/GPU usage while it encodes, so
           the graph isn't repeated here; the sheet shows the technical detail (command) instead. -->
      {#if selectedJob.status === 'Transcoding'}
        <p class="mb-4 flex items-center gap-1.5 text-xs text-slate-400">
          <Icon name="chevron" class="h-3.5 w-3.5 rotate-180" />
          {i18n.m.queue.usage_above}
        </p>
      {/if}

      <!-- Body: technical detail and the verification report on the left, box art floated to their
           right as a recognition aid for the title. -->
      <div class="flex flex-col gap-4 sm:flex-row">
        <div class="min-w-0 flex-1">
      <!-- Details -->
      <dl class="grid gap-x-8 gap-y-3 text-sm sm:grid-cols-2 lg:grid-cols-3">
        <div class="flex justify-between gap-4">
          <dt class="text-slate-500">{i18n.m.queue.detail_encoder}</dt>
          <dd class="text-right">{selectedJob.videoEncoder ?? '—'}{#if selectedJob.videoEncoder}<span class="ml-1 text-slate-400">({isGpuEncoder(selectedJob.videoEncoder) ? 'GPU' : 'CPU'})</span>{/if}</dd>
        </div>
        <div class="flex justify-between gap-4"><dt class="text-slate-500">{i18n.m.queue.detail_output_size}</dt><dd>{selectedJob.outputSizeBytes ? formatSize(selectedJob.outputSizeBytes) : '—'}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-slate-500">{i18n.m.queue.detail_priority}</dt><dd>{selectedJob.priority}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-slate-500">{i18n.m.queue.detail_verified}</dt><dd class="text-right">{selectedJob.verifiedAt ? new Date(selectedJob.verifiedAt).toLocaleString() : '—'}</dd></div>
      </dl>

      <!-- The exact ffmpeg command for this job — the "under the hood" view that complements the
           hero's live status. Useful while encoding and for diagnosing a failed job. -->
      {#if selectedJob.ffmpegArguments}
        <div class="mt-4">
          <div class="mb-1 text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.queue.ffmpeg_command}</div>
          <pre class="max-h-44 overflow-auto whitespace-pre-wrap break-all rounded-md bg-slate-50 p-3 font-mono text-[11px] leading-relaxed text-slate-600 dark:bg-slate-900/60 dark:text-slate-300">ffmpeg {selectedJob.ffmpegArguments}</pre>
        </div>
      {/if}

      <!-- Verification report, when one exists -->
      {#if parseReport(selectedJob)}
        {@const checks = parseReport(selectedJob)}
        <div class="mt-4 border-t border-slate-100 pt-4 dark:border-slate-800">
          {#if checks}<VerificationChecks {checks} />{/if}
        </div>
      {/if}
        </div>
        <Thumbnail mediaFileId={selectedJob.mediaFileId} alt={selectedJob.relativePath ?? ''} size="lg" />
      </div>

      <!-- Actions -->
      <div class="mt-4 flex flex-wrap gap-2">
        {#if selectedJob.status === 'ReadyToReplace' && selectedJob.verificationPassed}
          <button class="btn btn-primary px-3 py-1 text-xs" onclick={() => selectedJob && replace(selectedJob)} disabled={replacingId === selectedJob.id}>
            {replacingId === selectedJob.id ? i18n.m.queue.action_replacing_ellipsis : i18n.m.queue.action_replace_original}
          </button>
        {/if}
        {#if selectedJob.status === 'Failed' || selectedJob.status === 'Cancelled'}
          <button class="btn px-3 py-1 text-xs" onclick={() => selectedJob && retry(selectedJob)} disabled={retryingId === selectedJob.id}>
            {retryingId === selectedJob.id ? i18n.m.queue.action_retrying_ellipsis : i18n.m.queue.action_retry}
          </button>
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
    {/if}
  {/snippet}
</BottomSheet>
{/if}
