<script lang="ts">
  import type { Job } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import type { DashboardState } from '../dashboard-state'
  import { jobLocation, verificationPhase } from '../job-presentation'

  let { jobs, state: queueState }: { jobs: Job[]; state: DashboardState | null } = $props()

  // The stage a job reports is not always the stage its status names: a job selecting a
  // per-title quality is measuring candidates, not probing the source, and saying "probing"
  // sent people looking for a disk problem that was not there.
  function stageLabel(job: Job): string {
    const phase = verificationPhase(job)
    if (phase === 'waiting') return i18n.m.queue.status_awaitingverification
    if (phase === 'evidence') return i18n.m.queue.phase_evidence
    if (phase === 'media') return i18n.m.queue.phase_media
    if (job.workerName && job.remoteStage) {
      const remote = i18n.m.dashboard.remote_stage as Record<string, string>
      return remote[job.remoteStage] ?? job.remoteStage
    }
    const local = i18n.m.dashboard.local_stage as Record<string, string>
    return local[job.status] ?? job.status
  }

  function locationLabel(job: Job): string {
    const location = jobLocation(job)
    return location === 'worker' ? job.workerName ?? i18n.m.queue.lane_workers
      : location === 'transfer' ? i18n.m.queue.location_transfer : i18n.m.dashboard.this_server
  }

  function fileName(path: string | null): string {
    if (!path) return i18n.m.dashboard.unnamed_file
    const parts = path.split('/')
    return parts[parts.length - 1] || path
  }

  // Verification is the stage where a pass is worth colouring differently from work in progress.
  function verifying(job: Job): boolean {
    return job.status === 'Verifying' || job.status === 'AwaitingVerification' || job.remoteStage === 'Verifying'
  }

  // The server sends the mode as its enum name. Read it out in words, not in PascalCase.
  function qualityMode(mode: string | null): string | null {
    if (!mode) return null
    const modes = i18n.m.dashboard.quality_mode as Record<string, string>
    return modes[mode] ?? mode
  }

  let percent = (job: Job) => Math.max(0, Math.min(100, Math.round(job.progress * 100)))

  // A dashboard answers a question at a glance, so the list is capped and says what it is
  // hiding. Real servers routinely carry a dozen or more outstanding jobs — thirteen the day
  // this was written — which would push everything below it off the screen.
  const MAX_ROWS = 6
  let shown = $derived(jobs.slice(0, MAX_ROWS))
  let hidden = $derived(Math.max(0, jobs.length - MAX_ROWS))
</script>

<div class="card mb-4">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <span class="label mb-0">{i18n.m.dashboard.in_flight}</span>
    <span class="ml-auto font-mono text-xs text-ink-3">
      {t(i18n.m.dashboard.in_flight_counts, {
        running: jobs.length.toLocaleString(),
        queued: (queueState?.queued ?? 0).toLocaleString(),
      })}
    </span>
  </div>

  {#if jobs.length === 0}
    <!-- An empty list is not an error, and it is not the same as an idle server. The status bar
         has already said which; this repeats the consequence where a reader is looking for work,
         rather than leaving a blank panel. -->
    <p class="px-4 py-5 text-sm text-ink-3">
      {#if queueState?.kind === 'idle'}
        {i18n.m.dashboard.in_flight_idle}
      {:else if queueState?.detail}
        {queueState.detail}
      {:else}
        {i18n.m.dashboard.in_flight_none}
      {/if}
    </p>
  {:else}
    <ul class="m-0 list-none p-0">
      {#each shown as job (job.id)}
        <li class="grid gap-3 border-b border-line px-4 py-3 last:border-b-0 md:grid-cols-[1fr_200px_140px] md:items-center">
          <div class="min-w-0">
            <button
              class="block w-full truncate text-left font-mono text-sm text-ink hover:underline"
              onclick={() => router.go('/queue')}
              title={job.relativePath ?? undefined}
            >{fileName(job.relativePath)}</button>
            <div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-ink-3">
              {#if job.videoEncoder}<span class="badge border border-line font-mono font-normal">{job.videoEncoder}</span>{/if}
              {#if job.effectiveVideoQuality != null}<span class="badge border border-line font-mono font-normal">CRF {job.effectiveVideoQuality}</span>{/if}
              <span class="truncate" title={job.workerName && !job.remoteStage ? t(i18n.m.queue.now_returned, { worker: job.workerName }) : undefined}>{locationLabel(job)}</span>
            </div>
          </div>

          <div>
            <div class="mb-1.5 font-mono text-[10px] uppercase tracking-wider {verifying(job) ? 'text-ok' : 'text-accent'}">
              {stageLabel(job)}
            </div>
            <div class="progress-track">
              {#if job.progress > 0}
                <div class="progress-fill {verifying(job) ? '!bg-emerald-600' : ''}" style="width: {percent(job)}%"></div>
              {:else}
                <!-- A stage with no percentage of its own gets the indeterminate sweep rather than
                     a bar frozen at zero, which reads as stalled. -->
                <div class="progress-indeterminate"></div>
              {/if}
            </div>
            <div class="mt-1.5 font-mono text-xs tabular-nums text-ink-3">
              {job.progress > 0 ? `${percent(job)}%` : i18n.m.dashboard.no_percentage}
            </div>
          </div>

          <div class="font-mono text-xs tabular-nums text-ink-3 md:text-right">
            {#if qualityMode(job.videoQualityMode)}<div>{qualityMode(job.videoQualityMode)}</div>{/if}
            {#if job.qualityRetryCount > 0}
              <div class="text-warn">{t(i18n.m.dashboard.retry_count, { count: job.qualityRetryCount.toLocaleString() })}</div>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
    {#if hidden > 0}
      <button
        class="w-full border-t border-line px-4 py-2.5 text-left text-xs text-ink-3 transition-colors hover:text-accent"
        onclick={() => router.go('/queue')}
      >{t(i18n.m.dashboard.in_flight_more, { count: hidden.toLocaleString() })}</button>
    {/if}
  {/if}
</div>
