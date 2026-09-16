<script lang="ts">
  // A small inline status banner with a leading icon, shared across pages so error,
  // success, and info messages look the same everywhere. Margins are left to the
  // caller via `class` so it drops into existing layouts unchanged.
  import Icon from './Icon.svelte'
  import type { Snippet } from 'svelte'

  let { kind = 'info', class: className = '', children }: {
    kind?: 'error' | 'success' | 'info'
    class?: string
    children: Snippet
  } = $props()

  const styles = {
    error: { icon: 'warning', tone: 'tone-bad', ink: 'text-bad' },
    success: { icon: 'check', tone: 'tone-ok', ink: 'text-ok' },
    info: { icon: 'info', tone: 'tone-accent', ink: 'text-accent' },
  } as const
</script>

<div class="card flex items-start gap-2 p-3 text-sm {styles[kind].tone} {className}">
  <Icon name={styles[kind].icon} class="mt-0.5 h-4 w-4 flex-shrink-0 {styles[kind].ink}" />
  <span class="min-w-0">{@render children()}</span>
</div>
