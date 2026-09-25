<script lang="ts">
  import type { Job, QueueStatus } from '../api'
  import type { JobProgress as Telemetry } from '../realtime'
  import { isJobSuspended, jobLocation, verificationPhase } from '../job-presentation'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import JobProgress from './JobProgress.svelte'
  import JobStages from './JobStages.svelte'
  import Icon from './Icon.svelte'
  let { job, queue, telemetry, compact = false, selected = false, onopen }: {
    job: Job; queue: QueueStatus | null; telemetry?: Telemetry; compact?: boolean; selected?: boolean; onopen: (event: MouseEvent) => void
  } = $props()
  let title = $derived(job.relativePath?.split(/[\\/]/).pop()?.replace(/\.[^.]+$/, '').replace(/[._]+/g, ' ') || t(i18n.m.queue.job_fallback, { id: job.id }))
  let paused = $derived(isJobSuspended(job, queue))
  let location = $derived(jobLocation(job))
  let locationLabel = $derived(location === 'worker' ? job.workerName ?? '?' : location === 'transfer' ? i18n.m.queue.location_transfer : i18n.m.dashboard.this_server)
  let phase = $derived(verificationPhase(job))
  let status = $derived(paused ? i18n.m.queue.now_paused : job.status === 'Leased'
    ? (job.remoteStage === 'Encoding' ? t(i18n.m.queue.now_remote, { worker: job.workerName ?? '?' }) : job.remoteStage === 'Delivering' ? t(i18n.m.queue.remote_returning, { worker: job.workerName ?? '?' }) : t(i18n.m.queue.remote_job, { worker: job.workerName ?? '?' }))
    : job.finalizing ? i18n.m.queue.phase_finalizing
    : phase === 'waiting' ? job.workerName ? t(i18n.m.queue.now_returned, { worker: job.workerName }) : i18n.m.queue.status_awaitingverification
    : phase === 'evidence' ? i18n.m.queue.phase_evidence
    : job.status === 'Transcoding' ? i18n.m.queue.now_encoding : job.status === 'Probing' ? i18n.m.queue.now_probing : i18n.m.queue.now_verifying)
</script>

<article class="working-job card" class:compact class:selected aria-label={title}>
  <button class="working-poster focus-ring" onclick={onopen} aria-label={title} aria-haspopup="dialog"><Thumbnail mediaFileId={job.mediaFileId} size={compact ? 'md' : 'poster'} /></button>
  <div class="working-content">
    <div class="working-top"><span class="working-status" class:paused>{status}</span><span class="working-location" title={locationLabel}>{locationLabel}</span>{#if job.videoEncoder}<span class="working-encoder">{job.videoEncoder}</span>{/if}</div>
    {#if job.retryReason === 'SoftwareDecode'}<p class="working-retry">{i18n.m.queue.retry_software_title} · {t(i18n.m.queue.attempt_number, { number: job.executionAttempt ?? 1 })}</p>{/if}
    <h3><button class="working-title focus-ring" onclick={onopen} aria-haspopup="dialog">{title}</button></h3>
    {#if job.enqueueReason}<p class="working-reason">{job.enqueueReason}</p>{/if}
    <JobProgress {job} {queue} {telemetry} {compact} />
    {#if !compact}<JobStages {job} />{/if}
  </div>
  <div class="working-footer"><button id={`working-job-${job.id}`} class="btn btn-ghost" aria-haspopup="dialog" aria-controls={selected ? 'queue-job-dialog' : undefined} onclick={onopen}>{i18n.m.queue.view_job}<Icon name="arrow-right" /></button></div>
</article>

<style>
  .working-job { display: grid; grid-template-columns: 8rem minmax(0, 1fr); gap: 1.25rem 1.5rem; padding: 1.5rem; }
  .working-job.selected { box-shadow: var(--lift-1), inset 0 0 0 1px var(--accent); }.working-poster { align-self: start; border-radius: .5rem; overflow: hidden; box-shadow: var(--lift-2); }.working-content { min-width: 0; }
  .working-top { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem; margin-bottom: .625rem; }.working-status { font-size: .6875rem; color: var(--accent); margin-right: auto; }.working-status.paused { color: var(--warn); }.working-location { font-size: .6875rem; color: var(--ink-2); background: var(--sunken); border: 1px solid var(--divide-soft); border-radius: 999px; padding: .25rem .55rem; max-width: 12rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }.working-encoder { font: .6875rem ui-monospace, monospace; background: var(--sunken); padding: .3rem .5rem; border-radius: .375rem; color: var(--ink-3); overflow-wrap: anywhere; }
  .working-title { text-align: left; border-radius: .25rem; }.working-title:hover { color: var(--accent); }
  .working-retry { margin: -.25rem 0 .625rem; color: var(--warn); font-size: .6875rem; }
  h3 { color: var(--ink); font-size: 1.375rem; line-height: 1.3; letter-spacing: -.025em; font-weight: 600; overflow-wrap: anywhere; margin-bottom: 1rem; }.working-reason { font-size: .75rem; color: var(--ink-3); margin: -.5rem 0 1rem; }
  .working-footer { grid-column: 1/-1; display: flex; justify-content: flex-end; align-items: center; gap: 1rem; padding-top: .75rem; border-top: 1px solid var(--divide-soft); font-size: .75rem; color: var(--ink-3); }.working-footer .btn { color: var(--accent); font-size: .75rem; min-height: 2.5rem; }
  .compact { grid-template-columns: 2.75rem minmax(0, 1fr) auto; padding: 1rem 1.25rem; gap: .75rem 1rem; }.compact h3 { font-size: .9375rem; margin-bottom: .75rem; }.compact .working-footer { grid-column: 3; grid-row: 1; align-self: center; border: 0; padding: 0; }.compact .working-top { margin-bottom: .375rem; }
  @media(max-width: 639px) { .working-job { padding: 1rem; grid-template-columns: 3.5rem minmax(0, 1fr); gap: 1rem; }.working-poster :global([data-thumbnail]) { width: 3.5rem; height: 5.25rem; } h3 { font-size: 1.125rem; }.working-footer { gap: .5rem; }.working-top { align-items: flex-start; }.compact { grid-template-columns: 2.75rem minmax(0, 1fr); }.compact .working-poster :global([data-thumbnail]) { width: 2.75rem; height: 4rem; }.compact .working-footer { grid-column: 2; grid-row: auto; justify-content: flex-end; } }
</style>
