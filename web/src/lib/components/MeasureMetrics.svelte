<script lang="ts">
  // The measured figures of a report, each in a recessed well. Quantities are set in the
  // interface face with tabular numerals: monospace is kept for machine strings, never numbers.
  let { metrics }: { metrics: { label: string; value: string }[] } = $props()
</script>

<dl class="mt-4 grid min-w-0 grid-cols-1 gap-3 sm:grid-cols-2">
  {#each metrics as metric (metric.label)}
    <div class="surface-sunken min-w-0 p-3" data-metric>
      <dt class="text-caption text-ink-3">{metric.label}</dt>
      <dd class="mt-1 text-title font-semibold tabular-nums text-ink">{metric.value}</dd>
    </div>
  {/each}
</dl>
