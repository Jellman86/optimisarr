<script lang="ts">
  import type { Job, OptimisationResult } from '../api'
  import { formatRelative, formatSize, mediaTitle, savedPercent } from '../format'
  import PosterGlow from './PosterGlow.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import type { DashboardState } from '../dashboard-state'
  import { jobLocation, verificationPhase } from '../job-presentation'

  let { jobs, state: queueState, lastResult = null }: {
    jobs: Job[]
    state: DashboardState | null
    /** The most recent finished optimisation, shown while nothing runs so the panel is never blank. */
    lastResult?: OptimisationResult | null
  } = $props()

  let lastTitle = $derived(lastResult ? mediaTitle(lastResult.relativePath) : null)

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

<div class="card h-full">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <span class="label mb-0">{i18n.m.dashboard.in_flight}</span>
    <span class="ml-auto text-xs tabular-nums text-ink-3">
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
    <p class="px-4 pt-5 text-sm text-ink-3" class:pb-5={!lastResult}>
      {#if queueState?.kind === 'idle'}
        {i18n.m.dashboard.in_flight_idle}
      {:else if queueState?.detail}
        {queueState.detail}
      {:else}
        {i18n.m.dashboard.in_flight_none}
      {/if}
    </p>
    {#if lastResult && lastTitle}
      <!-- While nothing runs, the last finished title stands in for the work, so the panel
           answers "what has it been doing?" instead of sitting empty. -->
      <section class="last-result" aria-label={i18n.m.dashboard.last_finished}>
        <PosterGlow mediaFileId={lastResult.mediaFileId} />
        <Thumbnail mediaFileId={lastResult.mediaFileId} size="lg" />
        <span class="last-result-text">
          <span class="label mb-0">{i18n.m.dashboard.last_finished}</span>
          <strong>{lastTitle.primary ?? i18n.m.dashboard.unnamed_file}</strong>
          {#if lastTitle.episode}<span class="text-sm text-ink-2">{[lastTitle.episode, lastTitle.secondary].filter(Boolean).join(' · ')}</span>{/if}
          <span class="mt-2 text-sm tabular-nums text-ink-2">{formatSize(lastResult.sourceSizeBytes)} → {formatSize(lastResult.outputSizeBytes)} <span class="font-semibold text-accent">−{savedPercent(lastResult.sourceSizeBytes, lastResult.outputSizeBytes)}%</span></span>
          <span class="text-xs text-ink-3">{[lastResult.libraryName, lastResult.workerName ?? i18n.m.dashboard.this_server, formatRelative(lastResult.finishedAt, new Date(), i18n.locale)].filter(Boolean).join(' · ')}</span>
        </span>
      </section>
    {/if}
  {:else}
    <ul class="m-0 list-none p-0">
      {#each shown as job (job.id)}
        {@const title = mediaTitle(job.relativePath)}
        <li class="grid gap-3 border-b border-line px-4 py-3 last:border-b-0 md:grid-cols-[1fr_200px_140px] md:items-center">
          <div class="min-w-0">
            <div class="flex min-w-0 items-center gap-3">
              <Thumbnail mediaFileId={job.mediaFileId} size="sm" />
              <button
                class="block min-w-0 flex-1 truncate text-left text-sm font-medium text-ink hover:underline"
                onclick={() => router.go('/queue')}
                title={job.relativePath ?? undefined}
              >{title.primary ?? fileName(job.relativePath)}{#if title.episode}{' '}<span class="font-normal text-ink-3">{[title.episode, title.secondary].filter(Boolean).join(' · ')}</span>{/if}</button>
            </div>
            <div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-ink-3">
              {#if job.videoEncoder}<span class="badge border border-line font-mono font-normal">{job.videoEncoder}</span>{/if}
              {#if job.effectiveVideoQuality != null}<span class="badge border border-line font-mono font-normal">CRF {job.effectiveVideoQuality}</span>{/if}
              <span class="truncate" title={job.workerName && !job.remoteStage ? t(i18n.m.queue.now_returned, { worker: job.workerName }) : undefined}>{locationLabel(job)}</span>
            </div>
          </div>

          <div>
            <div class="mb-1.5 text-[11px] font-semibold {verifying(job) ? 'text-ok' : 'text-accent'}">
              {stageLabel(job)}
            </div>
            <div class="progress-track">
              {#if job.progress > 0}
                <div class="progress-fill {verifying(job) ? 'progress-fill-verifying' : ''}" style="width: {percent(job)}%"></div>
              {:else}
                <!-- A stage with no percentage of its own gets the indeterminate sweep rather than
                     a bar frozen at zero, which reads as stalled. -->
                <div class="progress-indeterminate"></div>
              {/if}
            </div>
            <div class="mt-1.5 text-xs tabular-nums text-ink-3">
              {job.progress > 0 ? `${percent(job)}%` : i18n.m.dashboard.no_percentage}
            </div>
          </div>

          <div class="text-xs tabular-nums text-ink-3 md:text-right">
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

<style>
  .last-result {
    position: relative;
    display: flex;
    align-items: center;
    gap: 1rem;
    width: calc(100% - 2rem);
    margin: 1rem;
    padding: 1rem;
    overflow: hidden;
    border-radius: 12px;
    background: var(--sunken);
    text-align: left;
  }
  .last-result > :global(:not(.poster-glow)) { position: relative; }
  .last-result-text { display: flex; min-width: 0; flex-direction: column; gap: 0.15rem; }
  .last-result-text strong {
    overflow: hidden;
    color: var(--ink);
    font-size: 1.05rem;
    font-weight: 600;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
