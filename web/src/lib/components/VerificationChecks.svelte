<script lang="ts">
  // The pass/fail verification gate list (✓/✗ + measured detail), shared by the Queue job
  // detail and the Quarantine compare-to-approve panel so the report reads the same everywhere.
  import type { VerificationCheck } from '../api'

  let { checks }: { checks: VerificationCheck[] } = $props()
</script>

<ul class="space-y-1.5">
  {#each checks as check (check.name)}
    <li class="flex items-start gap-2 text-xs">
      <span class={check.outcome === 'Passed' ? 'text-ok' : 'text-bad'}>
        {check.outcome === 'Passed' ? '✓' : '✗'}
      </span>
      <span class="w-32 shrink-0 font-medium text-ink-2">{check.name}</span>
      <span class="text-ink-3">{check.detail}</span>
    </li>
  {/each}
</ul>
