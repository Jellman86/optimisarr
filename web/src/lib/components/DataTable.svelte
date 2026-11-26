<script lang="ts" module>
  /** The values a sortable header's `aria-sort` takes; `.table-sort` draws the matching arrow. */
  export type SortState = 'ascending' | 'descending' | 'none'
</script>

<script lang="ts">
  // The one table for operational lists: Inventory, Queue, Quarantine, Candidates and the
  // dashboard's recent results all read the same way. It owns the surface, the sticky header,
  // the row dividers, hover and selected fills, density, and what shows when there are no rows.
  // The page owns its columns: it renders the header cells and rows, and chooses widths and
  // which columns give way on a phone with utilities on its own cells. On a phone each row reads
  // as two lines: the page marks the cell that names the row `data-table-primary` (on its `<td>`
  // and its `<th>`), which takes the first line, and the cells it leaves visible follow on the second.
  //
  // A sortable column marks its header `<th aria-sort={state}>` and puts its label in a
  // `<button class="table-sort">`; the arrow follows `aria-sort`, so sorting needs nothing here.
  import type { Snippet } from 'svelte'
  import type { HTMLAttributes } from 'svelte/elements'
  import { i18n } from '../i18n/i18n.svelte'

  let {
    rowCount,
    head,
    children,
    label,
    labelledby,
    class: tableClass = '',
    bare = false,
    density = 'default',
    layout = 'fixed',
    scroll = 'page',
    scroller = $bindable(),
    loading = false,
    error = null,
    loadingState,
    errorState,
    empty,
    footer,
    ...scrollerAttributes
  }: {
    /** How many rows the body renders. With none, the loading, error or empty state shows instead. */
    rowCount: number
    /** The header row or rows, rendered inside the sticky `<thead>`. */
    head: Snippet
    /** The body rows, rendered inside `<tbody>`. */
    children: Snippet
    label?: string
    labelledby?: string
    /** Classes for the `<table>` itself, for the page's column rules. */
    class?: string
    /** Drop the card surface when the table already sits inside one, such as a panel. */
    bare?: boolean
    /** `compact` tightens the rows for views where seeing more of the list matters most. */
    density?: 'default' | 'compact'
    /** `fixed` keeps column widths stable as rows change; `auto` sizes them to their content. */
    layout?: 'fixed' | 'auto'
    /**
     * `page`: the page scrolls and the header sticks to the top of it, or under a page's own sticky
     * bar when the page sets `--data-table-sticky-top`. `inline`: a table too wide
     * for a phone scrolls sideways inside its surface. `contained`: a long list scrolls inside a
     * bounded surface (`--data-table-max-height`, 65vh by default), with the header stuck to it.
     */
    scroll?: 'page' | 'inline' | 'contained'
    /** The element that scrolls in `contained` mode, for a page that restores its position. */
    scroller?: HTMLDivElement
    /** Rows are being fetched. Rows already shown stay and the table is marked busy. */
    loading?: boolean
    /**
     * Why the rows could not be loaded. With no rows to show, `errorState` renders it; without
     * one nothing does, because the page already reports the failure (and its retry) itself.
     */
    error?: string | null
    loadingState?: Snippet
    errorState?: Snippet<[string]>
    /** What to do next when there are no rows. */
    empty?: Snippet
    /** Sits under the surface: the range of rows shown and the paging controls. */
    footer?: Snippet
  } & Omit<HTMLAttributes<HTMLDivElement>, 'class' | 'children'> = $props()
</script>

<div class="data-table-frame">
  {#if rowCount > 0}
    <div
      {...scrollerAttributes}
      bind:this={scroller}
      class={['data-table-surface', `data-table-scroll-${scroll}`, !bare && 'card']}
    >
      <table
        class={['data-table', `data-table-${layout}`, density === 'compact' && 'data-table-compact', tableClass]}
        aria-label={label}
        aria-labelledby={labelledby}
        aria-busy={loading}
      >
        <thead class="table-head">{@render head()}</thead>
        <tbody>{@render children()}</tbody>
      </table>
    </div>
  {:else if loading}
    <div class={[loadingState ? 'data-table-state' : 'data-table-skeleton', density === 'compact' && 'data-table-compact', !bare && 'card']} role="status">
      {#if loadingState}
        {@render loadingState()}
      {:else}
        <span class="sr-only">{i18n.m.common.loading_short}</span>
        <div aria-hidden="true">
          {#each [1, 2, 3, 4, 5, 6] as row (row)}
            <div class="data-table-skeleton-row">
              <span class="h-3 w-1/2 rounded bg-line"></span>
              <span class="h-3 w-1/5 rounded bg-line"></span>
            </div>
          {/each}
        </div>
      {/if}
    </div>
  {:else if error}
    {#if errorState}<div class={['data-table-state', !bare && 'card']}>{@render errorState(error)}</div>{/if}
  {:else if empty}
    <div class={['data-table-state', !bare && 'card']}>{@render empty()}</div>
  {/if}
  {#if footer}<div class="data-table-footer">{@render footer()}</div>{/if}
</div>
