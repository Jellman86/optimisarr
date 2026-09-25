<script lang="ts">
  import type { Job, QueueStatus } from '../api'
  import type { JobProgress } from '../realtime'
  import { jobPercent, isJobSuspended, verificationPhase } from '../job-presentation'
  import { formatDuration } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  let { job, queue, telemetry, compact = false }: { job: Job; queue: QueueStatus | null; telemetry?: JobProgress; compact?: boolean } = $props()
  let percent = $derived(jobPercent(job))
  let paused = $derived(isJobSuspended(job, queue))
  let encoding = $derived(job.status === 'Transcoding' || (job.status === 'Leased' && job.remoteStage === 'Encoding'))
  let phase = $derived(verificationPhase(job))
  let description = $derived.by(() => {
    if (paused) return i18n.m.queue.now_paused
    if (job.finalizing) return i18n.m.queue.finalizing_detail
    if (job.status === 'Leased') {
      const worker = job.workerName ?? '?'
      switch (job.remoteStage) {
        case 'FetchingSource': return t(i18n.m.queue.remote_sending, { worker })
        case 'Encoding': return t(i18n.m.queue.remote_encoding, { worker })
        case 'Delivering': return t(i18n.m.queue.remote_returning, { worker })
        case 'Measuring': return t(i18n.m.queue.remote_measuring, { worker })
        default: return t(i18n.m.queue.remote_claimed, { worker })
      }
    }
    if (phase === 'waiting') return job.sidecarVerification
      ? t(i18n.m.queue.evidence_waiting_detail, { worker: job.workerName ?? '?' })
      : i18n.m.queue.returned_waiting
    if (job.status === 'Probing') return job.progress > 0 ? i18n.m.queue.selecting_quality : i18n.m.queue.probing_source
    if (phase === 'evidence') return t(i18n.m.queue.evidence_validating_detail, { worker: job.workerName ?? '?' })
    if (phase === 'media') return job.workerName ? t(i18n.m.queue.verifying_returned, { worker: job.workerName }) : i18n.m.queue.verifying_output
    return ''
  })
  function eta(seconds: number) { return seconds < 60 ? t(i18n.m.queue.eta_seconds, { seconds: Math.round(seconds) }) : t(i18n.m.queue.eta_duration, { duration: formatDuration(seconds) }) }
</script>

<div class="job-progress" class:compact class:paused>
  <div class="progress-track" role="progressbar" aria-label={i18n.m.queue.col_progress} aria-valuemin="0" aria-valuemax="100" aria-valuenow={percent ?? undefined} aria-valuetext={paused ? i18n.m.queue.now_paused : percent === null ? description : undefined}>
    {#if percent !== null}<div class="progress-fill" style:width={`${percent}%`}></div>
    {:else}<div class="progress-indeterminate"></div>{/if}
  </div>
  <div class="progress-readout">
    {#if percent !== null}<strong>{percent}%</strong>{/if}
    {#if !paused && encoding && telemetry}
      {#if telemetry.speed != null}<span>{telemetry.speed.toFixed(telemetry.speed < 10 ? 2 : 1)}×</span>{/if}
      {#if !compact && telemetry.fps != null}<span>{telemetry.fps.toFixed(0)} fps</span>{/if}
      {#if telemetry.finishing}<span class="remaining">{i18n.m.queue.finishing}</span>
      {:else if telemetry.etaSeconds != null}<span class="remaining">{eta(telemetry.etaSeconds)}</span>{/if}
    {/if}
  </div>
  {#if description && !(compact && job.status === 'Leased' && job.remoteStage === 'Encoding')}<p>{description}</p>{/if}
</div>

<style>
  .job-progress { min-width: 0; }.progress-track { height: .375rem; }.progress-readout { display: flex; flex-wrap: wrap; align-items: baseline; gap: .5rem 1rem; margin-top: .625rem; font-size: .75rem; color: var(--ink-3); font-variant-numeric: tabular-nums; }
  strong { font-size: 1.125rem; color: var(--ink); font-weight: 600; }.remaining { margin-left: auto; } p { font-size: .75rem; color: var(--ink-3); margin-top: .375rem; overflow-wrap: anywhere; }
  .paused .progress-fill { background: var(--warn); }.paused p { color: var(--warn); }.compact strong { font-size: .8125rem; }.compact .progress-track { height: .25rem; }.compact p { font-size: .6875rem; }
</style>
