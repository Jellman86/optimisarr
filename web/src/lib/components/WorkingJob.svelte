<script lang="ts">
  import type { Job, QueueStatus } from '../api'
  import type { JobProgress as Telemetry } from '../realtime'
  import { isJobSuspended, jobLocation, verificationPhase } from '../job-presentation'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import JobProgress from './JobProgress.svelte'
  import JobStages from './JobStages.svelte'
  import Chip from './Chip.svelte'
  import Icon from './Icon.svelte'
  let { job, queue, telemetry, compact = false, selected = false, onopen }: {
    job: Job; queue: QueueStatus | null; telemetry?: Telemetry; compact?: boolean; selected?: boolean; onopen: (event: MouseEvent) => void
  } = $props()
  let title = $derived(job.relativePath?.split(/[\\/]/).pop()?.replace(/\.[^.]+$/, '').replace(/[._]+/g, ' ').trim() || t(i18n.m.queue.job_fallback, { id: job.id }))
  let paused = $derived(isJobSuspended(job, queue))
  let location = $derived(jobLocation(job))
  let locationLabel = $derived(location === 'worker' ? job.workerName ?? '?' : location === 'transfer' ? i18n.m.queue.location_transfer : i18n.m.dashboard.this_server)
  let phase = $derived(verificationPhase(job))
  let isLive = $derived(!paused && phase !== 'waiting')
  let status = $derived(paused ? i18n.m.queue.now_paused : job.status === 'Leased'
    ? (job.remoteStage === 'Encoding' ? t(i18n.m.queue.now_remote, { worker: job.workerName ?? '?' }) : job.remoteStage === 'Delivering' ? t(i18n.m.queue.remote_returning, { worker: job.workerName ?? '?' }) : t(i18n.m.queue.remote_job, { worker: job.workerName ?? '?' }))
    : job.finalizing ? i18n.m.queue.phase_finalizing
    : phase === 'waiting' ? job.workerName ? t(i18n.m.queue.now_returned, { worker: job.workerName }) : i18n.m.queue.status_awaitingverification
    : phase === 'evidence' ? i18n.m.queue.phase_evidence
    : job.status === 'Transcoding' ? i18n.m.queue.now_encoding : job.status === 'Probing' ? i18n.m.queue.now_probing : i18n.m.queue.now_verifying)
</script>

<!-- Two rows beside a small poster: what is happening and to which file, then how far along it
     is. The list below is the page, so a running job takes a strip, not a screen. -->
<article class="working-job card grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-3.5 gap-y-2 px-4 py-3" class:selected aria-label={title}>
  <button type="button" class="row-span-2 rounded-[5px] focus-ring max-sm:self-start" onclick={onopen} aria-label={title} aria-haspopup="dialog"><Thumbnail mediaFileId={job.mediaFileId} size={compact ? 'sm' : 'md'} /></button>
  <div class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
    <span class={['inline-flex min-w-0 max-w-full items-center gap-1.5 text-caption font-semibold [overflow-wrap:anywhere]', paused ? 'text-warn' : isLive ? 'text-accent' : 'text-ink-2']}>
      <span class={['h-2 w-2 flex-none rounded-full', paused ? 'bg-warn' : isLive ? 'animate-pulse bg-accent' : 'bg-ink-4']} aria-hidden="true"></span>
      <span class="min-w-0">{status}</span>
    </span>
    <h3 class={['min-w-0 flex-[1_1_12rem] font-semibold text-ink [overflow-wrap:anywhere]', compact ? 'text-label' : 'text-title']}><button type="button" class="line-clamp-2 rounded text-left focus-ring hover:text-accent" onclick={onopen} aria-haspopup="dialog">{title}</button></h3>
    <span class="flex min-w-0 flex-wrap items-center gap-1.5"><Chip class="max-w-48" truncate title={locationLabel}>{locationLabel}</Chip>{#if job.videoEncoder}<Chip mono class="break-all">{job.videoEncoder}</Chip>{/if}</span>
    <button type="button" id={`working-job-${job.id}`} class="btn btn-sm btn-ghost ml-auto text-accent" aria-haspopup="dialog" aria-controls={selected ? 'queue-job-dialog' : undefined} onclick={onopen}>{i18n.m.queue.view_job}<Icon name="arrow-right" /></button>
  </div>
  <div class={['grid min-w-0 grid-cols-1 items-center gap-x-6 gap-y-1.5', !compact && 'lg:grid-cols-[minmax(0,1fr)_minmax(0,18rem)] xl:grid-cols-[minmax(0,1fr)_minmax(0,20rem)]']}>
    {#if job.retryReason === 'SoftwareDecode'}<p class="col-span-full text-caption text-warn">{i18n.m.queue.retry_software_title} · {t(i18n.m.queue.attempt_number, { number: job.executionAttempt ?? 1 })}</p>
    {:else if job.retryReason === 'AvoidIntelQsv'}<p class="col-span-full text-caption text-warn">{i18n.m.queue.retry_qsv_title} · {t(i18n.m.queue.attempt_number, { number: job.executionAttempt ?? 1 })}</p>{/if}
    {#if job.enqueueReason}<p class="col-span-full text-caption text-ink-3 [overflow-wrap:anywhere]">{job.enqueueReason}</p>{/if}
    <JobProgress {job} {queue} {telemetry} {compact} inline />
    {#if !compact}<JobStages {job} {queue} class="" />{/if}
  </div>
</article>

<style>
  .working-job.selected { background: linear-gradient(var(--accent-soft), var(--accent-soft)), var(--panel); }
</style>
