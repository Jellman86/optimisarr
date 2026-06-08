<script lang="ts">
  // A labelled on/off switch for boolean feature settings. Backed by a real
  // checkbox so the whole row is clickable, keyboard-operable, and announced
  // correctly; the visual switch is driven entirely by `peer` variants.
  let {
    checked = $bindable(false),
    label,
    hint = '',
    disabled = false,
  }: {
    checked?: boolean
    label: string
    hint?: string
    disabled?: boolean
  } = $props()
</script>

<label
  class="flex items-center justify-between gap-4 {disabled
    ? 'cursor-not-allowed opacity-60'
    : 'cursor-pointer'}"
>
  <span class="min-w-0">
    <span class="block text-sm font-medium text-slate-700 dark:text-slate-200">{label}</span>
    {#if hint}
      <span class="mt-0.5 block text-xs text-slate-500 dark:text-slate-400">{hint}</span>
    {/if}
  </span>

  <span class="relative inline-flex h-6 w-11 flex-shrink-0 items-center">
    <input type="checkbox" class="peer sr-only" bind:checked {disabled} />
    <span
      class="absolute inset-0 rounded-full bg-slate-300 transition-colors duration-200 peer-checked:bg-emerald-600 peer-focus-visible:ring-2 peer-focus-visible:ring-emerald-500/50 peer-focus-visible:ring-offset-2 peer-focus-visible:ring-offset-white dark:bg-slate-600 dark:peer-focus-visible:ring-offset-slate-950"
    ></span>
    <span
      class="absolute left-0.5 h-5 w-5 rounded-full bg-white shadow-sm transition-transform duration-200 peer-checked:translate-x-5"
    ></span>
  </span>
</label>
