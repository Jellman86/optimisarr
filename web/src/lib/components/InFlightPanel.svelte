<script lang="ts">
  import type { Job, OptimisationResult, QueueStatus } from '../api'
  import { formatDateTime, formatSize, mediaTitle } from '../format'
  import Chip from './Chip.svelte'
  import PosterGlow from './PosterGlow.svelte'
  import SizeChange from './SizeChange.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import type { DashboardState } from '../dashboard-state'
  import { encoderQualityControl, isJobSuspended, jobLocation, jobPercent, jobStageTone, qualityChipLabel, verificationPhase } from '../job-presentation'
  import { counts } from '../stores/counts.svelte'
  import PlaybackHolds from './PlaybackHolds.svelte'
  import PanelHeader from './PanelHeader.svelte'

  let { jobs, queue = null, state: queueState, lastResult = null }: {
    jobs: Job[]
    queue?: QueueStatus | null
    state: DashboardState | null
    /** The most recent finished optimisation, shown while nothing runs so the panel is never blank. */
    lastResult?: OptimisationResult | null
  } = $props()

  let lastTitle = $derived(lastResult ? mediaTitle(lastResult.relativePath) : null)

  // The stage a job reports is not always the stage its status names: a job selecting a
  // per-title quality is measuring candidates, not probing the source, and saying "probing"
  // sent people looking for a disk problem that was not there.
  function stageLabel(job: Job): string {
    if (isJobSuspended(job, queue)) return i18n.m.queue.now_paused
    if (job.finalizing) return i18n.m.queue.phase_finalizing
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
      : location === 'transfer' ? i18n.m.queue.location_transfer
      // Back from a worker and waiting here: say whose output it is, or it reads as local work.
      : job.workerName && !job.remoteStage && verificationPhase(job) === 'waiting' ? t(i18n.m.queue.now_returned, { worker: job.workerName })
      : i18n.m.dashboard.this_server
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

  // The server sends either a library mode as its enum name or the encoder's own rate control.
  // Read the first out in words rather than PascalCase, and the second as the code it is.
  function qualityMode(mode: string | null): string | null {
    if (!mode) return null
    const modes = i18n.m.dashboard.quality_mode as Record<string, string>
    return modes[mode] ?? encoderQualityControl(mode) ?? mode
  }

  const STAGE_TONE = { live: 'text-accent', paused: 'text-warn', failed: 'text-bad', waiting: 'text-ink-2' } as const

  // A dashboard answers a question at a glance, so the list is capped and says what it is
  // hiding. Real servers routinely carry a dozen or more outstanding jobs — thirteen the day
  // this was written — which would push everything below it off the screen.
  const MAX_ROWS = 6
  let shown = $derived(jobs.slice(0, MAX_ROWS))
  let hidden = $derived(Math.max(0, jobs.length - MAX_ROWS))
</script>

<!-- Columns follow the card's own width, not the window's: half a laptop screen is too narrow
     for three columns and cut every title to its first letter. -->
<section class="@container card h-full" aria-labelledby="in-flight-heading">
  <PanelHeader as="h2" id="in-flight-heading" title={i18n.m.dashboard.in_flight}>
    {#snippet trailing()}
      <span class="text-xs tabular-nums text-ink-3">
        {t(i18n.m.dashboard.in_flight_counts, {
          running: jobs.length.toLocaleString(),
          queued: (queueState?.queued ?? 0).toLocaleString(),
        })}
      </span>
    {/snippet}
  </PanelHeader>

  {#if jobs.length === 0}
    <!-- An empty list is not an error, and it is not the same as an idle server. The status bar
         has already said which; this repeats the consequence where a reader is looking for work,
         rather than leaving a blank panel. -->
    {@const holds = queueState?.kind === 'blocked' ? counts.queue?.playbackHolds ?? [] : []}
    {#if holds.length > 0}
      <!-- Playback is the reason nothing runs, so show what is playing rather than a sentence about it. -->
      <div class="px-4 pt-5" class:pb-5={!lastResult}>
        <p class="text-sm font-semibold text-ink">{i18n.m.queue.playback_waiting_title}</p>
        <p class="mt-0.5 text-xs text-ink-3">{i18n.m.queue.playback_waiting_hint}</p>
        <PlaybackHolds {holds} />
      </div>
    {:else}
    <p class="px-4 pt-5 text-sm text-ink-3" class:pb-5={!lastResult}>
      {#if queueState?.kind === 'idle'}
        {i18n.m.dashboard.in_flight_idle}
      {:else if queueState?.detail}
        {queueState.detail}
      {:else}
        {i18n.m.dashboard.in_flight_none}
      {/if}
    </p>
    {/if}
    {#if lastResult && lastTitle}
      <!-- While nothing runs, the last finished title stands in for the work, so the panel
           answers "what has it been doing?" instead of sitting empty. -->
      <section class="last-result" aria-label={i18n.m.dashboard.last_finished}>
        <PosterGlow mediaFileId={lastResult.mediaFileId} />
        <Thumbnail mediaFileId={lastResult.mediaFileId} size="lg" />
        <span class="last-result-text">
          <span class="label mb-0">{i18n.m.dashboard.last_finished}</span>
          <strong class="truncate text-title font-semibold text-ink">{lastTitle.primary ?? i18n.m.dashboard.unnamed_file}</strong>
          {#if lastTitle.episode}<span class="text-sm text-ink-2">{[lastTitle.episode, lastTitle.secondary].filter(Boolean).join(' · ')}</span>{/if}
          <span class="mt-2 text-sm tabular-nums text-ink-2">{formatSize(lastResult.sourceSizeBytes)} → {formatSize(lastResult.outputSizeBytes)} <SizeChange sourceSizeBytes={lastResult.sourceSizeBytes} outputSizeBytes={lastResult.outputSizeBytes} class="font-semibold" /></span>
          <span class="text-xs text-ink-3">{[lastResult.libraryName, lastResult.workerName ?? i18n.m.dashboard.this_server, formatDateTime(lastResult.finishedAt, new Date(), i18n.locale)].filter(Boolean).join(' · ')}</span>
        </span>
      </section>
    {/if}
  {:else}
    <ul class="m-0 list-none divide-y divide-line-soft p-0">
      {#each shown as job (job.id)}
        {@const title = mediaTitle(job.relativePath)}
        {@const tone = jobStageTone(job, queue)}
        {@const pct = jobPercent(job)}
        {@const modeLabel = qualityMode(job.videoQualityMode)}
        <li class="grid gap-3 px-4 py-3 @2xl:grid-cols-[1fr_200px_140px] @2xl:items-center">
          <div class="min-w-0">
            <div class="flex min-w-0 items-center gap-3">
              <Thumbnail mediaFileId={job.mediaFileId} size="sm" />
              <button
                type="button"
                class="block min-w-0 flex-1 truncate rounded-sm text-left text-sm font-medium text-ink hover:underline focus-ring"
                onclick={() => router.go('/queue')}
                title={job.relativePath ?? undefined}
              >{title.primary ?? fileName(job.relativePath)}{#if title.episode}{' '}<span class="font-normal text-ink-3">{[title.episode, title.secondary].filter(Boolean).join(' · ')}</span>{/if}</button>
            </div>
            <div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-ink-3">
              {#if job.videoEncoder}<Chip mono>{job.videoEncoder}</Chip>{/if}
              {#if job.effectiveVideoQuality != null}<Chip class="tabular-nums">{qualityChipLabel(job.videoQualityMode, job.effectiveVideoQuality)}</Chip>{/if}
              <span class="min-w-0 [overflow-wrap:anywhere]">{locationLabel(job)}</span>
            </div>
          </div>

          <div>
            <div class="mb-1.5 flex items-center gap-1.5 text-micro font-semibold {STAGE_TONE[tone]}" data-stage-tone={tone}>
              {#if tone === 'live'}<span class="h-1.5 w-1.5 flex-none animate-pulse rounded-full bg-accent" aria-hidden="true"></span>
              {:else if tone === 'paused'}<span class="h-1.5 w-1.5 flex-none rounded-full bg-warn" aria-hidden="true"></span>{/if}
              {stageLabel(job)}
            </div>
            <div
              class="progress-track"
              role="progressbar"
              aria-label={i18n.m.queue.col_progress}
              aria-valuemin="0"
              aria-valuemax="100"
              aria-valuenow={pct ?? undefined}
              aria-valuetext={tone === 'paused' ? i18n.m.queue.now_paused : pct === null ? stageLabel(job) : undefined}
            >
              {#if pct !== null && pct > 0}
                <div class="progress-fill {tone === 'paused' ? 'bg-warn' : verifying(job) ? 'progress-fill-verifying' : ''}" style="width: {pct}%"></div>
              {:else if tone === 'live'}
                <!-- A running stage with no percentage of its own gets the indeterminate sweep
                     rather than a bar frozen at zero, which reads as stalled. Waiting and paused
                     work stays still: motion is kept for what is actually moving. -->
                <div class="progress-indeterminate"></div>
              {/if}
            </div>
            {#if (pct !== null && pct > 0) || tone === 'live'}
              <div class="mt-1.5 text-xs tabular-nums text-ink-3">
                {pct !== null && pct > 0 ? `${pct}%` : i18n.m.dashboard.no_percentage}
              </div>
            {/if}
          </div>

          {#if modeLabel || job.qualityRetryCount > 0}
            <div class="text-xs tabular-nums text-ink-3 @2xl:text-right">
              {#if modeLabel}
                <!-- A value is never left to explain itself: on a phone this column stacks under the
                     progress bar, where a bare "ICQ" read as a stray word. -->
                <dl class="m-0 flex items-baseline gap-1.5 @2xl:block">
                  <dt class="text-micro text-ink-3">{i18n.m.dashboard.in_flight_quality_mode}</dt>
                  <dd class="m-0 text-ink-2">{modeLabel}</dd>
                </dl>
              {/if}
              {#if job.qualityRetryCount > 0}
                <div class="text-warn">{t(i18n.m.dashboard.retry_count, { count: job.qualityRetryCount.toLocaleString() })}</div>
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
    {#if hidden > 0}
      <button
        type="button"
        class="hairline-t w-full px-4 py-2.5 text-left text-xs text-ink-3 transition-colors hover:text-accent focus-ring-inset"
        onclick={() => router.go('/queue')}
      >{t(i18n.m.dashboard.in_flight_more, { count: hidden.toLocaleString() })}</button>
    {/if}
  {/if}
</section>

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
</style>
