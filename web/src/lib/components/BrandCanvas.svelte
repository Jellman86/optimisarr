<script lang="ts">
  // The animated mark itself, told what to show rather than reading the app's stores, so the
  // sidecar page can draw the server's chosen mark from its own status.
  import { untrack } from 'svelte'
  import { createBrandPlayer } from '../brand-player'
  import { brandAsset, type BrandStyle } from '../brand-style'

  let { style, working, dark, class: className = 'h-9 w-9' }: { style: BrandStyle; working: boolean; dark: boolean; class?: string } = $props()
  let canvas = $state<HTMLCanvasElement>()
  let usable = $state(true)
  let player = $state<ReturnType<typeof createBrandPlayer> | null>(null)

  $effect(() => {
    const chosen = style
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) { usable = false; return }
    const mounted = createBrandPlayer(canvas, ctx, chosen)
    untrack(() => mounted.update(working, dark))
    player = mounted
    return () => mounted.destroy()
  })
  $effect(() => { player?.update(working, dark) })
</script>

{#if usable}
  <canvas bind:this={canvas} width="288" height="288" class="object-contain {className}" aria-hidden="true"></canvas>
{:else}
  <img src={brandAsset(style, dark, working, true)} alt="" decoding="async" class="object-contain {className}" />
{/if}
