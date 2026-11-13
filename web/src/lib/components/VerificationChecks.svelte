<script lang="ts">
  // The pass/fail verification gate list (✓/✗ + measured detail), shared by the Queue job
  // detail and the Quarantine compare-to-approve panel so the report reads the same everywhere.
  // Each check is named in plain words; the name the code uses stays as a tooltip, because it is
  // the one a log or a bug report will quote.
  import type { VerificationCheck } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import { plainCheckName } from '../plain-checks'

  let { checks }: { checks: VerificationCheck[] } = $props()
</script>

<ul class="space-y-1.5">
  {#each checks as check (check.name)}
    <li class="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-x-2 gap-y-1 text-xs sm:grid-cols-[auto_minmax(0,12rem)_minmax(0,1fr)]">
      <span class={check.outcome === 'Passed' ? 'text-ok' : 'text-bad'}>
        {check.outcome === 'Passed' ? '✓' : '✗'}
      </span>
      <span class="break-words font-medium text-ink-2" title={check.name} data-check-name={check.name}>{plainCheckName(check.name, i18n.m.checkNames)}</span>
      <span class="col-start-2 break-words text-ink-3 sm:col-start-3">{check.detail}</span>
    </li>
  {/each}
</ul>
