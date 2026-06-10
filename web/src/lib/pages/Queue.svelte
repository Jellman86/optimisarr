<script lang="ts">
  import { api, type Job, type QueueStatus, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize, formatDuration } from '../format'
  import { createJobsConnection, type JobProgress } from '../realtime'
  import { router } from '../stores/ui.svelte'
  import Icon from '../components/Icon.svelte'

  let jobs = $state<Job[]>([])
  let queueStatus = $state<QueueStatus | null>(null)
  // Live transcode telemetry keyed by job id, pushed over SignalR between reloads.
  let live = $state<Record<number, JobProgress>>({})
  let error = $state<string | null>(null)
  let loading = $state(true)
  let cancellingId = $state<number | null>(null)
  let replacingId = $state<number | null>(null)
  let retryingId = $state<number | null>(null)
  let expandedId = $state<number | null>(null)
  let filter = $state<'all' | 'active' | 'completed' | 'failed'>('all')

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
      error = err instanceof Error ? err.message : 'Unable to load jobs'
    } finally {
      loading = false
    }
  }

  function speedLabel(speed: number | null): string {
    return speed == null ? '' : `${speed.toFixed(speed < 10 ? 2 : 1)}×`
  }

  function etaLabel(seconds: number | null): string {
    if (seconds == null) return ''
    return seconds < 60 ? `~${Math.round(seconds)}s left` : `~${formatDuration(seconds)} left`
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
      error = err instanceof Error ? err.message : 'Cancel failed'
    } finally {
      cancellingId = null
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
      error = err instanceof Error ? err.message : 'Retry failed'
    } finally {
      retryingId = null
    }
  }

  function matchesFilter(status: string): boolean {
    switch (filter) {
      case 'active': return isActive(status)
      case 'completed': return status === 'Completed'
      case 'failed': return status === 'Failed'
      default: return true
    }
  }

  let counts = $derived({
    all: jobs.length,
    active: jobs.filter((j) => isActive(j.status)).length,
    completed: jobs.filter((j) => j.status === 'Completed').length,
    failed: jobs.filter((j) => j.status === 'Failed').length,
  })
  let visibleJobs = $derived(jobs.filter((j) => matchesFilter(j.status)))

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
    if (!confirm('Replace the original with the verified output?\n\nThe original is moved to quarantine first and can be rolled back at any time.')) {
      return
    }
    replacingId = job.id
    try {
      await api.replaceFromJob(job.id)
      await load()
      router.go('/quarantine')
    } catch (err) {
      error = err instanceof Error ? err.message : 'Replace failed'
    } finally {
      replacingId = null
    }
  }

  let activeCount = $derived(jobs.filter((j) => isActive(j.status)).length)
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Queue</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    Transcode jobs. Outputs are verified before they are marked ready — your originals are never touched.
    {#if activeCount > 0}<span class="text-slate-400"> · {activeCount} active</span>{/if}
  </p>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

{#if queueStatus && !queueStatus.canStart}
  <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">
    Queue dispatch is paused: {queueStatus.blockedReason}
  </div>
{:else if queueStatus}
  <div class="card mb-4 p-3 text-xs text-slate-500 dark:text-slate-400">
    Dispatch ready · {queueStatus.runningJobs}/{queueStatus.maxConcurrentJobs} running · work free:
    {queueStatus.freeDiskBytes === null ? 'unknown' : formatSize(queueStatus.freeDiskBytes)}
  </div>
{/if}

{#if !loading && jobs.length > 0}
  <div class="mb-4 flex flex-wrap gap-2">
    {#each [['all', 'All'], ['active', 'Active'], ['completed', 'Completed'], ['failed', 'Failed']] as [key, label]}
      <button
        class="badge cursor-pointer border {filter === key
          ? 'border-emerald-300 bg-emerald-100 text-emerald-700 dark:border-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300'
          : 'border-transparent bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700'}
          {key === 'failed' && counts.failed > 0 && filter !== 'failed' ? '!text-red-600 dark:!text-red-400' : ''}"
        onclick={() => (filter = key as typeof filter)}
      >
        {label} · {counts[key as keyof typeof counts]}
      </button>
    {/each}
  </div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if visibleJobs.length > 0}
  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">Status</th>
          <th class="px-4 py-3">File</th>
          <th class="px-4 py-3 w-48">Progress</th>
          <th class="px-4 py-3">Verification</th>
          <th class="px-4 py-3">Priority</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each visibleJobs as job (job.id)}
          {@const checks = parseReport(job)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2"><span class="badge {badgeClass(job.status)}">{job.status}</span></td>
            <td class="max-w-xs truncate px-4 py-2 font-mono text-xs" title={job.relativePath ?? ''}>{job.relativePath ?? '—'}</td>
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
                  <div class="progress-track"><div class="progress-indeterminate"></div></div>
                  <div class="text-[11px] text-sky-600 dark:text-sky-400">{job.status === 'Probing' ? 'probing…' : 'verifying…'}</div>
                </div>
              {:else if job.status === 'Queued'}
                <span class="text-xs text-slate-400">waiting…</span>
              {:else if job.status === 'Failed'}
                <div class="flex items-start gap-1 text-xs text-red-600 dark:text-red-400" title={job.errorMessage ?? ''}>
                  <Icon name="warning" class="h-3.5 w-3.5 mt-0.5 flex-shrink-0" />
                  <span class="line-clamp-2">{job.errorMessage ?? 'Job failed'}</span>
                </div>
              {:else}
                <span class="text-xs text-slate-400">—</span>
              {/if}
            </td>
            <td class="px-4 py-2">
              {#if checks}
                <button
                  class="text-xs font-medium hover:underline {job.verificationPassed ? 'text-emerald-600 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'}"
                  onclick={() => toggle(job)}
                >
                  {job.verificationPassed ? '✓ passed' : '✗ failed'}
                  {#if job.outputSizeBytes}<span class="text-slate-400"> · {formatSize(job.outputSizeBytes)}</span>{/if}
                  <span class="text-slate-400">{expandedId === job.id ? ' ▾' : ' ▸'}</span>
                </button>
              {:else}
                <span class="text-xs text-slate-400">—</span>
              {/if}
            </td>
            <td class="px-4 py-2 text-xs">{job.priority}</td>
            <td class="px-4 py-2 text-right">
              <div class="flex justify-end gap-1">
                {#if job.status === 'ReadyToReplace' && job.verificationPassed}
                  <button class="btn btn-primary inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={() => replace(job)} disabled={replacingId === job.id}>
                    <Icon name="replace" class="h-3.5 w-3.5" />
                    {replacingId === job.id ? 'Replacing' : 'Replace'}
                  </button>
                {/if}
                {#if job.status === 'Failed' || job.status === 'Cancelled'}
                  <button class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={() => retry(job)} disabled={retryingId === job.id} title="Re-queue this file as a fresh attempt">
                    <Icon name="retry" class="h-3.5 w-3.5" />
                    {retryingId === job.id ? 'Retrying' : 'Retry'}
                  </button>
                {/if}
                {#if isActive(job.status)}
                  <button class="btn btn-danger inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={() => cancel(job)} disabled={cancellingId === job.id}>
                    <Icon name="x" class="h-3.5 w-3.5" />
                    {cancellingId === job.id ? 'Cancelling' : 'Cancel'}
                  </button>
                {/if}
              </div>
            </td>
          </tr>
          {#if expandedId === job.id && checks}
            <tr class="bg-slate-50 dark:bg-slate-900/40">
              <td colspan="6" class="px-4 py-3">
                <ul class="space-y-1.5">
                  {#each checks as check (check.name)}
                    <li class="flex items-start gap-2 text-xs">
                      <span class={check.outcome === 'Passed' ? 'text-emerald-600 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'}>
                        {check.outcome === 'Passed' ? '✓' : '✗'}
                      </span>
                      <span class="w-32 shrink-0 font-medium text-slate-600 dark:text-slate-300">{check.name}</span>
                      <span class="text-slate-500 dark:text-slate-400">{check.detail}</span>
                    </li>
                  {/each}
                </ul>
              </td>
            </tr>
          {/if}
        {/each}
      </tbody>
    </table>
  </div>
  <p class="mt-2 text-xs text-slate-400">{jobs.length.toLocaleString()} jobs</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    The queue is empty. Enqueue a library's eligible files from the Libraries page.
  </div>
{/if}
