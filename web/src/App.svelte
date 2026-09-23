<script lang="ts">
  import { onMount } from 'svelte'
  import { i18n } from './lib/i18n/i18n.svelte'
  import { router, layout, theme } from './lib/stores/ui.svelte'
  import { activity } from './lib/stores/activity.svelte'
  import { favicon } from './lib/stores/favicon.svelte'
  import { auth } from './lib/stores/auth.svelte'
  import { setup } from './lib/stores/setup.svelte'
  import Sidebar from './lib/components/Sidebar.svelte'
  import BrandMark from './lib/components/BrandMark.svelte'
  import Dashboard from './lib/pages/Dashboard.svelte'
  import Libraries from './lib/pages/Libraries.svelte'
  import QualityLab from './lib/pages/QualityLab.svelte'
  import Inventory from './lib/pages/Inventory.svelte'
  import Queue from './lib/pages/Queue.svelte'
  import Quarantine from './lib/pages/Quarantine.svelte'
  import Schedule from './lib/pages/Schedule.svelte'
  import Settings from './lib/pages/Settings.svelte'
  import Setup from './lib/pages/Setup.svelte'

  // Map the active route to its page component.
  let page = $derived.by(() => {
    const path = router.path
    if (/^\/libraries\/\d+\/quality-check$/.test(path)) return QualityLab
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

  // What identifies "a different page" for the purposes of remounting. It is deliberately
  // not the raw path: Settings navigates between its own rooms by URL, and it holds an
  // unsaved draft while you do. Remounting on every path change would throw that draft away
  // the moment you walked from one room to another, which is the whole reason rooms could be
  // worse than one long page.
  let pageKey = $derived.by(() => {
    const path = router.path
    if (path.startsWith('/settings') || path.startsWith('/tools')) return '/settings'
    if (path === '/quarantine' || path.startsWith('/quarantine/')) return '/quarantine'
    const libraryEditor = path.match(/^\/libraries\/(?:new|\d+\/configure)(?:\/|$)/)
    if (libraryEditor) return libraryEditor[0].replace(/\/$/, '')
    return path
  })

  let tokenInput = $state('')

  // One app-wide connection drives the sidebar activity indicator and the Queue usage graph.
  onMount(() => {
    void auth.check()
  })

  $effect(() => {
    if (auth.canUseApp) {
      favicon.start()
      if (!setup.checked && !setup.loading) void setup.load().catch(() => {})
      else if (setup.checked && !setup.required) { activity.start() }
    }
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
    class="flex h-dvh items-center justify-center bg-ground p-4 text-ink"
    style="padding-top: max(var(--shell-inset, 0px), env(safe-area-inset-top)); padding-bottom: max(var(--shell-inset, 0px), env(safe-area-inset-bottom));"
  >
    <div class="flex items-center gap-3 text-ink-3">
      <BrandMark class="h-8 w-8" />
      <span class="text-sm font-semibold">{i18n.m.common.loading}</span>
    </div>
  </div>
{:else if auth.required && !auth.token}
  <div
    class="flex h-dvh items-center justify-center bg-ground p-4 text-ink"
    style="padding-top: max(var(--shell-inset, 0px), env(safe-area-inset-top)); padding-bottom: max(var(--shell-inset, 0px), env(safe-area-inset-bottom));"
  >
    <form class="card w-full max-w-sm p-6" onsubmit={submitToken}>
      <div class="mb-6 flex items-center gap-3">
        <BrandMark class="h-9 w-9" />
        <div>
          <h1 class="text-lg font-bold tracking-tight text-ink">Optimisarr</h1>
          <p class="text-sm text-ink-3">{i18n.m.auth.token_required}</p>
        </div>
      </div>

      <label class="label" for="admin-token">{i18n.m.auth.token_label}</label>
      <input
        id="admin-token"
        class="input"
        type="password"
        autocomplete="current-password"
        bind:value={tokenInput}
      />

      {#if auth.error}
        <p class="callout tone-bad mt-3">
          {auth.error}
        </p>
      {/if}

      <button class="btn btn-primary mt-5 w-full" type="submit" disabled={auth.checking}>
        {auth.checking ? i18n.m.common.checking : i18n.m.common.continue}
      </button>
    </form>
  </div>
{:else if !setup.checked || setup.loading && setup.state === null}
  <div class="flex h-dvh items-center justify-center bg-ground p-4 text-ink">
    <div class="flex items-center gap-3 text-ink-3">
      <BrandMark class="h-8 w-8" />
      <span class="text-sm font-semibold">{i18n.m.setup.loading}</span>
    </div>
  </div>
{:else if setup.error && setup.state === null}
  <div class="flex h-dvh items-center justify-center bg-ground p-4 text-ink">
    <div class="card w-full max-w-md p-6 text-center">
      <h1 class="font-semibold text-ink">{i18n.m.setup.error_heading}</h1>
      <p class="mt-2 text-sm text-bad">{setup.error}</p>
      <button class="btn btn-primary mt-5" onclick={() => void setup.load().catch(() => {})}>{i18n.m.setup.retry}</button>
    </div>
  </div>
{:else if setup.required}
  <Setup />
{:else}
  <!-- h-dvh tracks iOS Safari's dynamic toolbar; the safe-area insets keep the bar
       and content clear of the notch and home indicator. -->
  <!-- At md+ the shell is two islands on the ground: the rail, a raised card, and the page in a
       recessed tray beside it. On a phone the rail is a drawer and the page takes the screen. -->
  <div
    class="flex h-dvh bg-ground text-ink md:gap-3.5 md:p-3.5 md:[--shell-inset:1rem]"
    style="padding-top: max(var(--shell-inset, 0px), env(safe-area-inset-top)); padding-bottom: max(var(--shell-inset, 0px), env(safe-area-inset-bottom));"
  >
  <!-- Backdrop behind the mobile drawer; tap to dismiss. Desktop never shows it. -->
  {#if layout.mobileOpen}
    <button
      class="fixed inset-0 z-40 bg-black/50 backdrop-blur-sm md:hidden"
      aria-label={i18n.m.nav.close_menu}
      onclick={() => layout.closeMobile()}
    ></button>
  {/if}

  <Sidebar />

  <!-- min-w-0 is essential: without it this flex child sizes to its widest content
       (tables, grids) and pushes the page off-screen to the right on small viewports. -->
  <div class="flex min-w-0 flex-1 flex-col md:app-tray md:overflow-hidden md:rounded-[18px]">
    <!-- Mobile top bar: hamburger + brand + theme. Hidden once the sidebar is in-flow (md+). -->
    <header
      class="flex items-center gap-3 border-b border-line bg-panel/95 px-4 py-3 backdrop-blur md:hidden"
    >
      <button class="btn btn-ghost px-2" aria-label={i18n.m.nav.open_menu} onclick={() => layout.toggleMobile()}>
        <svg class="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d="M4 6h16M4 12h16M4 18h16" />
        </svg>
      </button>
      <button class="flex items-center gap-2" onclick={() => router.go('/')}>
        <BrandMark class="h-7 w-7" />
        <span class="font-bold tracking-tight text-ink">Optimisarr</span>
      </button>
      <button class="btn btn-ghost ml-auto px-2" aria-label={i18n.m.nav.toggle_theme} onclick={() => theme.toggle()}>
        {#if theme.isDark}
          <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z" /></svg>
        {:else}
          <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z" /></svg>
        {/if}
      </button>
    </header>

    <main
      class="min-w-0 flex-1 overflow-y-auto scroll-pb-24 p-4 sm:p-6 lg:p-8"
      style="padding-right: max(1rem, env(safe-area-inset-right));"
    >
      <div class:mx-auto={!/^\/libraries\/\d+\/quality-check$/.test(router.path)} class:max-w-6xl={!/^\/libraries\/\d+\/quality-check$/.test(router.path)}>
        {#key pageKey}
          {@const Page = page}
          <Page />
        {/key}
      </div>
    </main>
  </div>
  </div>
{/if}
