<script lang="ts">
  import type { Snippet } from 'svelte'

  let {
    step,
    title,
    description,
    id,
    children,
  }: {
    step: number
    title: string
    description: string
    id: string
    children: Snippet
  } = $props()
</script>

<section {id} class="card overflow-hidden" data-config-section={step} aria-labelledby={`${id}-heading`}>
  <header class="flex items-start gap-3 border-b border-slate-200 bg-slate-50/70 px-4 py-4 dark:border-slate-700 dark:bg-slate-800/35 sm:px-6">
    <span
      class="flex h-7 w-7 flex-none items-center justify-center rounded-full bg-cyan-100 text-xs font-bold text-cyan-700 dark:bg-cyan-950 dark:text-cyan-300"
      aria-hidden="true"
    >
      {step}
    </span>
    <div class="min-w-0">
      <h2 id={`${id}-heading`} class="text-base font-semibold text-slate-900 dark:text-slate-100">{title}</h2>
      <p class="mt-0.5 max-w-3xl text-sm leading-relaxed text-slate-500 dark:text-slate-400">{description}</p>
    </div>
  </header>
  <div class="p-4 sm:p-6">
    {@render children()}
  </div>
</section>
