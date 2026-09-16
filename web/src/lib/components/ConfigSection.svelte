<script lang="ts">
  import type { Snippet } from 'svelte'

  let {
    step,
    title,
    description,
    id,
    children,
  }: {
    /**
     * Only Setup passes one. There, the sections genuinely are a sequence you work through,
     * and the number tells you where you are in it. Settings is not a sequence — nobody
     * configures their encoder before their notifications because it is numbered lower — so
     * it omits this and the badge disappears.
     */
    step?: number
    title: string
    description: string
    id: string
    children: Snippet
  } = $props()
</script>

<section {id} class="card overflow-hidden" data-config-section={step ?? ''} aria-labelledby={`${id}-heading`}>
  <header class="flex items-start gap-3 px-4 py-4 hairline-b sm:px-6">
    {#if step !== undefined}
      <span
        class="flex h-7 w-7 flex-none items-center justify-center rounded-full tone-accent text-xs font-bold"
        aria-hidden="true"
      >
        {step}
      </span>
    {/if}
    <div class="min-w-0">
      <h2 id={`${id}-heading`} class="text-base font-semibold text-ink">{title}</h2>
      <p class="mt-0.5 max-w-3xl text-sm leading-relaxed text-ink-3">{description}</p>
    </div>
  </header>
  <div class="p-4 sm:p-6">
    {@render children()}
  </div>
</section>
