<script lang="ts">
  import { untrack } from 'svelte'
  import { createBrandPlayer } from '../brand-player'
  import { activity } from '../stores/activity.svelte'
  import { brand } from '../stores/brand.svelte'
  import { brandAsset } from '../brand-style'
  import { theme } from '../stores/ui.svelte'

  let { class: className = 'h-9 w-9' }: { class?: string } = $props()
  let canvas = $state<HTMLCanvasElement>()
  let usable = $state(true)
  let player = $state<ReturnType<typeof createBrandPlayer> | null>(null)

  $effect(() => {
    const style = brand.style
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) { usable = false; return }
    const mounted = createBrandPlayer(canvas, ctx, style)
    untrack(() => mounted.update(activity.brandWorking, theme.isDark))
    player = mounted
    return () => mounted.destroy()
  })
  $effect(() => { player?.update(activity.brandWorking, theme.isDark) })
</script>

{#if usable}
  <canvas bind:this={canvas} width="288" height="288" class="object-contain {className}" aria-hidden="true"></canvas>
{:else}
  <img src={brandAsset(brand.style, theme.isDark, activity.brandWorking, true)} alt="" decoding="async" class="object-contain {className}" />
{/if}
