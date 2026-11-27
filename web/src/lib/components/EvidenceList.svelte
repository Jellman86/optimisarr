<script lang="ts" module>
  export type Evidence = {
    /** Unique within the list; also the name a log or bug report quotes, kept as a tooltip. */
    id: string
    passed: boolean
    /** The proof in plain words. */
    label: string
    /** What was measured, when there is anything to say. */
    detail?: string | null
  }
</script>

<script lang="ts">
  // The one way proof is shown: a row per check at body size, a filled ok mark or a cross that
  // says passed or failed, the check in plain words and what was measured beside it. Proof is
  // the reason anything is replaced, so it is never the smallest thing on the page.
  import { i18n } from '../i18n/i18n.svelte'
  import Icon from './Icon.svelte'
  import OkMark from './OkMark.svelte'

  let { items }: { items: Evidence[] } = $props()
</script>

<ul class="min-w-0 divide-y divide-line-soft text-body" data-evidence-list>
  {#each items as item, index (`${index}:${item.id}`)}
    <li class="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-x-3 gap-y-0.5 py-2.5 first:pt-0 last:pb-0 sm:grid-cols-[auto_minmax(0,14rem)_minmax(0,1fr)]"
      data-evidence={item.passed ? 'passed' : 'failed'}>
      <span class="flex h-5 items-center">
        {#if item.passed}<OkMark />{:else}<Icon name="x" class="h-3.5 w-3.5 flex-none text-bad" />{/if}
        <span class="sr-only">{item.passed ? i18n.m.queue.verify_passed : i18n.m.queue.verify_failed}</span>
      </span>
      <span class="break-words font-semibold text-ink" title={item.id} data-check-name={item.id}>{item.label}</span>
      {#if item.detail}<span class="col-start-2 break-words tabular-nums text-ink-2 sm:col-start-3">{item.detail}</span>{/if}
    </li>
  {/each}
</ul>
