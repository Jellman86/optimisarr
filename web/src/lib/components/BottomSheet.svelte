<script lang="ts">
  import type { Snippet } from 'svelte'
  import { layout } from '../stores/ui.svelte'

  type Props = {
    // Whether the sheet is slid into view.
    open: boolean
    // Full content (true) vs. header-only strip (false). Bindable so the parent can reset it.
    expanded?: boolean
    // The sheet's measured rendered height in px, so a parent can shrink a table to match.
    height?: number
    onclose: () => void
    header: Snippet
    children: Snippet
    // Optional ambient layer rendered behind the whole sheet (header + content), e.g. a faded
    // poster. Fills the panel and is clipped to it, so it never scrolls with the content.
    backdrop?: Snippet
  }

  let {
    open,
    expanded = $bindable(true),
    height = $bindable(0),
    onclose,
    header,
    children,
    backdrop,
  }: Props = $props()

  // Mirror Sidebar.svelte's mobile detection: md breakpoint is 768 px.
  let isMobile = $state(false)
  $effect(() => {
    const mq = window.matchMedia('(max-width: 767px)')
    const update = () => (isMobile = mq.matches)
    update()
    mq.addEventListener('change', update)
    return () => mq.removeEventListener('change', update)
  })

  // On mobile the sidebar is a fixed overlay (not in-flow), so the sheet spans the full width.
  // On desktop, offset by the sidebar width so the sheet stays inside the content column.
  let sheetLeft = $derived(isMobile ? '0px' : layout.collapsed ? '4rem' : '15rem')

  // Observed so a parent can subtract the exact rendered height from its scroll container.
  let sheetEl = $state<HTMLElement | null>(null)
  $effect(() => {
    const el = sheetEl
    if (!el) return
    const observer = new ResizeObserver(([entry]) => {
      height = entry.contentRect.height
    })
    observer.observe(el)
    return () => observer.disconnect()
  })
</script>

<!-- Always in the DOM, slides into view when `open`. `left` is offset by the sidebar width so
     the sheet stays within the content column and never overlaps the nav rail. -->
<div
  class="fixed bottom-0 right-0 z-30"
  style="left: {sheetLeft}; transform: {open ? 'translateY(0)' : 'translateY(100%)'}; transition: transform 0.3s ease-out, left 0.2s ease-out;"
  aria-hidden={!open}
  bind:this={sheetEl}
>
  <div
    class="relative overflow-hidden border-t border-slate-200 bg-white shadow-[0_-4px_24px_rgba(0,0,0,0.1)] dark:border-slate-700 dark:bg-slate-900 dark:shadow-[0_-4px_24px_rgba(0,0,0,0.4)]"
  >
    {#if backdrop}
      <!-- Ambient backdrop behind the entire sheet; pointer-transparent and clipped to the panel. -->
      <div class="pointer-events-none absolute inset-0 z-0">{@render backdrop()}</div>
    {/if}

    <!-- Drag-handle affordance -->
    <div class="relative z-10 flex justify-center pt-2 pb-0.5">
      <div class="h-1 w-10 rounded-full bg-slate-300 dark:bg-slate-600"></div>
    </div>

    <!-- Header: caller content + expand/collapse + close -->
    <div class="relative z-10 flex items-start gap-3 px-5 pt-2 pb-3">
      <div class="min-w-0 flex-1">{@render header()}</div>
      <button
        class="btn btn-ghost flex-shrink-0 px-2 py-1"
        onclick={() => (expanded = !expanded)}
        aria-label={expanded ? 'Collapse detail panel' : 'Expand detail panel'}
        title={expanded ? 'Collapse' : 'Expand'}
      >
        <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d={expanded ? 'M19 9l-7 7-7-7' : 'M5 15l7-7 7 7'} />
        </svg>
      </button>
      <button class="btn btn-ghost flex-shrink-0 px-2 py-1" onclick={onclose} aria-label="Close detail panel">
        <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
          <path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12" />
        </svg>
      </button>
    </div>

    <!-- Content: only rendered when open and expanded; the ResizeObserver picks up the size
         change automatically so a parent's table max-height adjusts without extra logic. -->
    {#if open && expanded}
      <div class="relative z-10 max-h-[60vh] overflow-y-auto border-t border-slate-100 px-5 py-4 dark:border-slate-800">
        {@render children()}
      </div>
    {/if}
  </div>
</div>
