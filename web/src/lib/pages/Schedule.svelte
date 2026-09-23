<script lang="ts">
  import { api, type Library, type QueueStatus } from '../api'
  import { localWorkloadCapacity } from '../job-presentation'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'

  let queueStatus = $state<QueueStatus | null>(null)
  let libraries = $state<Library[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let now = $state(new Date())

  $effect(() => {
    void load()
    const timer = setInterval(() => {
      now = new Date()
      void refresh()
    }, 15000)
    return () => clearInterval(timer)
  })

  async function refresh() {
    try {
      const [nextStatus, nextLibraries] = await Promise.all([api.queueStatus(), api.libraries()])
      queueStatus = nextStatus
      libraries = nextLibraries
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.schedule.error_load
    }
  }

  async function load() {
    loading = true
    error = null
    await refresh()
    loading = false
  }

  // Returns true when the current local time falls inside start→end.
  // Handles overnight windows (end < start, e.g. 22:00→06:00).
  // A 00:00→00:00 window means "always on" and is treated as all-day.
  function inWindow(start: string, end: string): boolean {
    const [sh, sm] = start.split(':').map(Number)
    const [eh, em] = end.split(':').map(Number)
    const startMin = sh * 60 + sm
    const endMin = eh * 60 + em
    if (startMin === endMin) return true
    const nowMin = now.getHours() * 60 + now.getMinutes()
    return startMin < endMin
      ? nowMin >= startMin && nowMin < endMin
      : nowMin >= startMin || nowMin < endMin
  }

  function isOvernightWindow(start: string, end: string): boolean {
    const [sh, sm] = start.split(':').map(Number)
    const [eh, em] = end.split(':').map(Number)
    return sh * 60 + sm > eh * 60 + em
  }

  let autoOptimiseLibraries = $derived(libraries.filter((l) => l.autoEnqueueEnabled))
  let dispatchExplanation = $derived(queueStatus?.blockedReason ?? queueStatus?.waitingReason ?? i18n.m.schedule.explain_ready)
</script>

<header class="mb-6">
  <h1 class="page-title">{i18n.m.nav.schedule}</h1>
  <p class="text-sm text-ink-3">
    {i18n.m.schedule.intro_before}<button
      class="text-accent hover:underline"
      onclick={() => router.go('/settings')}
    >{i18n.m.nav.settings}</button>{i18n.m.schedule.intro_between}<button
      class="text-accent hover:underline"
      onclick={() => router.go('/libraries')}
    >{i18n.m.nav.libraries}</button>{i18n.m.schedule.intro_after}
  </p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
{:else if queueStatus}
  <section class="card schedule-dispatch mb-6 p-5 sm:p-6" aria-label={i18n.m.schedule.dispatch_status}>
    <div class="schedule-dispatch-head">
      <div>
        <p class="schedule-eyebrow">{i18n.m.schedule.dispatch_status}</p>
        <h2 class="schedule-dispatch-title">
          {#if queueStatus.canStart}{i18n.m.schedule.queue_ready}
          {:else if queueStatus.manuallyPaused}{i18n.m.schedule.queue_paused}
          {:else}{i18n.m.schedule.queue_blocked}{/if}
        </h2>
      </div>
      <button class="btn min-h-11" onclick={() => router.go('/settings/encoding')}>{i18n.m.settings.room_encoding}</button>
    </div>
    <p class="schedule-dispatch-reason" class:blocked={!queueStatus.canStart}>{dispatchExplanation}</p>
    <dl class="schedule-metrics">
      <div><dt>{i18n.m.schedule.running_jobs}</dt><dd>{queueStatus.runningJobs} / {localWorkloadCapacity(queueStatus)}</dd></div>
      <div><dt>{i18n.m.schedule.scan_interval}</dt><dd>{t(i18n.m.schedule.every_hours, { hours: queueStatus.libraryScanIntervalHours })}</dd></div>
      <div><dt>{i18n.m.schedule.work_disk_free}</dt><dd>{queueStatus.freeDiskBytes === null ? i18n.m.common.unknown : formatSize(queueStatus.freeDiskBytes)}</dd></div>
    </dl>
  </section>

  <section aria-label={i18n.m.schedule.auto_windows_title}>
    <div class="schedule-section-head">
      <div><h2>{i18n.m.schedule.auto_windows_title}</h2><p>{i18n.m.schedule.auto_windows_desc}</p></div>
      <button class="btn min-h-11" onclick={() => router.go('/libraries')}>{i18n.m.nav.libraries}</button>
    </div>
    {#if autoOptimiseLibraries.length > 0}
      <div class="schedule-library-grid">
        {#each autoOptimiseLibraries as lib (lib.id)}
          {@const open = lib.enabled && inWindow(lib.autoEnqueueWindowStart, lib.autoEnqueueWindowEnd)}
          {@const overnight = isOvernightWindow(lib.autoEnqueueWindowStart, lib.autoEnqueueWindowEnd)}
          <article class="card schedule-library p-5 sm:p-6">
            <div class="schedule-library-head">
              <h3>{lib.name}</h3>
              {#if !lib.enabled}<span class="badge tone-muted">{i18n.m.libraries.badge_disabled}</span>
              {:else if open}<span class="badge tone-ok">{i18n.m.schedule.in_window}</span>
              {:else}<span class="badge tone-neutral">{i18n.m.schedule.outside_window}</span>{/if}
            </div>
            <div class="schedule-window">
              <span>{i18n.m.schedule.col_window}</span>
              <strong>{lib.autoEnqueueWindowStart} <span aria-hidden="true">→</span> {lib.autoEnqueueWindowEnd}</strong>
              {#if overnight}<small>{i18n.m.schedule.overnight}</small>{/if}
            </div>
            <p class="schedule-library-reason">{!lib.enabled ? i18n.m.schedule.explain_disabled : open ? i18n.m.schedule.explain_open : i18n.m.schedule.explain_closed}</p>
            <dl class="schedule-library-facts">
              <div><dt>{i18n.m.schedule.col_auto_replace}</dt><dd>{lib.autoReplace ? i18n.m.schedule.when_verified : i18n.m.common.off}</dd></div>
              <div><dt>{i18n.m.schedule.col_last_enqueued}</dt><dd>{lib.lastAutoEnqueueAt ? new Date(lib.lastAutoEnqueueAt).toLocaleString() : i18n.m.common.never}</dd></div>
            </dl>
            <button class="schedule-configure focus-ring" onclick={() => router.go(`/libraries/${lib.id}/configure`)}>{i18n.m.libraries.configure}<span aria-hidden="true">↗</span></button>
          </article>
        {/each}
      </div>
    {:else}
      <div class="card p-6 text-sm text-ink-3">
        {i18n.m.schedule.none_before}<button class="text-accent hover:underline" onclick={() => router.go('/libraries')}>{i18n.m.nav.libraries}</button>{i18n.m.schedule.none_after}
      </div>
    {/if}
  </section>
{/if}

<style>
  .schedule-dispatch-head, .schedule-section-head, .schedule-library-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 1rem; }
  .schedule-eyebrow, .schedule-window > span, .schedule-metrics dt, .schedule-library-facts dt { color: var(--ink-3); font-size: .6875rem; font-weight: 600; letter-spacing: .07em; text-transform: uppercase; }
  .schedule-dispatch-title { color: var(--ink); font-size: 1.5rem; font-weight: 650; letter-spacing: -.025em; line-height: 1.2; margin-top: .35rem; }
  .schedule-dispatch-reason { color: var(--ink-2); font-size: .875rem; line-height: 1.5; margin-top: 1rem; overflow-wrap: anywhere; }
  .schedule-dispatch-reason.blocked { color: var(--warn); }
  .schedule-metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: .75rem; margin-top: 1.25rem; }
  .schedule-metrics > div { background: var(--sunken); border: 1px solid var(--divide-soft); border-radius: .625rem; padding: .875rem 1rem; min-width: 0; }
  .schedule-metrics dd { color: var(--ink); font-size: 1rem; font-variant-numeric: tabular-nums; font-weight: 600; margin-top: .375rem; overflow-wrap: anywhere; }
  .schedule-section-head { align-items: center; margin-bottom: 1rem; }
  .schedule-section-head h2 { color: var(--ink); font-size: 1rem; font-weight: 650; }
  .schedule-section-head p { color: var(--ink-3); font-size: .8125rem; line-height: 1.5; margin-top: .25rem; max-width: 55rem; }
  .schedule-library-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; }
  .schedule-library { min-width: 0; display: flex; flex-direction: column; transition: transform 180ms ease, box-shadow 180ms ease; }
  .schedule-library:hover, .schedule-library:focus-within { transform: translateY(-2px); box-shadow: var(--lift-3), inset 0 1px 0 var(--edge); }
  .schedule-library-head { align-items: center; flex-wrap: wrap; }
  .schedule-library-head h3 { color: var(--ink); font-size: 1rem; font-weight: 600; overflow-wrap: anywhere; min-width: 0; }
  .schedule-window { display: flex; align-items: baseline; flex-wrap: wrap; gap: .35rem .75rem; margin-top: 1.25rem; }
  .schedule-window > span { flex-basis: 100%; }
  .schedule-window strong { color: var(--ink); font: 600 1.125rem ui-monospace, monospace; font-variant-numeric: tabular-nums; }
  .schedule-window small { color: var(--ink-3); font-size: .75rem; }
  .schedule-library-reason { color: var(--ink-3); font-size: .8125rem; line-height: 1.5; margin-top: .75rem; min-height: 2.4em; overflow-wrap: anywhere; }
  .schedule-library-facts { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; border-top: 1px solid var(--divide-soft); margin-top: 1.25rem; padding-top: 1rem; }
  .schedule-library-facts dd { color: var(--ink-2); font-size: .8125rem; margin-top: .3rem; overflow-wrap: anywhere; }
  .schedule-configure { align-self: flex-end; display: inline-flex; align-items: center; gap: .5rem; color: var(--accent); font-size: .8125rem; font-weight: 600; min-height: 2.75rem; margin-top: auto; padding-top: .875rem; }
  .schedule-configure:hover { text-decoration: underline; }
  @media (max-width: 760px) { .schedule-library-grid { grid-template-columns: 1fr; } }
  @media (max-width: 560px) { .schedule-metrics { grid-template-columns: 1fr; }.schedule-dispatch-head, .schedule-section-head { flex-wrap: wrap; } }
  @media (prefers-reduced-motion: reduce) { .schedule-library { transition: none; }.schedule-library:hover, .schedule-library:focus-within { transform: none; } }
</style>
