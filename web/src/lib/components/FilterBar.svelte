<script lang="ts" module>
  export type FilterItem<T extends string> = {
    value: T
    label: string
    /** How many rows the filter would show, in tabular figures: "Failed · 3". Omit to show none. */
    count?: number | null
  }
</script>

<script lang="ts" generics="T extends string">
  // Narrows the rows of the list below it. The chosen filter is a lit fill, never the accent
  // fill: that is kept for the one action on the page that advances the work.
  let { items, value, onselect, label, class: className = '' }: {
    items: FilterItem<T>[]
    value: T
    onselect: (value: T) => void
    label: string
    class?: string
  } = $props()
</script>

<div class="filter-bar {className}" role="group" aria-label={label}>
  {#each items as item (item.value)}
    <button type="button" class="filter-pill" class:filter-pill-on={item.value === value} aria-pressed={item.value === value}
      onclick={() => onselect(item.value)}><span>{item.label}{#if item.count != null}{' · '}<span class="tabular-nums">{item.count}</span>{/if}</span></button>
  {/each}
</div>
