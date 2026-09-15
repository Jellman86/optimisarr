<script lang="ts">
  // The application mark, drawn rather than fetched: a four-dimensional hypercube on a pixel
  // grid. It wakes when the server has work and settles when the queue goes quiet, so the icon
  // itself reports the one thing you would otherwise open the dashboard to learn.
  //
  // Falls back to the packaged PNG when a canvas is unavailable or the viewer has asked for
  // reduced motion — a still mark is better than a moving one nobody wanted.
  import {
    BASE_RATE,
    CROSS_MS,
    PixelGrid,
    RESTING,
    WORKING,
    crossStates,
    drawTesseract,
    palette,
    toImageBytes,
    type TesseractState,
  } from '../tesseract'
  import { activity } from '../stores/activity.svelte'
  import { theme } from '../stores/ui.svelte'

  let {
    class: className = 'h-9 w-9',
    /**
     * Rasterisation grid. Kept coarse on purpose — this is pixel art, not a render — and
     * matched to the canvas's backing store so the browser scales it by a whole number. A
     * larger grid squeezed into a small box is downsampled, and `pixelated` then discards
     * pixels rather than blending them, which is how a crisp mark turns to mush.
     */
    grid = 32,
  }: { class?: string; grid?: number } = $props()

  let canvas = $state<HTMLCanvasElement | null>(null)
  let usable = $state(true)

  let working = $derived(activity.activeJobs > 0)

  $effect(() => {
    const el = canvas
    if (!el) return
    const ctx = el.getContext('2d')
    if (!ctx) {
      usable = false
      return
    }

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)')
    const g = new PixelGrid(grid)
    const scratch = document.createElement('canvas')
    scratch.width = grid
    scratch.height = grid
    const sctx = scratch.getContext('2d')
    if (!sctx) {
      usable = false
      return
    }
    // Captured after the guards so the closures below see non-null contexts.
    const out = ctx
    const scratchCtx = sctx
    const target = el
    const image = scratchCtx.createImageData(grid, grid)

    let clock = 0
    let cfg: TesseractState = { ...(working ? WORKING : RESTING) }
    let crossFrom: TesseractState | null = null
    let crossStart = 0
    let wasWorking = working
    let last = performance.now()
    let frameHandle = 0

    // A sidebar badge does not need sixty frames a second, and a resting mark takes nearly a
    // minute and a half to turn. Drawing more often than this would burn a core to animate
    // something nobody can see move.
    const FPS = 24
    let nextDraw = 0

    function paint(t: number) {
      g.clear()
      drawTesseract(g, t, cfg, theme.isDark)
      toImageBytes(g, image.data, palette(theme.isDark))
      scratchCtx.putImageData(image, 0, 0)
      const size = target.width
      out.clearRect(0, 0, size, size)
      out.imageSmoothingEnabled = false
      out.drawImage(scratch, 0, 0, size, size)
    }

    function frame(now: number) {
      frameHandle = requestAnimationFrame(frame)
      const dt = Math.min(0.1, (now - last) / 1000)
      last = now

      if (working !== wasWorking) {
        crossFrom = { ...cfg }
        crossStart = now
        wasWorking = working
      }
      const target = working ? WORKING : RESTING
      if (crossFrom) {
        cfg = crossStates(crossFrom, target, now - crossStart)
        if (now - crossStart >= CROSS_MS) crossFrom = null
      } else {
        cfg = target
      }

      // Paused tabs and reduced-motion viewers hold the resting pose rather than burning
      // frames on something that is not being watched.
      const still = reduced.matches || document.hidden
      if (!still) clock = (clock + dt * cfg.speed * BASE_RATE) % 1

      if (now < nextDraw && !crossFrom) return
      nextDraw = now + 1000 / FPS
      paint(still ? 0 : clock)
    }

    frameHandle = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(frameHandle)
  })
</script>

<!-- Decorative in both branches: every placement sits beside the application's name, so the
     mark repeating it would only make a screen reader say "Optimisarr" twice. Where the name
     is not on screen — the collapsed rail — the button around it carries the label. -->
{#if usable}
  <canvas
    bind:this={canvas}
    width={grid}
    height={grid}
    class="object-contain {className}"
    style="image-rendering: pixelated"
    aria-hidden="true"
  ></canvas>
{:else}
  <img src="/favicon-192.png" alt="" decoding="async" class="object-contain {className}" />
{/if}
