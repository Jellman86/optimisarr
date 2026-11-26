<script lang="ts">
  import { dashboardStateFor, type DashboardState } from '../dashboard-state'
  import { formatSize } from '../format'
  import { localWorkloadCapacity } from '../job-presentation'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { playbackLine, playbackPhrases, playbackSummary } from '../playback'
  import { counts } from '../stores/counts.svelte'
  import Icon from './Icon.svelte'
  import InfoTip from './InfoTip.svelte'
  import Thumbnail from './Thumbnail.svelte'

  // The queue's state on every page, so the answer to "is it working?" is never a click away.
  let queue = $derived(counts.queue)
  let workers = $derived(queue?.workloadLanes?.find((lane) => lane.lane === 'Workers') ?? null)
  let current = $derived<DashboardState | null>(
    queue
      ? dashboardStateFor(queue, counts.stats?.queued ?? 0)
      : null,
  )

  // Each state names itself in words as well as colour, so the strip never depends on hue alone.
  const LABEL: Record<DashboardState['kind'], () => string> = {
    encoding: () => i18n.m.dashboard.state_encoding,
    workers: () => i18n.m.dashboard.state_workers,
    paused: () => i18n.m.dashboard.state_paused,
    blocked: () => i18n.m.dashboard.state_blocked,
    waiting: () => i18n.m.dashboard.state_waiting,
    idle: () => i18n.m.dashboard.state_idle,
    unexplained: () => i18n.m.dashboard.state_unexplained,
  }

  let tone = $derived(current?.severity === 'live' ? 'text-accent' : current?.severity === 'attention' ? 'text-bad' : 'text-ink')
  let capacity = $derived(queue ? localWorkloadCapacity(queue) : null)
  let slots = $derived(capacity ? `${queue?.runningJobs ?? 0} / ${capacity}` : `${queue?.runningJobs ?? 0}`)
  // Only a server with workers shows their slots; one without them has nothing to count.
  let workerSlots = $derived(workers && (workers.capacity > 0 || workers.active > 0) ? `${workers.active} / ${workers.capacity}` : null)
  // While workers encode, a hold on this server is the explanation rather than the state, so say
  // whose hold it is. A state with no reason of its own gets a plain sentence instead of nothing.
  // Held by playback: name what is playing and who, rather than only how many streams.
  let phrases = $derived(playbackPhrases(i18n.m))
  let holds = $derived(current?.kind === 'blocked' ? queue?.playbackHolds ?? [] : [])
  let detail = $derived(
    !current ? null
    : current.kind === 'workers' && current.localPaused ? i18n.m.dashboard.state_workers_local_paused
    : current.kind === 'workers' && current.detail ? t(i18n.m.dashboard.state_workers_local_held, { reason: current.detail })
    : current.kind === 'unexplained' ? i18n.m.dashboard.state_unexplained_detail
    : holds.length > 0 ? t(i18n.m.queue.playback_status, { count: holds.length, summary: playbackSummary(holds, phrases) ?? '' })
    : current.detail,
  )
  // The reason wraps rather than clips, so it is complete on screen. Only several playbacks have
  // more to say than fits in a sentence: the tooltip lists each one, and the link opens the
  // queue with the same detail for touch and keyboard.
  let detailTitle = $derived(holds.length > 0 ? holds.map((hold) => `${hold.watcher} · ${playbackLine(hold, phrases)}`).join('\n') : undefined)

  // On a phone the strip keeps to one line, so a changing reason never pushes the page down: the
  // reason is cut short and the figures step aside until the strip is opened. A failed pause opens
  // it, because an alert is never cut short; the operator can still close it again once read.
  let open = $state(false)
  $effect(() => {
    if (counts.pauseError) open = true
  })
</script>

<!-- A labelled region rather than a live one: it re-reads every fifteen seconds, and a region
     that announces each poll would talk over whatever the reader is doing on the page. -->
<section class="status-strip status-strip-collapsible card" class:status-strip-open={open} aria-label={i18n.m.dashboard.state}>
  <!-- One block: the artwork (or the state's dot), then the state over its reason. -->
  <div class="status-strip-state">
    {#if holds[0]?.artworkUrl}
      <Thumbnail src={holds[0].artworkUrl} size="sm" shape={holds[0].kind === 'Track' ? 'square' : 'portrait'} />
    {:else if current?.severity === 'live'}
      <span class="h-2 w-2 flex-none animate-pulse rounded-full bg-accent" aria-hidden="true"></span>
    {:else if current?.severity === 'attention'}
      <span class="h-2 w-2 flex-none rounded-full bg-bad" aria-hidden="true"></span>
    {:else}
      <span class="h-2 w-2 flex-none rounded-full bg-ink-4" aria-hidden="true"></span>
    {/if}
    <div class="status-strip-text">
      <span class="sr-only">{i18n.m.dashboard.state}:</span>
      <span class="status-strip-label text-sm font-semibold {tone}">{current ? LABEL[current.kind]() : '—'}</span>
      {#if detail}
        {#if holds.length > 0}
          <a class="status-strip-reason status-strip-playback text-label focus-ring" href="#/queue" title={detailTitle}>{detail}</a>
        {:else}
          <span class="status-strip-reason text-label">{detail}</span>
        {/if}
      {/if}
    </div>
  </div>

  <!-- Phone only. -->
  <button
    type="button"
    class="status-strip-toggle btn btn-sm btn-ghost btn-icon"
    aria-label={i18n.m.dashboard.state_details}
    aria-expanded={open}
    onclick={() => (open = !open)}
  >
    <Icon name="chevron" class="h-4 w-4" />
  </button>

  <div class="status-strip-fact">
    <span class="label mb-0">{i18n.m.dashboard.slots}</span>
    <span class="text-sm font-semibold tabular-nums text-ink">{slots}</span>
  </div>

  {#if workerSlots}
    <div class="status-strip-fact">
      <span class="label mb-0">{i18n.m.dashboard.worker_slots}</span>
      <span class="text-sm font-semibold tabular-nums text-ink">{workerSlots}</span>
    </div>
  {/if}

  <div class="status-strip-fact">
    <span class="label mb-0">{i18n.m.nav.queue}</span>
    <span class="text-sm font-semibold tabular-nums text-ink">{(counts.stats?.queued ?? 0).toLocaleString()}</span>
  </div>

  {#if queue?.freeDiskBytes != null}
    <div class="status-strip-fact status-strip-optional">
      <span class="label mb-0">{t(i18n.m.dashboard.free_on, { path: queue.workRoot || '/work' })}</span>
      <span class="text-sm font-semibold tabular-nums text-ink">{formatSize(queue.freeDiskBytes)}</span>
    </div>
  {/if}

  {#if queue}
    <div class="status-strip-action">
      {#if counts.pauseError}<span class="status-strip-error text-xs text-bad" role="alert">{counts.pauseError}</span>{/if}
      <!-- What pausing stops and what it lets finish decides whether to press it, so it is a tap
           away on every screen rather than a hover. Before the button, which keeps the strip's edge. -->
      <span class="status-strip-action-help"><InfoTip id="status-strip-pause-effect" text={queue.manuallyPaused ? i18n.m.queue.resume_queue_title : i18n.m.queue.pause_queue_title} /></span>
      <button
        class="btn btn-sm"
        class:btn-primary={queue.manuallyPaused}
        onclick={() => void counts.togglePause()}
        disabled={counts.pauseBusy}
        aria-busy={counts.pauseBusy}
        aria-describedby="status-strip-pause-effect"
      >
        <Icon name={queue.manuallyPaused ? 'play' : 'pause'} class="h-3.5 w-3.5" />
        <span class="status-strip-action-label">{counts.pauseBusy ? i18n.m.common.loading_short : queue.manuallyPaused ? i18n.m.queue.resume_queue : i18n.m.queue.pause_queue}</span>
      </button>
    </div>
  {/if}
</section>
