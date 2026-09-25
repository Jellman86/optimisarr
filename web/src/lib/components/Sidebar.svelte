<script lang="ts">
  import { theme, layout, router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import { counts } from '../stores/counts.svelte'
  import { api } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import BrandMark from './BrandMark.svelte'
  import Icon from './Icon.svelte'
  import LanguageSelect from './LanguageSelect.svelte'
  import NowEncoding from './NowEncoding.svelte'

  const gitHash = typeof __GIT_HASH__ === 'string' ? __GIT_HASH__ : 'unknown'

  // The running backend's version — the same source as the optimisation marker stamped into files —
  // shown alongside the build's git hash. Fetched once; falls back to just the hash if unavailable.
  let version = $state<string | null>(null)
  $effect(() => {
    api.health().then((health) => (version = health.version)).catch(() => {})
  })
  // "0.2.0.0" -> "v0.2.0" for display.
  let versionLabel = $derived(version ? `v${version.split('.').slice(0, 3).join('.')}` : null)

  $effect(() => counts.start())

  /**
   * A figure beside an entry, for the two entries that can ask something of you: the queue
   * while it has work, and quarantine while originals are waiting on a decision.
   *
   * The others deliberately carry nothing. A count on every row is seven numbers competing for
   * attention and five of them never change, which leaves a reader with no idea which of them
   * they were supposed to look at. Null means either nothing to report or nothing known yet —
   * an unanswered poll must never read as zero.
   */
  function badge(path: string): string | null {
    const value = path === '/quarantine' ? counts.quarantine : path === '/queue' ? counts.queued : null
    return value == null || value === 0 ? null : value.toLocaleString()
  }

  // Built here rather than in the template: Svelte trims the leading space out of an inline
  // {#if} block, which renders "3/ 14".
  function queueActivityLabel(): string {
    const queued = badge('/queue')
    return queued ? `${activity.activeJobs} / ${queued}` : `${activity.activeJobs}`
  }

  type NavItem = { path: string; label: string; icon: string; enabled: boolean }

  // Active items route; disabled items mark roadmap phases not yet built. Labels are
  // `$derived` so they re-resolve when the language changes.
  let navItems: NavItem[] = $derived([
    { path: '/', label: i18n.m.nav.dashboard, icon: 'M3 12l9-9 9 9M5 10v10h14V10', enabled: true },
    { path: '/libraries', label: i18n.m.nav.libraries, icon: 'M4 6h16M4 10h16M4 14h10M4 18h10', enabled: true },
    { path: '/inventory', label: i18n.m.nav.inventory, icon: 'M4 5h16v4H4zM4 11h16v8H4z', enabled: true },
    { path: '/queue', label: i18n.m.nav.queue, icon: 'M4 6h16M4 12h16M4 18h7', enabled: true },
    { path: '/quarantine', label: i18n.m.nav.quarantine, icon: 'M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', enabled: true },
    { path: '/schedule', label: i18n.m.nav.schedule, icon: 'M8 7V3m8 4V3M3 11h18M5 5h14a2 2 0 012 2v12a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2z', enabled: true },
    { path: '/settings', label: i18n.m.nav.settings, icon: 'M10.3 4.3a1.7 1.7 0 013.4 0 1.7 1.7 0 002.5 1.1 1.7 1.7 0 012.4 2.4 1.7 1.7 0 001 2.5 1.7 1.7 0 010 3.4 1.7 1.7 0 00-1 2.5 1.7 1.7 0 01-2.4 2.4 1.7 1.7 0 00-2.5 1 1.7 1.7 0 01-3.4 0 1.7 1.7 0 00-2.5-1 1.7 1.7 0 01-2.4-2.4 1.7 1.7 0 00-1-2.5 1.7 1.7 0 010-3.4 1.7 1.7 0 001-2.5A1.7 1.7 0 017.8 5.4a1.7 1.7 0 002.5-1.1zM15 12a3 3 0 11-6 0 3 3 0 016 0z', enabled: true },
  ])

  let collapsed = $derived(layout.collapsed)

  // The collapse-to-icons rail is a desktop-only affordance. On mobile the sidebar is a
  // full-width drawer, so it must always show labels regardless of the persisted collapse
  // state — otherwise a previously-collapsed desktop session leaves the drawer icon-only.
  let isMobile = $state(false)
  $effect(() => {
    const mq = window.matchMedia('(max-width: 767px)')
    const update = () => (isMobile = mq.matches)
    update()
    mq.addEventListener('change', update)
    return () => mq.removeEventListener('change', update)
  })
  let railCollapsed = $derived(collapsed && !isMobile)

  function isActive(path: string) {
    return path === '/' ? router.path === '/' : router.path.startsWith(path)
  }
</script>

<!-- Off-canvas drawer below md (fixed, slides in over a backdrop). At md+ it is an island: a
     raised card floating on the ground beside the page's tray, its own height, with the
     collapse-to-icons rail still available. -->
<aside
  class="app-rail fixed inset-y-0 left-0 z-50 flex h-full w-64 flex-col transition-transform duration-200 md:static md:z-auto md:h-auto md:translate-x-0 md:rounded-[18px] md:transition-[width] {layout.mobileOpen
 ? 'translate-x-0'
 : '-translate-x-full'} {collapsed ? 'md:w-16' : 'md:w-60'}"
>
  <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain">
  <div class="flex min-h-full flex-col">
  <!-- A calm identity above the operational navigation; activity animates the chosen mark. -->
  <button
    class="relative flex w-full flex-col items-center px-3 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 focus-visible:ring-inset {railCollapsed ? 'px-2 pt-5 pb-3' : 'pt-5 pb-1'}"
    aria-label={i18n.m.nav.dashboard}
    onclick={() => {
      router.go('/')
      layout.closeMobile()
    }}
  >
    <BrandMark
      class="relative flex-shrink-0 {railCollapsed ? 'h-10 w-10' : 'h-32 w-32'}"
    />

    {#if !railCollapsed}
      <!-- Keep the contact shadow clear of the wordmark. -->
      <span
        class="relative -mt-2 text-[18px] font-bold tracking-tight text-ink [text-shadow:0_1px_10px_var(--panel)]"
      >Optimisarr</span>
    {/if}
  </button>

  {#if !railCollapsed}
    <!-- The running version and the build it came from, under the name rather than in the foot:
         they are the first thing anyone is asked for when something looks wrong. -->
    <a
      href="https://github.com/jellman86/optimisarr/commits/{gitHash}"
      target="_blank"
      rel="noopener noreferrer"
      class="mx-auto mb-2 rounded px-1 text-center font-mono text-[10.5px] text-ink-4 transition-colors hover:text-accent focus-ring"
      title={version ? t(i18n.m.app.version_build, { version, hash: gitHash }) : t(i18n.m.app.build, { hash: gitHash })}
    >{versionLabel ? `${versionLabel} · ${gitHash}` : `build ${gitHash}`}</a>
  {/if}

  <!-- Nav -->
  <nav class="space-y-1 px-2.5 pt-3 pb-2">
    {#each navItems as item}
      {@const showActivity = item.path === '/queue' && activity.activeJobs > 0}
      <button
        class="nav-button relative"
        class:nav-button-active={item.enabled && isActive(item.path)}
        class:nav-button-inactive={item.enabled && !isActive(item.path)}
        class:nav-button-disabled={!item.enabled}
        disabled={!item.enabled}
        title={!item.enabled ? t(i18n.m.nav.coming_soon, { label: item.label }) : railCollapsed ? item.label : ''}
        onclick={() => {
          if (!item.enabled) return
          router.go(item.path)
          layout.closeMobile()
        }}
      >
        <svg class="h-5 w-5 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d={item.icon} />
        </svg>
        {#if !railCollapsed}
          <span class="truncate">{item.label}</span>
          {#if !item.enabled}
            <span class="badge ml-auto tone-muted">{i18n.m.nav.soon}</span>
          {:else if !showActivity && badge(item.path)}
            <span class="nav-badge">{badge(item.path)}</span>
          {/if}
          {#if showActivity}
            <!-- A throbbing GPU chip means the GPU is doing the work; a snail means it's grinding
                 on the CPU. The count shows how many jobs are running. -->
            <span
              class="ml-auto flex animate-pulse items-center gap-1 {activity.hardwareActive ? 'text-accent' : 'text-warn'}"
              title={activity.hardwareActive ? i18n.m.app.encoding_on_gpu : i18n.m.app.encoding_on_cpu}
            >
              <Icon name={activity.hardwareActive ? 'gpu' : 'snail'} class="h-4 w-4" />
              <span class="font-mono text-[10.5px] tabular-nums">{queueActivityLabel()}</span>
            </span>
          {/if}
        {:else if showActivity}
          <!-- Collapsed rail: a small throbbing dot, GPU-cyan or CPU-amber. -->
          <span
            class="absolute right-1 top-1 h-2 w-2 animate-pulse rounded-full {activity.hardwareActive ? 'bg-cyan-500' : 'bg-amber-500'}"
            title={activity.hardwareActive ? i18n.m.app.encoding_on_gpu : i18n.m.app.encoding_on_cpu}
          ></span>
        {/if}
      </button>
    {/each}
  </nav>

  <!-- Whatever is left between the navigation and the foot belongs to the work in progress. -->
  <div class="min-h-0 flex-1"></div>
  <NowEncoding collapsed={railCollapsed} />

  </div>
  </div>

  <!-- One foot: theme, language, collapse. Three ghost buttons, no strips, no rules. -->
  <div class="flex shrink-0 items-center gap-1 px-2.5 pt-2 pb-3.5 {railCollapsed ? 'flex-col' : 'justify-between'}">
    <button class="btn btn-ghost px-2" onclick={() => theme.toggle()} title={i18n.m.nav.toggle_theme} aria-label={i18n.m.nav.toggle_theme}>
      {#if theme.isDark}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z" /></svg>
      {:else}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z" /></svg>
      {/if}
    </button>
    <LanguageSelect compact opensUp />
    <button class="btn btn-ghost hidden px-2 md:inline-flex" onclick={() => layout.toggle()} title={i18n.m.nav.collapse_sidebar} aria-label={i18n.m.nav.collapse_sidebar}>
      <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
        <path stroke-linecap="round" stroke-linejoin="round" d={collapsed ? 'M13 5l7 7-7 7M5 5l7 7-7 7' : 'M11 19l-7-7 7-7m8 14l-7-7 7-7'} />
      </svg>
    </button>
  </div>
</aside>
