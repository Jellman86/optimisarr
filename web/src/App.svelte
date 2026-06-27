<script lang="ts">
  import { onMount } from 'svelte'
  import { router, layout, theme } from './lib/stores/ui.svelte'
  import { activity } from './lib/stores/activity.svelte'
  import { auth } from './lib/stores/auth.svelte'
  import Sidebar from './lib/components/Sidebar.svelte'
  import BrandMark from './lib/components/BrandMark.svelte'
  import Dashboard from './lib/pages/Dashboard.svelte'
  import Libraries from './lib/pages/Libraries.svelte'
  import Inventory from './lib/pages/Inventory.svelte'
  import Queue from './lib/pages/Queue.svelte'
  import Quarantine from './lib/pages/Quarantine.svelte'
  import Schedule from './lib/pages/Schedule.svelte'
  import Settings from './lib/pages/Settings.svelte'

  // Map the active route to its page component.
  let page = $derived.by(() => {
    const path = router.path
    if (path.startsWith('/libraries')) return Libraries
    // Inventory absorbed the Candidates view; the old /candidates route still lands there.
    if (path.startsWith('/inventory') || path.startsWith('/candidates')) return Inventory
    if (path.startsWith('/queue')) return Queue
    if (path.startsWith('/quarantine')) return Quarantine
    if (path.startsWith('/schedule')) return Schedule
    // Tools moved into Settings; the old route still lands there (opens the Tools tab).
    if (path.startsWith('/tools') || path.startsWith('/settings')) return Settings
    return Dashboard
  })

  let tokenInput = $state('')

  // One app-wide connection drives the sidebar activity indicator and the Queue usage graph.
  onMount(() => {
    void auth.check()
  })

  $effect(() => {
    if (auth.canUseApp) activity.start()
  })

  async function submitToken(event: SubmitEvent) {
    event.preventDefault()
    await auth.login(tokenInput)
    if (auth.token) tokenInput = ''
  }
</script>

<svelte:head>
  <title>Optimisarr</title>
</svelte:head>

{#if !auth.checked}
  <div
    class="flex h-dvh items-center justify-center bg-slate-50 p-4 text-slate-800 dark:bg-slate-950 dark:text-slate-200"
    style="padding-top: env(safe-area-inset-top); padding-bottom: env(safe-area-inset-bottom);"
  >
    <div class="flex items-center gap-3 text-slate-500 dark:text-slate-400">
      <BrandMark sizes="32px" class="h-8 w-8" />
      <span class="text-sm font-semibold">Loading Optimisarr...</span>
    </div>
  </div>
{:else if auth.required && !auth.token}
  <div
    class="flex h-dvh items-center justify-center bg-slate-50 p-4 text-slate-800 dark:bg-slate-950 dark:text-slate-200"
    style="padding-top: env(safe-area-inset-top); padding-bottom: env(safe-area-inset-bottom);"
  >
    <form class="card w-full max-w-sm p-6" onsubmit={submitToken}>
      <div class="mb-6 flex items-center gap-3">
        <BrandMark sizes="36px" class="h-9 w-9" />
        <div>
          <h1 class="text-lg font-bold tracking-tight text-slate-900 dark:text-slate-100">Optimisarr</h1>
          <p class="text-sm text-slate-500 dark:text-slate-400">Admin token required</p>
        </div>
      </div>

      <label class="label" for="admin-token">Admin token</label>
      <input
        id="admin-token"
        class="input"
        type="password"
        autocomplete="current-password"
        bind:value={tokenInput}
      />

      {#if auth.error}
        <p class="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300">
          {auth.error}
        </p>
      {/if}

      <button class="btn btn-primary mt-5 w-full" type="submit" disabled={auth.checking}>
        {auth.checking ? 'Checking...' : 'Continue'}
      </button>
    </form>
  </div>
{:else}
  <!-- h-dvh tracks iOS Safari's dynamic toolbar; the safe-area insets keep the bar
       and content clear of the notch and home indicator. -->
  <div
    class="flex h-dvh bg-slate-50 text-slate-800 dark:bg-slate-950 dark:text-slate-200"
    style="padding-top: env(safe-area-inset-top); padding-bottom: env(safe-area-inset-bottom);"
  >
  <!-- Backdrop behind the mobile drawer; tap to dismiss. Desktop never shows it. -->
  {#if layout.mobileOpen}
    <button
      class="fixed inset-0 z-40 bg-slate-900/50 backdrop-blur-sm md:hidden"
      aria-label="Close menu"
      onclick={() => layout.closeMobile()}
    ></button>
  {/if}

  <Sidebar />

  <!-- min-w-0 is essential: without it this flex child sizes to its widest content
       (tables, grids) and pushes the page off-screen to the right on small viewports. -->
  <div class="flex min-w-0 flex-1 flex-col">
    <!-- Mobile top bar: hamburger + brand + theme. Hidden once the sidebar is in-flow (md+). -->
    <header
      class="flex items-center gap-3 border-b border-slate-200 bg-white/95 px-4 py-3 backdrop-blur md:hidden dark:border-slate-700 dark:bg-slate-900/95"
    >
      <button class="btn btn-ghost px-2" aria-label="Open menu" onclick={() => layout.toggleMobile()}>
        <svg class="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d="M4 6h16M4 12h16M4 18h16" />
        </svg>
      </button>
      <button class="flex items-center gap-2" onclick={() => router.go('/')}>
        <BrandMark sizes="28px" class="h-7 w-7" />
        <span class="font-bold tracking-tight text-slate-800 dark:text-slate-100">Optimisarr</span>
      </button>
      <button class="btn btn-ghost ml-auto px-2" aria-label="Toggle theme" onclick={() => theme.toggle()}>
        {#if theme.isDark}
          <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z" /></svg>
        {:else}
          <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z" /></svg>
        {/if}
      </button>
    </header>

    <main
      class="min-w-0 flex-1 overflow-y-auto p-4 sm:p-6 lg:p-8"
      style="padding-right: max(1rem, env(safe-area-inset-right));"
    >
      <div class="mx-auto max-w-6xl">
        {#key router.path}
          {@const Page = page}
          <Page />
        {/key}
      </div>
    </main>
  </div>
  </div>
{/if}
