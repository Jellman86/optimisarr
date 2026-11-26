<script lang="ts">
  import {
    api,
    type DailySaving,
    type FailureGroup,
    type OptimisationResult,
    type Health,
    type Job,
    type QueueStatus,
    type Stats,
    type ToolCheck,
    type Worker,
  } from '../api'
  import { dashboardStateFor } from '../dashboard-state'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { activity } from '../stores/activity.svelte'
  import Banner from '../components/Banner.svelte'
  import PageHeader from '../components/PageHeader.svelte'
  import FleetPanel from '../components/FleetPanel.svelte'
  import InFlightPanel from '../components/InFlightPanel.svelte'
  import NeedsYouPanel from '../components/NeedsYouPanel.svelte'
  import RecentResultsPanel from '../components/RecentResultsPanel.svelte'
  import SavedPerDayPanel from '../components/SavedPerDayPanel.svelte'
  import SavingsPanel from '../components/SavingsPanel.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let stats = $state<Stats | null>(null)
  let queue = $state<QueueStatus | null>(null)
  let jobs = $state<Job[]>([])
  let failures = $state<FailureGroup[]>([])
  let workers = $state<Worker[]>([])
  let workersAvailable = $state(false)
  let results = $state<OptimisationResult[] | null>(null)
  let dailySavings = $state<DailySaving[] | null>(null)
  let error = $state<string | null>(null)
  let loadRequest = 0
  let refreshing = false
  let refreshPending = false

  // Reset flow for the lifetime savings tally: a two-step inline confirm so the headline
  // figure is never wiped on a stray click.
  let confirmingReset = $state(false)
  let resetting = $state(false)

  $effect(() => {
    let disposed = false
    // Job events are not throttled server-side. Fold a burst into one follow-up read so the
    // page never stacks overlapping nine-request loads.
    async function refresh() {
      if (refreshing) { refreshPending = true; return }
      refreshing = true
      try {
        do { refreshPending = false; await load() } while (refreshPending && !disposed)
      } finally { refreshing = false }
    }
    void refresh()
    const unsubscribe = activity.subscribe({ onChanged: () => void refresh() })
    // Fleet and storage figures can also change outside job events.
    const timer = setInterval(() => { if (!document.hidden) void refresh() }, 15000)
    return () => {
      disposed = true
      loadRequest++
      unsubscribe()
      clearInterval(timer)
    }
  })

  async function load() {
    const request = ++loadRequest
    try {
      // These five answer the page. A failure in any of them is a real error worth a banner,
      // because the status bar would otherwise sit there stating a stale state confidently.
      const [healthResult, toolsResult, statsResult, queueResult, jobsResult] = await Promise.all([
        api.health(),
        api.tools(),
        api.stats(),
        api.queueStatus(),
        api.liveJobs(),
      ])
      if (request !== loadRequest) return
      health = healthResult
      tools = toolsResult
      stats = statsResult
      queue = queueResult
      jobs = jobsResult
      error = null

      // These only enrich: a missing failure breakdown costs a subtitle, an unreachable worker
      // list costs the sidecar rows, and the history panels keep their last answer. None is
      // worth losing the page over.
      const [failuresResult, resultsResult, dailyResult] = await Promise.allSettled([
        api.jobFailures(),
        api.results(12),
        api.dailySavings(30),
      ])
      if (request !== loadRequest) return
      failures = failuresResult.status === 'fulfilled' ? failuresResult.value : []
      if (resultsResult.status === 'fulfilled') results = resultsResult.value
      if (dailyResult.status === 'fulfilled') dailySavings = dailyResult.value
      await loadFleet(request)
    } catch (err) {
      if (request === loadRequest) error = err instanceof Error ? err.message : i18n.m.dashboard.error_load
    }
  }

  // Saved choices and explicit deployment overrides can disable workers. Ask settings before
  // listing the fleet so a disabled service does not turn a valid local dashboard into an error.
  async function loadFleet(request: number) {
    try {
      const settings = await api.settings()
      if (request !== loadRequest) return
      const available = settings.remoteWorkersAvailable && settings.remoteWorkersEnabled
      const fleet = available ? await api.workers() : []
      if (request !== loadRequest) return
      workersAvailable = available
      workers = fleet
    } catch {
      if (request !== loadRequest) return
      workersAvailable = false
      workers = []
    }
  }

  async function resetSavings() {
    resetting = true
    error = null
    try {
      stats = await api.clearStats()
      confirmingReset = false
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.dashboard.error_reset
    } finally {
      resetting = false
    }
  }

  let toolsReady = $derived(tools.length > 0 && tools.filter((tool) => tool.required).every((tool) => tool.available))
  let healthy = $derived(health?.status === 'healthy' && toolsReady)
  let healthDetail = $derived(
    t(i18n.m.dashboard.health_detail, {
      status: health?.status ?? i18n.m.dashboard.unknown_status,
      tools: toolsReady ? i18n.m.dashboard.tools_ready : i18n.m.dashboard.tools_not_detected,
    }),
  )

  // The server decides what counts as in progress and this page does not re-derive it. An
  // earlier attempt to infer it from startedAt/finishedAt disagreed with the server on real
  // data — it found one job where the server counted twenty-four.
  let runningLocally = $derived(jobs.filter((job) => !job.workerName).length)

  // A decision waiting on a person outranks every figure on the page, so it leads when there is
  // one. When there is none, an empty "nothing is waiting" card is not the first thing to read.
  let needsAttention = $derived(
    (stats?.readyToReplace ?? 0) > 0 || (stats?.inQuarantine ?? 0) > 0 || (stats?.failed ?? 0) > 0,
  )

  let queueState = $derived(
    queue
      ? dashboardStateFor(queue, stats?.queued ?? 0)
      : null,
  )
</script>

<PageHeader title={i18n.m.nav.dashboard} subtitle={i18n.m.dashboard.subtitle} />

{#if error}
  <Banner kind="error" class="mb-6">{error}</Banner>
{/if}

<!-- One grid that reorganises by the width it is given, not by the window: a phone stacks, a
     laptop pairs panels, and a large display reads like a control room. "Needs you" moves in the
     document as well as on screen, so keyboard and screen-reader order follow what is shown first. -->
{#snippet needsYou()}
  <div class="area-needs"><NeedsYouPanel {stats} {failures} /></div>
{/snippet}

<div class="@container">
  <div class="dashboard-grid grid grid-cols-1 gap-4 *:min-w-0" class:needs-first={needsAttention}>
    {#if needsAttention}{@render needsYou()}{/if}
    <div class="area-savings">
      <SavingsPanel {stats} {healthy} {healthDetail} bind:confirmingReset {resetting} onreset={resetSavings} />
    </div>
    <div class="area-inflight"><InFlightPanel {jobs} {queue} state={queueState} lastResult={results?.[0] ?? null} /></div>
    <div class="area-fleet"><FleetPanel {workers} {workersAvailable} {runningLocally} localFreeBytes={queue?.freeDiskBytes ?? null} /></div>
    {#if !needsAttention}{@render needsYou()}{/if}
    <div class="area-chart"><SavedPerDayPanel days={dailySavings} /></div>
    <div class="area-recent"><RecentResultsPanel {results} /></div>
  </div>
</div>

<style>
  /* A phone stacks the panels in markup order; each wider layout reads row by row in that order. */
  @container (min-width: 48rem) {
    .area-savings { grid-area: s; }
    .area-inflight { grid-area: i; }
    .area-needs { grid-area: n; }
    .area-chart { grid-area: c; }
    .area-fleet { grid-area: f; }
    .area-recent { grid-area: r; }
    .dashboard-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      grid-template-areas: 's i' 'f n' 'c c' 'r r';
    }
    .dashboard-grid.needs-first { grid-template-areas: 'n n' 's i' 'f f' 'c c' 'r r'; }
  }

  @container (min-width: 100rem) {
    .dashboard-grid {
      gap: 1.25rem;
      grid-template-columns: repeat(12, minmax(0, 1fr));
      grid-template-areas:
        's s s s i i i i i i i i'
        'f f f f c c c c c c c c'
        'n n n n c c c c c c c c'
        'r r r r r r r r r r r r';
    }
    .dashboard-grid.needs-first {
      grid-template-areas:
        'n n n n n n n n n n n n'
        's s s s i i i i i i i i'
        'f f f f c c c c c c c c'
        'r r r r r r r r r r r r';
    }
  }
</style>
