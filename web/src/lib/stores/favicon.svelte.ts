// The tab icon, drawn from the same geometry as the sidebar mark.
//
// The point is that a background tab reports the server's state: the mark turns while work is
// running and settles when the queue goes quiet, so "is it still going?" is answerable without
// switching to the tab at all.
//
// Two things keep this from being wasteful. A hidden tab is not redrawn, because nobody can see
// it — and the browser throttles the timer anyway. A resting mark takes nearly a minute and a
// half to turn, so it is redrawn a few times a second rather than sixty.
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
import { activity } from './activity.svelte'
import { theme } from './ui.svelte'

// Favicons are rendered at 16 or 32 CSS pixels. Drawing the grid at 32 keeps it honest pixel
// art at both, and below 28 the renderer drops to the outline and core on its own.
const GRID = 32
const WORKING_FPS = 12
const RESTING_FPS = 2

function createFavicon() {
  let started = false

  function start() {
    if (started || typeof document === 'undefined') return
    started = true

    const link =
      document.querySelector<HTMLLinkElement>('link[rel~="icon"]') ??
      document.head.appendChild(Object.assign(document.createElement('link'), { rel: 'icon' }))
    const original = link.href

    const canvas = document.createElement('canvas')
    canvas.width = GRID
    canvas.height = GRID
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const grid = new PixelGrid(GRID)
    const image = ctx.createImageData(GRID, GRID)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)')

    let clock = 0
    let cfg: TesseractState = { ...RESTING }
    let crossFrom: TesseractState | null = null
    let crossStart = 0
    let wasWorking = false
    let last = performance.now()
    let nextDraw = 0
    let handle = 0

    function draw(t: number) {
      grid.clear()
      drawTesseract(grid, t, cfg, theme.isDark)
      toImageBytes(grid, image.data, palette(theme.isDark))
      ctx!.putImageData(image, 0, 0)
      try {
        link.href = canvas.toDataURL('image/png')
      } catch {
        // A tainted or unsupported canvas leaves the packaged icon in place.
        link.href = original
      }
    }

    function frame(now: number) {
      handle = requestAnimationFrame(frame)
      const dt = Math.min(0.25, (now - last) / 1000)
      last = now

      const working = activity.activeJobs > 0
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

      if (document.hidden) return
      if (reduced.matches) {
        // One write, then nothing: the resting pose, held.
        if (nextDraw !== Infinity) {
          draw(0)
          nextDraw = Infinity
        }
        return
      }

      clock = (clock + dt * cfg.speed * BASE_RATE) % 1
      if (now < nextDraw) return
      nextDraw = now + 1000 / (working || crossFrom ? WORKING_FPS : RESTING_FPS)
      draw(clock)
    }

    handle = requestAnimationFrame(frame)
    // Redraw promptly when the tab comes back, rather than at the next resting tick.
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden) nextDraw = 0
    })
    return () => cancelAnimationFrame(handle)
  }

  return { start }
}

export const favicon = createFavicon()
