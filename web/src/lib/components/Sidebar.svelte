<script lang="ts">
  import { theme, layout, router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import BrandMark from './BrandMark.svelte'
  import Icon from './Icon.svelte'

  const gitHash = typeof __GIT_HASH__ === 'string' ? __GIT_HASH__ : 'unknown'
  const appVersion = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : gitHash

  type NavItem = { path: string; label: string; icon: string; enabled: boolean }

  // Active items route; disabled items mark roadmap phases not yet built.
  const navItems: NavItem[] = [
    { path: '/', label: 'Dashboard', icon: 'M3 12l9-9 9 9M5 10v10h14V10', enabled: true },
    { path: '/libraries', label: 'Libraries', icon: 'M4 6h16M4 10h16M4 14h10M4 18h10', enabled: true },
    { path: '/inventory', label: 'Inventory', icon: 'M4 5h16v4H4zM4 11h16v8H4z', enabled: true },
    { path: '/queue', label: 'Queue', icon: 'M4 6h16M4 12h16M4 18h7', enabled: true },
    { path: '/quarantine', label: 'Quarantine', icon: 'M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', enabled: true },
    { path: '/schedule', label: 'Schedule', icon: 'M8 7V3m8 4V3M3 11h18M5 5h14a2 2 0 012 2v12a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2z', enabled: true },
    { path: '/settings', label: 'Settings', icon: 'M10.3 4.3a1.7 1.7 0 013.4 0 1.7 1.7 0 002.5 1.1 1.7 1.7 0 012.4 2.4 1.7 1.7 0 001 2.5 1.7 1.7 0 010 3.4 1.7 1.7 0 00-1 2.5 1.7 1.7 0 01-2.4 2.4 1.7 1.7 0 00-2.5 1 1.7 1.7 0 01-3.4 0 1.7 1.7 0 00-2.5-1 1.7 1.7 0 01-2.4-2.4 1.7 1.7 0 00-1-2.5 1.7 1.7 0 010-3.4 1.7 1.7 0 001-2.5A1.7 1.7 0 017.8 5.4a1.7 1.7 0 002.5-1.1zM15 12a3 3 0 11-6 0 3 3 0 016 0z', enabled: true },
  ]

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

<!-- Off-canvas drawer below md (fixed, slides in over a backdrop); a static in-flow rail
     at md+ that can collapse to icons. -->
<aside
  class="fixed inset-y-0 left-0 z-50 flex h-full w-64 flex-col border-r border-slate-200 bg-white/95 backdrop-blur transition-transform duration-200 md:static md:z-auto md:translate-x-0 md:transition-[width] dark:border-slate-700 dark:bg-slate-900/95 {layout.mobileOpen
    ? 'translate-x-0'
    : '-translate-x-full'} {collapsed ? 'md:w-16' : 'md:w-60'}"
>
  <!-- Brand: large centered mark that scales between collapsed and expanded. -->
  <button
    class="flex flex-col items-center gap-3 border-b border-slate-200 p-4 text-center transition-all duration-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-cyan-500/50 dark:border-slate-700"
    onclick={() => {
      router.go('/')
      layout.closeMobile()
    }}
  >
    <BrandMark
      sizes={railCollapsed ? '48px' : '144px'}
      class="flex-shrink-0 drop-shadow-[0_0_18px_rgba(34,211,238,0.32)] transition-all duration-200 {railCollapsed ? 'h-12 w-12' : 'h-36 w-36'}"
    />
    {#if !railCollapsed}
      <div class="leading-tight">
        <div class="font-bold tracking-tight text-slate-800 dark:text-slate-100">Optimisarr</div>
        <div class="text-xs text-slate-500 dark:text-slate-400">Safe library optimiser</div>
      </div>
    {/if}
  </button>

  <!-- Nav -->
  <nav class="flex-1 space-y-1 overflow-y-auto p-2">
    {#each navItems as item}
      {@const showActivity = item.path === '/queue' && activity.activeJobs > 0}
      <button
        class="nav-button relative"
        class:nav-button-active={item.enabled && isActive(item.path)}
        class:nav-button-inactive={item.enabled && !isActive(item.path)}
        class:nav-button-disabled={!item.enabled}
        disabled={!item.enabled}
        title={!item.enabled ? `${item.label} — coming soon` : railCollapsed ? item.label : ''}
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
          <span class="flex-1">{item.label}</span>
          {#if !item.enabled}
            <span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">soon</span>
          {/if}
          {#if showActivity}
            <!-- A throbbing GPU chip means the GPU is doing the work; a snail means it's grinding
                 on the CPU. The count shows how many jobs are running. -->
            <span
              class="flex animate-pulse items-center gap-1 {activity.hardwareActive ? 'text-cyan-500' : 'text-amber-500'}"
              title={activity.hardwareActive ? 'Encoding on GPU' : 'Encoding on CPU'}
            >
              <Icon name={activity.hardwareActive ? 'gpu' : 'snail'} class="h-4 w-4" />
              <span class="text-xs tabular-nums">{activity.activeJobs}</span>
            </span>
          {/if}
        {:else if showActivity}
          <!-- Collapsed rail: a small throbbing dot, GPU-cyan or CPU-amber. -->
          <span
            class="absolute right-1 top-1 h-2 w-2 animate-pulse rounded-full {activity.hardwareActive ? 'bg-cyan-500' : 'bg-amber-500'}"
            title={activity.hardwareActive ? 'Encoding on GPU' : 'Encoding on CPU'}
          ></span>
        {/if}
      </button>
    {/each}
  </nav>

  <!-- Build version (git hash) -->
  <a
    href="https://github.com/jellman86/optimisarr/commits/{gitHash}"
    target="_blank"
    rel="noopener noreferrer"
    class="border-t border-slate-200 px-2 py-1.5 text-center font-mono text-[10px] text-slate-400 transition-colors hover:text-cyan-600 dark:border-slate-700 dark:text-slate-500 dark:hover:text-cyan-400"
    title="Build {appVersion}"
  >
    {railCollapsed ? gitHash.slice(0, 4) : `build ${gitHash}`}
  </a>

  <!-- Footer: theme + collapse -->
  <div class="flex items-center gap-1 border-t border-slate-200 p-2 dark:border-slate-700 {railCollapsed ? 'flex-col' : 'justify-between'}">
    <button class="btn btn-ghost px-2" onclick={() => theme.toggle()} title="Toggle theme" aria-label="Toggle theme">
      {#if theme.isDark}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z" /></svg>
      {:else}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z" /></svg>
      {/if}
    </button>
    <button class="btn btn-ghost hidden px-2 md:inline-flex" onclick={() => layout.toggle()} title="Collapse sidebar" aria-label="Collapse sidebar">
      <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
        <path stroke-linecap="round" stroke-linejoin="round" d={collapsed ? 'M13 5l7 7-7 7M5 5l7 7-7 7' : 'M11 19l-7-7 7-7m8 14l-7-7 7-7'} />
      </svg>
    </button>
  </div>
</aside>
