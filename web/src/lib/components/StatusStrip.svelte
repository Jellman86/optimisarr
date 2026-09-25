<script lang="ts">
  import { dashboardState, type DashboardState } from '../dashboard-state'
  import { formatSize } from '../format'
  import { localWorkloadCapacity } from '../job-presentation'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { counts } from '../stores/counts.svelte'
  import Icon from './Icon.svelte'

  // The queue's state on every page, so the answer to "is it working?" is never a click away.
  let queue = $derived(counts.queue)
  let state = $derived<DashboardState | null>(
    queue
      ? dashboardState({
          canStart: queue.canStart,
          blockedReason: queue.blockedReason,
          manuallyPaused: queue.manuallyPaused,
          manualPauseMode: queue.manualPauseMode,
          waitingReason: queue.waitingReason,
          runningJobs: queue.runningJobs,
          queued: counts.stats?.queued ?? 0,
        })
      : null,
  )

  // Each state names itself in words as well as colour, so the strip never depends on hue alone.
  const LABEL: Record<DashboardState['kind'], () => string> = {
    encoding: () => i18n.m.dashboard.state_encoding,
    paused: () => i18n.m.dashboard.state_paused,
    blocked: () => i18n.m.dashboard.state_blocked,
    waiting: () => i18n.m.dashboard.state_waiting,
    idle: () => i18n.m.dashboard.state_idle,
    unexplained: () => i18n.m.dashboard.state_unexplained,
  }

  let tone = $derived(state?.severity === 'live' ? 'text-accent' : state?.severity === 'attention' ? 'text-bad' : 'text-ink')
  let capacity = $derived(queue ? localWorkloadCapacity(queue) : null)
  let slots = $derived(capacity ? `${queue?.runningJobs ?? 0} / ${capacity}` : `${queue?.runningJobs ?? 0}`)
</script>

<!-- A labelled region rather than a live one: it re-reads every fifteen seconds, and a region
     that announces each poll would talk over whatever the reader is doing on the page. -->
<section class="status-strip card" aria-label={i18n.m.dashboard.state}>
  <div class="status-strip-state">
    {#if state?.severity === 'live'}
      <span class="h-2 w-2 flex-none animate-pulse rounded-full bg-accent" aria-hidden="true"></span>
    {:else if state?.severity === 'attention'}
      <span class="h-2 w-2 flex-none rounded-full bg-bad" aria-hidden="true"></span>
    {:else}
      <span class="h-2 w-2 flex-none rounded-full bg-ink-5" aria-hidden="true"></span>
    {/if}
    <span class="label mb-0">{i18n.m.dashboard.state}</span>
    <span class="font-mono text-sm font-medium {tone}">{state ? LABEL[state.kind]() : '—'}</span>
    {#if state?.detail}
      <span class="status-strip-reason" title={state.detail}>{state.detail}</span>
    {/if}
  </div>

  <div class="status-strip-fact">
    <span class="label mb-0">{i18n.m.dashboard.slots}</span>
    <span class="font-mono text-sm font-medium tabular-nums text-ink">{slots}</span>
  </div>

  <div class="status-strip-fact">
    <span class="label mb-0">{i18n.m.nav.queue}</span>
    <span class="font-mono text-sm font-medium tabular-nums text-ink">{(counts.stats?.queued ?? 0).toLocaleString()}</span>
  </div>

  {#if queue?.freeDiskBytes != null}
    <div class="status-strip-fact status-strip-optional">
      <span class="label mb-0">{t(i18n.m.dashboard.free_on, { path: queue.workRoot || '/work' })}</span>
      <span class="font-mono text-sm font-medium tabular-nums text-ink">{formatSize(queue.freeDiskBytes)}</span>
    </div>
  {/if}

  {#if queue}
    <div class="status-strip-action">
      {#if counts.pauseError}<span class="text-xs text-bad" role="alert">{counts.pauseError}</span>{/if}
      <button
        class="btn px-3 py-1.5 text-xs"
        class:btn-primary={queue.manuallyPaused}
        onclick={() => void counts.togglePause()}
        disabled={counts.pauseBusy}
        aria-busy={counts.pauseBusy}
        title={queue.manuallyPaused ? i18n.m.queue.resume_queue_title : i18n.m.queue.pause_queue_title}
      >
        <Icon name={queue.manuallyPaused ? 'play' : 'pause'} class="h-3.5 w-3.5" />
        {counts.pauseBusy ? i18n.m.common.loading_short : queue.manuallyPaused ? i18n.m.queue.resume_queue : i18n.m.queue.pause_queue}
      </button>
    </div>
  {/if}
</section>

<style>
  .status-strip {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.25rem 1.5rem;
    padding: 0.55rem 0.6rem 0.55rem 1rem;
  }
  .status-strip-state {
    display: flex;
    min-width: 0;
    flex: 1 1 16rem;
    align-items: center;
    gap: 0.6rem;
  }
  .status-strip-reason {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 0.8125rem;
    color: var(--ink-2);
  }
  .status-strip-fact {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }
  .status-strip-action {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-left: auto;
  }
  @media (max-width: 40rem) {
    .status-strip-optional { display: none; }
  }
</style>
