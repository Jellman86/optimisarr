<script lang="ts">
  // What a bulk action actually did. A batch reports per row, so the operator is told how many
  // rows were handled and, for the rest, why each one was refused — a silent partial success is
  // the one outcome an operator must never be left to guess about.
  import { i18n, t } from '../i18n/i18n.svelte'
  import type { BatchFailure } from '../api'
  import Icon from './Icon.svelte'

  let {
    succeeded,
    failures,
    skipped = 0,
    title = null,
    ondismiss,
  }: {
    /** How many rows the action handled. */
    succeeded: number
    /** The rows it could not handle, and why. */
    failures: BatchFailure[]
    /** How many rows the action passed over, e.g. files that are no longer there. */
    skipped?: number
    /** What the action was, so the feedback names it. */
    title?: string | null
    ondismiss: () => void
  } = $props()

  let allFailed = $derived(succeeded === 0 && failures.length > 0)
</script>

<div class="card batch-result {allFailed ? 'tone-bad' : failures.length > 0 || skipped > 0 ? 'tone-warn' : 'tone-ok'}" role="status" data-batch-result>
  <span class="batch-result-icon">
    <Icon name={failures.length > 0 || skipped > 0 ? 'warning' : 'check'} class="h-4 w-4" />
  </span>
  <div class="batch-result-body">
    <p class="batch-result-title">
      {#if title}{title} — {/if}
      {t(i18n.m.selection.result_succeeded, { count: succeeded.toLocaleString() })}
    </p>
    {#if skipped > 0}
      <p class="batch-result-failures">{t(i18n.m.selection.result_skipped, { count: skipped.toLocaleString() })}</p>
    {/if}
    {#if failures.length > 0}
      <p class="batch-result-failures">{t(i18n.m.selection.result_failed, { count: failures.length.toLocaleString() })}</p>
      <ul class="batch-result-list">
        {#each failures as failure (failure.id)}
          <li>
            <span class="batch-result-id">#{failure.id}</span>
            <span class="batch-result-reason">{failure.message}</span>
          </li>
        {/each}
      </ul>
    {/if}
  </div>
  <button type="button" class="btn btn-ghost batch-result-dismiss" onclick={ondismiss}>
    {i18n.m.selection.result_dismiss}
  </button>
</div>

<style>
  .batch-result { display: flex; align-items: flex-start; gap: .625rem; margin-bottom: .75rem; padding: .75rem .875rem; font-size: .8125rem; }
  .batch-result-icon { flex: none; margin-top: .125rem; }
  .batch-result-body { min-width: 0; flex: 1; }
  .batch-result-title { color: var(--ink); font-weight: 600; }
  .batch-result-failures { margin-top: .125rem; color: var(--ink-2); font-size: .75rem; }
  .batch-result-list { margin: .375rem 0 0; padding: 0; list-style: none; display: grid; gap: .25rem; }
  .batch-result-list li { display: flex; gap: .375rem; min-width: 0; font-size: .6875rem; color: var(--ink-3); }
  .batch-result-id { flex: none; font-family: ui-monospace, monospace; color: var(--ink-4); }
  .batch-result-reason { min-width: 0; overflow-wrap: anywhere; }
  .batch-result-dismiss { flex: none; font-size: .75rem; }
  @media(max-width: 639px) { .batch-result { flex-wrap: wrap; } .batch-result-dismiss { margin-left: auto; } }
</style>
