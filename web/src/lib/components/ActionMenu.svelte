<script lang="ts">
  // A small "more actions" menu for the secondary and destructive actions of a card, so the card
  // shows one primary button and the rest stay one click away instead of competing with it.
  // Keyboard: Escape closes and returns focus, arrows move between items, Tab leaves and closes.
  import Icon from './Icon.svelte'

  export type ActionMenuItem = {
    label: string
    icon?: string
    onSelect: () => void
    danger?: boolean
    disabled?: boolean
    title?: string
  }

  let {
    label,
    items,
    disabled = false,
  }: { label: string; items: ActionMenuItem[]; disabled?: boolean } = $props()

  let open = $state(false)
  let root: HTMLDivElement | undefined = $state()
  let trigger: HTMLButtonElement | undefined = $state()
  let itemButtons: HTMLButtonElement[] = $state([])

  function close(restoreFocus = false) {
    open = false
    if (restoreFocus) trigger?.focus()
  }

  function toggle() {
    open = !open
    if (open) queueMicrotask(() => focusItem(1))
  }

  // Move focus to the next enabled item in `direction`, wrapping; from nowhere, land on the first
  // (or last) one.
  function focusItem(direction: 1 | -1) {
    const enabled = itemButtons.filter((button) => button && !button.disabled)
    if (enabled.length === 0) return
    const current = enabled.indexOf(document.activeElement as HTMLButtonElement)
    const next = current === -1
      ? (direction === 1 ? 0 : enabled.length - 1)
      : (current + direction + enabled.length) % enabled.length
    enabled[next]?.focus()
  }

  function onMenuKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.preventDefault()
      close(true)
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      focusItem(event.key === 'ArrowDown' ? 1 : -1)
    } else if (event.key === 'Tab') {
      close()
    }
  }

  function onTriggerKeydown(event: KeyboardEvent) {
    if (open && event.key === 'Escape') {
      event.preventDefault()
      close(true)
    }
  }

  function onDocumentPointerDown(event: PointerEvent) {
    if (open && root && !root.contains(event.target as Node)) close()
  }
</script>

<svelte:document onpointerdown={onDocumentPointerDown} />

<div class="relative flex-shrink-0" bind:this={root}>
  <button
    type="button"
    class="btn min-h-11 px-2"
    aria-haspopup="menu"
    aria-expanded={open}
    aria-label={label}
    title={label}
    {disabled}
    bind:this={trigger}
    onclick={toggle}
    onkeydown={onTriggerKeydown}
  >
    <Icon name="more" class="h-5 w-5" />
  </button>
  {#if open}
    <div
      role="menu"
      tabindex="-1"
      aria-label={label}
      class="absolute right-0 z-20 mt-1 min-w-44 rounded-lg border border-slate-200 bg-white p-1 shadow-lg dark:border-slate-700 dark:bg-slate-900"
      onkeydown={onMenuKeydown}
    >
      {#each items as item, index (item.label)}
        <button
          type="button"
          role="menuitem"
          class="flex min-h-11 w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-cyan-500/50 disabled:pointer-events-none disabled:opacity-50 {item.danger
            ? 'text-red-700 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-950'
            : 'text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-800'}"
          disabled={item.disabled}
          title={item.title}
          bind:this={itemButtons[index]}
          onclick={() => {
            close()
            item.onSelect()
          }}
        >
          {#if item.icon}<Icon name={item.icon} class="h-4 w-4" />{/if}
          {item.label}
        </button>
      {/each}
    </div>
  {/if}
</div>
