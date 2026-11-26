<script lang="ts">
  // Quick Jump: type a few letters of a page, a settings room or a library and go there. It only
  // ever navigates. Nothing here starts, changes or removes anything, so there is nothing to
  // confirm and nothing to undo; the footer says as much.
  import { onMount } from 'svelte'
  import { api } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import { modal } from '../modal'
  import { filterJumpItems, quickJumpItems, wrapIndex, type JumpGroup, type JumpItem } from '../keyboard'
  import Icon from './Icon.svelte'

  let { onclose, onnavigate }: { onclose: () => void; onnavigate: (path: string) => void } = $props()

  let query = $state('')
  let field: HTMLInputElement | undefined = $state()
  let libraries = $state<{ id: number; name: string }[]>([])
  let loadingLibraries = $state(true)
  let activeId = $state<string | null>(null)

  // Libraries are fetched only while the palette is open. If they cannot be fetched, pages and
  // settings still work and the libraries group is simply absent.
  onMount(() => {
    let alive = true
    api.libraries()
      .then((found) => { if (alive) libraries = found.map(({ id, name }) => ({ id, name })) })
      .catch(() => {})
      .finally(() => { if (alive) loadingLibraries = false })
    queueMicrotask(() => field?.focus())
    return () => { alive = false }
  })

  let groupLabels = $derived<Record<JumpGroup, string>>({
    pages: i18n.m.shortcuts.jump_pages,
    settings: i18n.m.shortcuts.jump_settings,
    libraries: i18n.m.shortcuts.jump_libraries,
  })
  let results = $derived(filterJumpItems(quickJumpItems(i18n.m, libraries), query, groupLabels))
  let activeIndex = $derived.by(() => {
    if (results.length === 0) return -1
    const found = activeId === null ? -1 : results.findIndex((item) => item.id === activeId)
    return found < 0 ? 0 : found
  })
  let activeItem = $derived(activeIndex >= 0 ? results[activeIndex] : null)
  let sections = $derived(
    (['pages', 'settings', 'libraries'] as JumpGroup[])
      .map((group) => ({ group, items: results.map((item, index) => ({ item, index })).filter(({ item }) => item.group === group) }))
      .filter((section) => section.items.length > 0),
  )

  function optionId(item: JumpItem) {
    return `quick-jump-option-${item.id.replace(/[^a-z0-9_-]/gi, '-')}`
  }

  let activeOptionId = $derived(activeItem ? optionId(activeItem) : undefined)
  let statusText = $derived(
    loadingLibraries ? i18n.m.shortcuts.jump_loading_libraries
    : results.length === 0 ? i18n.m.shortcuts.jump_empty
    : '',
  )

  $effect(() => {
    if (activeOptionId) document.getElementById(activeOptionId)?.scrollIntoView({ block: 'nearest' })
  })

  // Only a pointer that actually moved picks an option: the list scrolling under a resting pointer
  // (because the arrow keys moved the active option) must not snatch the choice back.
  let lastPointer = ''
  function point(event: MouseEvent, item: JumpItem) {
    const at = `${event.clientX},${event.clientY}`
    if (at === lastPointer) return
    lastPointer = at
    activeId = item.id
  }

  function go(item: JumpItem | undefined) {
    if (item) onnavigate(item.path)
  }

  function keydown(event: KeyboardEvent) {
    if (event.isComposing) return
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      const next = wrapIndex(activeIndex, results.length, event.key === 'ArrowDown' ? 1 : -1)
      activeId = next < 0 ? null : results[next].id
    } else if (event.key === 'Enter') {
      event.preventDefault()
      go(results[activeIndex])
    }
  }
</script>

<dialog id="quick-jump-dialog" class="app-modal quick-jump-dialog" use:modal={onclose} aria-labelledby="quick-jump-title">
  <h2 id="quick-jump-title" class="sr-only">{i18n.m.shortcuts.jump_title}</h2>
  <div class="hairline-b flex items-center gap-2 p-3">
    <div class="relative min-w-0 flex-1">
      <Icon name="search" class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-ink-3" />
      <input
        bind:this={field}
        bind:value={query}
        oninput={() => (activeId = null)}
        onkeydown={keydown}
        type="text"
        class="input pl-9"
        role="combobox"
        aria-label={i18n.m.shortcuts.jump_label}
        aria-expanded={results.length > 0}
        aria-controls="quick-jump-listbox"
        aria-autocomplete="list"
        aria-activedescendant={activeOptionId}
        placeholder={i18n.m.shortcuts.jump_placeholder}
        autocomplete="off"
        spellcheck="false"
      />
    </div>
    <button type="button" class="btn btn-ghost btn-icon" onclick={onclose} aria-label={i18n.m.shortcuts.jump_close}><Icon name="x" /></button>
  </div>

  <div class="min-h-0 overflow-y-auto overscroll-contain p-2">
    <div id="quick-jump-listbox" role="listbox" aria-label={i18n.m.shortcuts.jump_label}>
      {#each sections as section (section.group)}
        <div role="group" aria-labelledby={`quick-jump-group-${section.group}`} class="mb-2 last:mb-0">
          <div id={`quick-jump-group-${section.group}`} role="presentation" class="px-3 pt-2 pb-1 text-caption font-semibold text-ink-3">{groupLabels[section.group]}</div>
          {#each section.items as { item, index } (item.id)}
            <!-- The combobox input keeps DOM focus and drives active options via aria-activedescendant;
                 pointer clicks pick an option without stealing focus from the field. -->
            <!-- svelte-ignore a11y_click_events_have_key_events, a11y_interactive_supports_focus -->
            <div
              id={optionId(item)}
              role="option"
              aria-selected={index === activeIndex}
              class="flex cursor-pointer items-center justify-between gap-3 rounded-lg px-3 py-2 text-label text-ink"
              class:bg-accent-soft={index === activeIndex}
              class:font-semibold={index === activeIndex}
              onmousedown={(event) => event.preventDefault()}
              onmousemove={(event) => point(event, item)}
              onclick={() => go(item)}
            >
              <span class="min-w-0 truncate">{item.label}</span>
              {#if item.chord}
                <span class="flex shrink-0 items-center gap-1" aria-hidden="true"><kbd class="kbd">g</kbd><kbd class="kbd">{item.chord}</kbd></span>
              {/if}
            </div>
          {/each}
        </div>
      {/each}
    </div>
    <p
      role="status"
      class={[
        loadingLibraries && 'px-3 py-2 text-caption text-ink-3',
        !loadingLibraries && results.length === 0 && 'px-3 py-6 text-center text-label text-ink-3',
        !statusText && 'sr-only',
      ]}
    >{statusText}</p>
  </div>

  <p class="hairline-t px-4 py-2.5 text-caption text-ink-3">{i18n.m.shortcuts.jump_hint}</p>
</dialog>
