<script lang="ts">
  // The pass/fail verification gate list (✓/✗ + measured detail), shared by the Queue job
  // detail and the Quarantine compare-to-approve panel so the report reads the same everywhere.
  import type { VerificationCheck } from '../api'

  let { checks }: { checks: VerificationCheck[] } = $props()
</script>

<ul class="space-y-1.5">
  {#each checks as check (check.name)}
    <li class="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-x-2 gap-y-1 text-xs sm:grid-cols-[auto_minmax(0,8rem)_minmax(0,1fr)]">
      <span class={check.outcome === 'Passed' ? 'text-ok' : 'text-bad'}>
        {check.outcome === 'Passed' ? '✓' : '✗'}
      </span>
      <span class="break-words font-medium text-ink-2">{check.name}</span>
      <span class="col-start-2 break-words text-ink-3 sm:col-start-3">{check.detail}</span>
    </li>
  {/each}
</ul>
