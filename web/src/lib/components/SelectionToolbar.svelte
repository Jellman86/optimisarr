<script lang="ts">
  // The bar that appears above a list once rows are selected. It says what is selected, carries
  // the actions that apply to the selection, and offers the way back to no selection. The Queue
  // table and the candidate table share it so both handle a bulk action the same way.
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
  } = $props()
</script>

<div class="selection-toolbar card" data-selection-toolbar>
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
</style>
