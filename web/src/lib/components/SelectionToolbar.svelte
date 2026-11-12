<script lang="ts">
  // The bar that appears once rows are selected. It says what is selected, carries the actions
  // that apply to the selection, and offers the way back to no selection. The Queue table and
  // the candidate table share it so both handle a bulk action the same way.
  import type { Snippet } from 'svelte'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { MAX_SELECTION } from '../selection'
  import Icon from './Icon.svelte'

  let {
    count,
    total = null,
    limited = false,
    onclear,
    actions,
    variant = 'card',
  }: {
    /** How many rows are selected. */
    count: number
    /** How many rows the list holds, so the operator can see the share that is selected. */
    total?: number | null
    /** True once the selection has reached the cap one action can carry. */
    limited?: boolean
    onclear: () => void
    /** The actions that apply to the selection. */
    actions?: Snippet
    /**
     * How the bar sits in its surroundings. `card` floats above the list it belongs to;
     * `inline` fills the row it shares with the list's own filters, on the dark slab that
     * says the list is now under a bulk action.
     */
    variant?: 'card' | 'inline'
  } = $props()
</script>

<div class="selection-toolbar card" class:selection-toolbar-inline={variant === 'inline'} data-selection-toolbar>
  <span class="selection-count" aria-live="polite">
    {#if total !== null && total !== count}
      {t(i18n.m.selection.selected_of, { count: count.toLocaleString(), total: total.toLocaleString() })}
    {:else}
      {t(i18n.m.selection.selected, { count: count.toLocaleString() })}
    {/if}
  </span>
  {#if limited}
    <span class="selection-limit">{t(i18n.m.selection.limit, { max: MAX_SELECTION.toLocaleString() })}</span>
  {/if}
  {#if actions}
    <div class="selection-actions" aria-label={i18n.m.selection.actions}>{@render actions()}</div>
  {/if}
  <button type="button" class="btn btn-ghost selection-clear" onclick={onclear}>
    <Icon name="x" class="h-4 w-4" />
    {i18n.m.selection.clear}
  </button>
</div>

<style>
  .selection-toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem .75rem; margin-bottom: .75rem; padding: .625rem .875rem; }
  .selection-count { font-size: .8125rem; font-weight: 600; color: var(--ink); white-space: nowrap; }
  .selection-limit { font-size: .6875rem; color: var(--warn); }
  .selection-actions { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem; margin-left: auto; }
  .selection-actions :global(.btn) { font-size: .75rem; }
  .selection-clear { flex: none; font-size: .75rem; }
  @media(max-width: 639px) { .selection-actions { margin-left: 0; width: 100%; } }

  /* The inline bar takes the row the list's filters would otherwise have, so it is a dark
     slab rather than a card: the change of mode is the point, and it has to be readable in
     both schemes. The inks come from the tip tokens, which exist for exactly this surface.
     The buttons carry no fill of their own: a light fill on a dark slab would put dark ink on
     a light ground, and the destructive one has to keep its red on the slab to stay legible. */
  .selection-toolbar-inline { width: 100%; margin-bottom: 0; padding: .375rem .5rem .375rem 1rem; border-radius: 999px; background: var(--tip); color: var(--tip-ink); box-shadow: none; }
  .selection-toolbar-inline .selection-count { color: var(--tip-ink); }
  .selection-toolbar-inline .selection-limit { color: var(--tip-ink); opacity: .8; }
  .selection-toolbar-inline .selection-actions :global(.btn),
  .selection-toolbar-inline .selection-clear { color: var(--tip-ink); background: none; box-shadow: none; }
  .selection-toolbar-inline .selection-actions :global(.btn:hover),
  .selection-toolbar-inline .selection-clear:hover { color: var(--tip-ink); background: color-mix(in srgb, var(--tip-ink) 14%, transparent); }
  .selection-toolbar-inline .selection-actions :global(.btn-danger) { color: var(--tip-bad); }
  .selection-toolbar-inline .selection-actions :global(.btn-danger:hover) { color: var(--tip-bad); background: color-mix(in srgb, var(--tip-bad) 18%, transparent); }
  @media(max-width: 639px) { .selection-toolbar-inline { border-radius: .875rem; padding: .625rem .75rem; } }
</style>
