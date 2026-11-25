<script lang="ts">
  // The bar across the top of an operational card: its name, an optional quiet hint, and the
  // card's own figures or actions pushed to the far edge. It is divided from the card's body by
  // a hairline, never a stroke, so a card stays one surface.
  import type { Snippet } from 'svelte'

  let {
    title,
    id = undefined,
    as = 'span',
    hint = '',
    hintClass = '',
    trailing,
  }: {
    title: string
    /** The heading's id, for the card's `aria-labelledby`. */
    id?: string
    /** A heading element when the card is a landmark section; a plain label otherwise. */
    as?: 'h2' | 'h3' | 'span'
    hint?: string
    /** Extra classes for the hint, e.g. to show it only when the card is wide enough. */
    hintClass?: string
    /** Figures or actions, aligned to the end of the bar. */
    trailing?: Snippet
  } = $props()
</script>

<div class="hairline-b flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5">
  <svelte:element this={as} {id} class="label mb-0 min-w-0">{title}</svelte:element>
  {#if hint}<span class="text-xs text-ink-3 {hintClass}">{hint}</span>{/if}
  {#if trailing}
    <div class="ml-auto flex min-w-0 flex-wrap items-center justify-end gap-1">{@render trailing()}</div>
  {/if}
</div>
