<script lang="ts">
  // A consistent empty/placeholder state: a muted icon, a short title, an optional
  // hint, and an optional action (passed as children) — e.g. an "Add" button.
  import Icon from './Icon.svelte'
  import type { Snippet } from 'svelte'

  let { icon = 'info', title, hint = '', children }: {
    icon?: string
    title: string
    hint?: string
    children?: Snippet
  } = $props()
</script>

<div class="card flex flex-col items-center gap-3 p-10 text-center">
  <Icon name={icon} class="h-10 w-10 text-ink-5" />
  <div>
    <p class="font-medium text-ink-2">{title}</p>
    {#if hint}<p class="mt-1 text-sm text-ink-3">{hint}</p>{/if}
  </div>
  {@render children?.()}
</div>
