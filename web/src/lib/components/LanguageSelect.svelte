<script lang="ts">
  import { tick } from 'svelte'
  import { i18n, AVAILABLE_LOCALES } from '../i18n/i18n.svelte'

  // `compact` draws the trigger as a globe alone, for a foot row of icon buttons; `opensUp`
  // forces the menu above the trigger, for a trigger that sits at the bottom of the window.
  let { compact = false, opensUp: forceUp = false }: { compact?: boolean; opensUp?: boolean } = $props()

  let open = $state(false)
  // Measured on open unless the caller has fixed the direction.
  let measuredUp = $state(false)
  let opensUp = $derived(forceUp || measuredUp)
  let root: HTMLDivElement
  let trigger: HTMLButtonElement
  let menu = $state<HTMLDivElement>()
  let menuLeft = $state(0)
  let menuMaxHeight = $state(288)
  let positioned = $state(false)

  const selectedName = $derived(
    AVAILABLE_LOCALES.find((locale) => locale.code === i18n.locale)?.name ?? i18n.locale,
  )

  $effect(() => {
    if (!open) return

    function closeOnOutsidePointer(event: PointerEvent) {
      if (!root.contains(event.target as Node)) open = false
    }

    function closeOnEscape(event: KeyboardEvent) {
      if (event.key !== 'Escape') return
      open = false
      trigger.focus()
    }

    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    window.addEventListener('resize', positionMenu)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
      window.removeEventListener('resize', positionMenu)
    }
  })

  function positionMenu() {
    if (!trigger || !menu) return
    const triggerRect = trigger.getBoundingClientRect()
    const spaceBelow = window.innerHeight - triggerRect.bottom
    const spaceAbove = triggerRect.top
    measuredUp = spaceBelow < Math.min(menu.scrollHeight, 288) + 8 && spaceAbove > spaceBelow
    menuMaxHeight = Math.max(0, Math.min(288, (forceUp || measuredUp ? spaceAbove : spaceBelow) - 8))
    if (compact) {
      const width = menu.offsetWidth
      const left = Math.max(8, Math.min(triggerRect.left + (triggerRect.width - width) / 2, window.innerWidth - width - 8))
      menuLeft = left - root.getBoundingClientRect().left
    }
    positioned = true
  }

  async function toggle() {
    open = !open
    if (!open) return
    positioned = false
    await tick()
    positionMenu()
    await tick()
    focusOption(i18n.locale)
  }

  function selectLocale(code: string) {
    i18n.set(code)
    open = false
    trigger.focus()
  }

  function focusOption(code: string) {
    menu?.querySelector<HTMLButtonElement>(`[data-locale="${code}"]`)?.focus()
  }

  function moveFocus(event: KeyboardEvent, index: number) {
    let next = index
    if (event.key === 'ArrowDown') next = Math.min(index + 1, AVAILABLE_LOCALES.length - 1)
    else if (event.key === 'ArrowUp') next = Math.max(index - 1, 0)
    else if (event.key === 'Home') next = 0
    else if (event.key === 'End') next = AVAILABLE_LOCALES.length - 1
    else return

    event.preventDefault()
    focusOption(AVAILABLE_LOCALES[next].code)
  }
</script>

<div class="relative flex items-center gap-2" bind:this={root}>
  {#if !compact}
    <svg class="h-4 w-4 flex-shrink-0 text-ink-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
      <path stroke-linecap="round" stroke-linejoin="round" d="M12 3a9 9 0 100 18 9 9 0 000-18zM3.6 9h16.8M3.6 15h16.8M12 3a13 13 0 000 18M12 3a13 13 0 010 18" />
    </svg>
  {/if}
  <button
    bind:this={trigger}
    type="button"
    class={compact ? 'btn btn-ghost px-2' : 'input flex h-8 w-full items-center justify-between py-0 text-left text-xs'}
    aria-label={compact ? `${i18n.m.language.label}: ${selectedName}` : i18n.m.language.label}
    title={compact ? selectedName : undefined}
    aria-haspopup="listbox"
    aria-expanded={open}
    aria-controls="language-options"
    onclick={toggle}
    onkeydown={(event) => {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault()
        void toggle()
      }
    }}
  >
    {#if compact}
      <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
        <path stroke-linecap="round" stroke-linejoin="round" d="M12 3a9 9 0 100 18 9 9 0 000-18zM3.6 9h16.8M3.6 15h16.8M12 3a13 13 0 000 18M12 3a13 13 0 010 18" />
      </svg>
    {:else}
      <span>{selectedName}</span>
      <svg class="h-3.5 w-3.5 text-ink-4 transition-transform" class:rotate-180={open} fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
        <path stroke-linecap="round" stroke-linejoin="round" d="M19 9l-7 7-7-7" />
      </svg>
    {/if}
  </button>

  {#if open}
    <div
      bind:this={menu}
      id="language-options"
      role="listbox"
      aria-label={i18n.m.language.label}
      class="card absolute z-50 max-h-72 overflow-y-auto p-1 {compact ? 'w-44 max-w-none' : 'left-6 right-0'}"
      style:left={compact ? `${menuLeft}px` : undefined}
      style:max-height={`${menuMaxHeight}px`}
      style:visibility={positioned ? 'visible' : 'hidden'}
      class:bottom-full={opensUp}
      class:mb-1={opensUp}
      class:top-full={!opensUp}
      class:mt-1={!opensUp}
    >
      {#each AVAILABLE_LOCALES as locale, index (locale.code)}
        <button
          type="button"
          role="option"
          aria-selected={locale.code === i18n.locale}
          data-locale={locale.code}
          class="flex w-full items-center justify-between gap-3 whitespace-nowrap rounded-md px-2 py-1.5 text-left text-xs hover:bg-raised focus:bg-raised focus:outline-none"
          class:text-accent={locale.code === i18n.locale}
          onclick={() => selectLocale(locale.code)}
          onkeydown={(event) => moveFocus(event, index)}
        >
          <span>{locale.name}</span>
          {#if locale.code === i18n.locale}
            <span aria-hidden="true">✓</span>
          {/if}
        </button>
      {/each}
    </div>
  {/if}
</div>
