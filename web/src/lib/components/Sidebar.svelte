<script lang="ts">
  import { theme, layout, router } from '../stores/ui.svelte'

  type NavItem = { path: string; label: string; icon: string; enabled: boolean }

  // Active items route; disabled items mark roadmap phases not yet built.
  const navItems: NavItem[] = [
    { path: '/', label: 'Dashboard', icon: 'M3 12l9-9 9 9M5 10v10h14V10', enabled: true },
    { path: '/libraries', label: 'Libraries', icon: 'M4 6h16M4 10h16M4 14h10M4 18h10', enabled: true },
    { path: '/inventory', label: 'Inventory', icon: 'M4 5h16v4H4zM4 11h16v8H4z', enabled: true },
    { path: '/candidates', label: 'Candidates', icon: 'M3 4h18l-7 8v6l-4 2v-8z', enabled: true },
    { path: '/tools', label: 'Tools', icon: 'M11 4a4 4 0 015.66 5.66l-9 9L4 20l1.34-3.66 9-9A4 4 0 0111 4z', enabled: true },
    { path: '/queue', label: 'Queue', icon: 'M4 6h16M4 12h16M4 18h7', enabled: true },
    { path: '/verification', label: 'Verification', icon: 'M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z', enabled: false },
    { path: '/quarantine', label: 'Quarantine', icon: 'M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', enabled: true },
    { path: '/schedule', label: 'Schedule', icon: 'M8 7V3m8 4V3M3 11h18M5 5h14a2 2 0 012 2v12a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2z', enabled: false },
    { path: '/settings', label: 'Settings', icon: 'M10.3 4.3a1.7 1.7 0 013.4 0 1.7 1.7 0 002.5 1.1 1.7 1.7 0 012.4 2.4 1.7 1.7 0 001 2.5 1.7 1.7 0 010 3.4 1.7 1.7 0 00-1 2.5 1.7 1.7 0 01-2.4 2.4 1.7 1.7 0 00-2.5 1 1.7 1.7 0 01-3.4 0 1.7 1.7 0 00-2.5-1 1.7 1.7 0 01-2.4-2.4 1.7 1.7 0 00-1-2.5 1.7 1.7 0 010-3.4 1.7 1.7 0 001-2.5A1.7 1.7 0 017.8 5.4a1.7 1.7 0 002.5-1.1zM15 12a3 3 0 11-6 0 3 3 0 016 0z', enabled: true },
  ]

  let collapsed = $derived(layout.collapsed)

  function isActive(path: string) {
    return path === '/' ? router.path === '/' : router.path.startsWith(path)
  }
</script>

<aside
  class="flex h-full flex-col border-r border-slate-200 bg-white/95 backdrop-blur transition-all duration-200 dark:border-slate-700 dark:bg-slate-900/95 {collapsed
    ? 'w-16'
    : 'w-60'}"
>
  <!-- Brand -->
  <button
    class="flex items-center gap-3 border-b border-slate-200 p-4 dark:border-slate-700"
    onclick={() => router.go('/')}
  >
    <img src="/favicon-192.png" alt="Optimisarr" class="h-9 w-9 flex-shrink-0" />
    {#if !collapsed}
      <div class="text-left leading-tight">
        <div class="font-bold text-slate-800 dark:text-slate-100">Optimisarr</div>
        <div class="text-xs text-slate-500 dark:text-slate-400">Safe library optimiser</div>
      </div>
    {/if}
  </button>

  <!-- Nav -->
  <nav class="flex-1 space-y-1 overflow-y-auto p-2">
    {#each navItems as item}
      <button
        class="nav-button"
        class:nav-button-active={item.enabled && isActive(item.path)}
        class:nav-button-inactive={item.enabled && !isActive(item.path)}
        class:nav-button-disabled={!item.enabled}
        disabled={!item.enabled}
        title={!item.enabled ? `${item.label} — coming soon` : collapsed ? item.label : ''}
        onclick={() => item.enabled && router.go(item.path)}
      >
        <svg class="h-5 w-5 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d={item.icon} />
        </svg>
        {#if !collapsed}
          <span class="flex-1">{item.label}</span>
          {#if !item.enabled}
            <span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">soon</span>
          {/if}
        {/if}
      </button>
    {/each}
  </nav>

  <!-- Footer: theme + collapse -->
  <div class="flex items-center gap-1 border-t border-slate-200 p-2 dark:border-slate-700 {collapsed ? 'flex-col' : 'justify-between'}">
    <button class="btn btn-ghost px-2" onclick={() => theme.toggle()} title="Toggle theme" aria-label="Toggle theme">
      {#if theme.isDark}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z" /></svg>
      {:else}
        <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z" /></svg>
      {/if}
    </button>
    <button class="btn btn-ghost px-2" onclick={() => layout.toggle()} title="Collapse sidebar" aria-label="Collapse sidebar">
      <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
        <path stroke-linecap="round" stroke-linejoin="round" d={collapsed ? 'M13 5l7 7-7 7M5 5l7 7-7 7' : 'M11 19l-7-7 7-7m8 14l-7-7 7-7'} />
      </svg>
    </button>
  </div>
</aside>
