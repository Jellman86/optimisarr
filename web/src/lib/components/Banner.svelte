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
    error: { icon: 'warning', cls: 'border-red-300 text-red-700 dark:border-red-800 dark:text-red-400' },
    success: { icon: 'check', cls: 'border-emerald-300 text-emerald-700 dark:border-emerald-800 dark:text-emerald-400' },
    info: { icon: 'info', cls: 'border-cyan-300 text-cyan-700 dark:border-cyan-800 dark:text-cyan-400' },
  } as const
</script>

<div class="card flex items-start gap-2 p-3 text-sm {styles[kind].cls} {className}">
  <Icon name={styles[kind].icon} class="mt-0.5 h-4 w-4 flex-shrink-0" />
  <span class="min-w-0">{@render children()}</span>
</div>
