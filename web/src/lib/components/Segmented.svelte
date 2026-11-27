<script lang="ts" module>
  import type { Snippet } from 'svelte'

  export type SegmentedItem<T extends string> = {
    value: T
    label: string
    /** Shown after the label in tabular figures, e.g. "Candidates (12)". Omit to show none. */
    count?: number | null
    /** Something the segment must say beyond its label and count, such as an unsaved marker. */
    suffix?: Snippet
    disabled?: boolean
    id?: string
    ariaControls?: string
  }
</script>

<script lang="ts" generics="T extends string">
  // Switches between views of the same page. Two semantics, because the pages already
  // announce theirs and assistive technology users rely on what they hear:
  //   "pressed" — toggle buttons (aria-pressed) in a group, or in a <nav> when the switch
  //               moves around a page;
  //   "tabs"    — a WAI-ARIA tablist: one tab stop, arrow keys move between tabs.
  let { items, value, onselect, semantics = 'pressed', as = 'div', label, class: className = '' }: {
    items: SegmentedItem<T>[]
    value: T
    onselect: (value: T) => void
    semantics?: 'pressed' | 'tabs'
    as?: 'div' | 'nav'
    label: string
    class?: string
  } = $props()

  let container: HTMLElement | undefined = $state()
  let tabs = $derived(semantics === 'tabs')
  let tag = $derived(tabs ? 'div' : as)
  let role = $derived(tabs ? 'tablist' : as === 'nav' ? undefined : 'group')
  let tabStopIndex = $derived.by(() => {
    const activeIndex = items.findIndex((item) => item.value === value && !item.disabled)
    if (activeIndex >= 0) return activeIndex
    return items.findIndex((item) => !item.disabled)
  })

  function moveFocus(event: KeyboardEvent, index: number) {
    if (!tabs) return
    const enabled = items.map((item, i) => ({ item, i })).filter(({ item }) => !item.disabled)
    const position = enabled.findIndex(({ i }) => i === index)
    const target = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? enabled[(position + 1) % enabled.length]
      : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? enabled[(position - 1 + enabled.length) % enabled.length]
      : event.key === 'Home' ? enabled[0]
      : event.key === 'End' ? enabled[enabled.length - 1]
      : undefined
    if (!target) return
    event.preventDefault()
    onselect(target.item.value)
    container?.querySelectorAll<HTMLButtonElement>('.segment')[target.i]?.focus()
  }
</script>

<svelte:element this={tag} bind:this={container} class="segmented {className}" {role} aria-label={label}>
  {#each items as item, index (item.value)}
    {@const on = item.value === value}
    <button
      type="button"
      class="segment"
      class:segment-on={on}
      id={item.id}
      role={tabs ? 'tab' : undefined}
      aria-selected={tabs ? on : undefined}
      aria-pressed={tabs ? undefined : on}
      aria-controls={item.ariaControls}
      tabindex={tabs ? (index === tabStopIndex ? 0 : -1) : undefined}
      disabled={item.disabled}
      onclick={() => onselect(item.value)}
      onkeydown={(event) => moveFocus(event, index)}
    ><span>{item.label}{#if item.count != null}{' '}<span class="tabular-nums">({item.count})</span>{/if}</span>{#if item.suffix}{@render item.suffix()}{/if}</button>
  {/each}
</svelte:element>
