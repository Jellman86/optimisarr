<script lang="ts">
  import { onMount } from 'svelte'
  import { createBrandPlayer } from '../brand-player'
  import { activity } from '../stores/activity.svelte'
  import { theme } from '../stores/ui.svelte'

  let { class: className = 'h-9 w-9' }: { class?: string } = $props()
  let canvas = $state<HTMLCanvasElement>()
  let usable = $state(true)
  let player = $state<ReturnType<typeof createBrandPlayer> | null>(null)

  onMount(() => {
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) { usable = false; return }
    const mounted = createBrandPlayer(canvas, ctx)
    player = mounted
    return () => mounted.destroy()
  })
  $effect(() => { player?.update(activity.brandWorking, theme.isDark) })
</script>

{#if usable}
  <canvas bind:this={canvas} width="288" height="288" class="object-contain {className}" aria-hidden="true"></canvas>
{:else}
  <img src="/favicon-192.png" alt="" decoding="async" class="object-contain {className}" />
{/if}
