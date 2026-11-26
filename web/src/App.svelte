<script lang="ts">
  import { onMount, tick } from 'svelte'
  import { i18n } from './lib/i18n/i18n.svelte'
  import { router, layout, theme } from './lib/stores/ui.svelte'
  import { activity } from './lib/stores/activity.svelte'
  import { favicon } from './lib/stores/favicon.svelte'
  import { brand } from './lib/stores/brand.svelte'
  import { auth } from './lib/stores/auth.svelte'
  import { setup } from './lib/stores/setup.svelte'
  import Sidebar from './lib/components/Sidebar.svelte'
  import BrandMark from './lib/components/BrandMark.svelte'
  import Icon from './lib/components/Icon.svelte'
  import Dashboard from './lib/pages/Dashboard.svelte'
  import Libraries from './lib/pages/Libraries.svelte'
  import QualityLab from './lib/pages/QualityLab.svelte'
  import Inventory from './lib/pages/Inventory.svelte'
  import OptimiseFile from './lib/pages/OptimiseFile.svelte'
  import ExactDuplicates from './lib/pages/ExactDuplicates.svelte'
  import Queue from './lib/pages/Queue.svelte'
  import Quarantine from './lib/pages/Quarantine.svelte'
  import Schedule from './lib/pages/Schedule.svelte'
  import Settings from './lib/pages/Settings.svelte'
  import Setup from './lib/pages/Setup.svelte'
  import DiagnosticCaptureBanner from './lib/components/DiagnosticCaptureBanner.svelte'
  import StatusStrip from './lib/components/StatusStrip.svelte'
  import ShortcutsDialog from './lib/components/ShortcutsDialog.svelte'
  import QuickJumpDialog from './lib/components/QuickJumpDialog.svelte'
  import {
    capturesKeys,
    createGoChord,
    hasCommandModifier,
    hasOpenModalDialog,
    moveTableRowFocus,
    openFocusedRow,
    toggleFocusedRowSelection,
  } from './lib/keyboard'
  import { APP_TITLE, pageTitle } from './lib/route-meta'

  // Map the active route to its page component.
  let page = $derived.by(() => {
    const path = router.path
    if (/^\/libraries\/\d+\/quality-check$/.test(path)) return QualityLab
    if (/^\/libraries\/\d+\/duplicates$/.test(path)) return ExactDuplicates
    if (path.startsWith('/libraries')) return Libraries
    // Inventory absorbed the Candidates view; the old /candidates route still lands there.
    if (/^\/inventory\/optimise\/\d+$/.test(path)) return OptimiseFile
    if (path === '/inventory/duplicates') return ExactDuplicates
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
    if (path === '/queue' || path === '/queue/failures') return '/queue'
    if (path === '/quarantine' || path.startsWith('/quarantine/')) return '/quarantine'
    const libraryEditor = path.match(/^\/libraries\/(?:new|\d+\/configure)(?:\/|$)/)
    if (libraryEditor) return libraryEditor[0].replace(/\/$/, '')
    return path
  })

  // The shell's scroll container; null while sign-in or setup has the screen instead.
  let mainEl = $state<HTMLElement | null>(null)

  // The tab names the page, so history, tab switchers and screen readers say where you are.
  // Sign-in and setup are not pages of the app yet, so they carry only its name.
  let documentTitle = $derived(mainEl ? pageTitle(router.path, i18n.m) : APP_TITLE)

  // Arriving on a different page starts at its top, with focus on its heading, so a keyboard or
  // screen-reader user hears where they landed instead of staying on the link they left. The
  // first page shown is left alone: the browser already starts there, and grabbing focus on
  // load would draw a ring nobody asked for. A page that moved focus itself (to its own heading
  // or a field) keeps it.
  let shownPageKey: string | null = null
  let focusedBeforeNav: Element | null = null
  $effect.pre(() => {
    const key = pageKey
    if (shownPageKey !== null && key !== shownPageKey) {
      focusedBeforeNav = document.activeElement
    }
  })
  $effect(() => {
    const key = pageKey
    const main = mainEl
    if (!main) {
      shownPageKey = null
      focusedBeforeNav = null
      return
    }
    if (shownPageKey === null || key === shownPageKey) {
      shownPageKey = key
      return
    }
    shownPageKey = key
    const focusedBefore = focusedBeforeNav
    focusedBeforeNav = null
    main.scrollTo({ top: 0 })
    void tick().then(() => {
      if (!main.isConnected || key !== pageKey) return
      const focused = document.activeElement
      if (focused && focused !== focusedBefore && focused !== document.body && main.contains(focused)) return
      const heading = main.querySelector<HTMLElement>('[data-page-heading]') ?? main.querySelector<HTMLElement>('h1[tabindex]')
      heading?.focus({ preventScroll: true })
    })
  })

  // Keyboard shortcuts belong to the signed-in application only. Each one moves, finds, selects
  // or opens something to look at; none of them changes anything, so none needs confirming.
  // An open dialog is never interrupted: its own work comes first.
  let shortcutsOpen = $state(false)
  let jumpOpen = $state(false)

  async function openQuickJump() {
    // Let the sheet close (and hand focus back) before the palette takes focus for itself.
    if (shortcutsOpen) {
      shortcutsOpen = false
      await tick()
    }
    jumpOpen = true
  }

  function jumpTo(path: string) {
    jumpOpen = false
    layout.closeMobile()
    router.go(path)
  }

  function focusPageSearch(main: HTMLElement): boolean {
    const field = main.querySelector<HTMLInputElement>('input[type="search"]:not([disabled])')
    if (!field) return false
    field.focus()
    field.select()
    return true
  }

  $effect(() => {
    const main = mainEl
    if (!main) return
    const chord = createGoChord((path) => {
      router.go(path)
      layout.closeMobile()
    })

    const keydown = (event: KeyboardEvent) => {
      if (event.isComposing || event.defaultPrevented) return

      if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 'k') {
        chord.cancel()
        if (jumpOpen) {
          event.preventDefault()
          jumpOpen = false
        } else if (shortcutsOpen || !hasOpenModalDialog()) {
          event.preventDefault()
          void openQuickJump()
        }
        return
      }

      if (event.key === '?' && !hasCommandModifier(event)) {
        if (capturesKeys(event.target)) return
        chord.cancel()
        if (shortcutsOpen) {
          event.preventDefault()
          shortcutsOpen = false
        } else if (!hasOpenModalDialog()) {
          event.preventDefault()
          shortcutsOpen = true
        }
        return
      }

      if (hasOpenModalDialog()) return chord.cancel()
      if (event.key === 'Escape' && layout.mobileOpen) {
        chord.cancel()
        layout.closeMobile()
        return
      }
      if (layout.mobileOpen || hasCommandModifier(event) || capturesKeys(event.target)) return chord.cancel()

      // A page that reads letter keys itself (the quality check picks a sample by letter) keeps
      // them; search focus and the shortcut sheet still work there.
      const pageOwnsLetters = main.querySelector('[data-keyboard-owner]') !== null
      const chordResult = pageOwnsLetters ? 'ignored' : chord.handle(event)
      if (chordResult === 'armed' || chordResult === 'navigated') return event.preventDefault()
      if (chordResult === 'cancelled') return

      if (event.key === '/') {
        if (focusPageSearch(main)) event.preventDefault()
        return
      }
      if (pageOwnsLetters) return

      const list = main.querySelector('[data-keyboard-list]')
      const handled =
        event.key === 'j' ? moveTableRowFocus(list, 'next')
        : event.key === 'k' ? moveTableRowFocus(list, 'prev')
        : event.key === 'x' ? toggleFocusedRowSelection(list)
        : event.key === 'Enter' ? openFocusedRow(list, event.target)
        : false
      if (handled) event.preventDefault()
    }

    window.addEventListener('keydown', keydown)
    return () => {
      window.removeEventListener('keydown', keydown)
      chord.dispose()
      shortcutsOpen = false
      jumpOpen = false
    }
  })

  let tokenInput = $state('')

  // One app-wide connection drives the sidebar activity indicator and the Queue usage graph.
  onMount(() => {
    void auth.check()
  })

  $effect(() => {
    if (auth.canUseApp) {
      favicon.start()
      void brand.sync()
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
  <title>{documentTitle}</title>
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

  <Sidebar onOpenShortcuts={() => (shortcutsOpen = true)} />

  <!-- min-w-0 is essential: without it this flex child sizes to its widest content
       (tables, grids) and pushes the page off-screen to the right on small viewports. -->
  <div class="flex min-w-0 flex-1 flex-col md:app-tray md:overflow-hidden md:rounded-[18px]">
    <!-- Mobile top bar: hamburger + brand + theme. Hidden once the sidebar is in-flow (md+). -->
    <header
      class="flex items-center gap-3 bg-glass px-4 py-3 shadow-[var(--lift-1)] md:hidden"
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
      <button class="btn btn-ghost ml-auto px-2" aria-label={i18n.m.nav.toggle_theme} aria-describedby="mobile-theme-mode" onclick={() => theme.toggle()}>
        <Icon name={theme.icon} class="h-5 w-5" />
        <span id="mobile-theme-mode" class="sr-only">{i18n.m.nav[`theme_${theme.mode}`]}</span>
      </button>
    </header>

    <!-- relative is a correctness requirement, not decoration: an absolutely positioned
         descendant is only clipped by ancestors in its containing-block chain, so while main
         was static, screen-reader text such as the queue table's sr-only header label kept its
         own box outside this scroll container and stretched the document past the viewport. -->
    <main
      bind:this={mainEl}
      class="relative min-w-0 flex-1 overflow-y-auto scroll-pb-24 px-4 sm:px-6 lg:px-8"
      style="padding-right: max(1rem, env(safe-area-inset-right));"
    >
      <div class="page-frame py-4 sm:py-6 lg:py-8">
        <StatusStrip />
        <DiagnosticCaptureBanner />
        {#key pageKey}
          {@const Page = page}
          <Page />
        {/key}
      </div>
    </main>
  </div>
  </div>
  {#if shortcutsOpen}<ShortcutsDialog onclose={() => (shortcutsOpen = false)} />{/if}
  {#if jumpOpen}<QuickJumpDialog onclose={() => (jumpOpen = false)} onnavigate={jumpTo} />{/if}
{/if}
