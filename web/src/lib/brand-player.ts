import { createBrandMotion } from './brand-motion'
import type { createBrandRenderer } from './brand-renderer'

const images = new Map<string, Promise<HTMLImageElement>>()
function loadImage(path: string) {
  let promise = images.get(path)
  if (!promise) {
    const image = new Image()
    image.src = path
    promise = image
      .decode()
      .then(() => image)
      .catch((error) => {
        images.delete(path)
        throw error
      })
    images.set(path, promise)
  }
  return promise
}

export function createBrandPlayer(canvas: HTMLCanvasElement, ctx: CanvasRenderingContext2D) {
  const reduced = matchMedia('(prefers-reduced-motion: reduce)')
  const motion = createBrandMotion()
  let renderer: ReturnType<typeof createBrandRenderer> | undefined
  let working = false,
    dark = true,
    visible = false,
    disposed = false,
    graphicsFailed = false
  let size = 0,
    lastPaint = 0,
    timer = 0,
    pending = 0
  let preparing = false
  let still: HTMLImageElement | undefined
  let stillPath = ''

  function stop() {
    window.clearTimeout(timer)
    timer = 0
    lastPaint = 0
    canvas.dataset.lightMotion = 'still'
  }
  function available() {
    return !disposed && visible && !document.hidden && size > 0
  }
  function paint() {
    window.clearTimeout(timer)
    timer = 0
    if (!available()) return
    canvas.dataset.lightState = working ? 'excited' : 'steady'
    let source: CanvasImageSource | undefined = still
    if (renderer && !reduced.matches) {
      const now = performance.now()
      let elapsed = lastPaint ? Math.min(0.2, (now - lastPaint) / 1000) : 0
      lastPaint = now
      // Fixed upper step size keeps the spring response consistent across frame rates.
      motion.step(0, working)
      while (elapsed > 0) {
        const dt = Math.min(1 / 120, elapsed)
        motion.step(dt, working)
        elapsed -= dt
      }
      try {
        source = renderer.render(motion, dark, size)
      } catch {
        renderer.destroy()
        renderer = undefined
        graphicsFailed = true
        void prepare()
      }
    }
    if (!source) return
    ctx.clearRect(0, 0, size, size)
    ctx.imageSmoothingEnabled = true
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(source, 0, 0, size, size)
    const playing = Boolean(renderer) && !reduced.matches
    canvas.dataset.lightMotion = playing ? 'playing' : 'still'
    if (playing) timer = window.setTimeout(paint, 1000 / (working || motion.mode !== 'rest' ? 30 : 24))
  }

  async function prepare() {
    if (!available()) return
    if (renderer && !reduced.matches) {
      paint()
      return
    }
    const path = `/brand/${dark ? 'dark' : 'light'}-${working ? 'excited' : 'steady'}.webp`
    const request = ++pending
    if (stillPath !== path) {
      let nextStill: HTMLImageElement
      try {
        nextStill = await loadImage(path)
      } catch {
        try {
          nextStill = await loadImage('/favicon-192.png')
        } catch {
          return
        }
      }
      if (disposed || request !== pending) return
      still = nextStill
      stillPath = path
    }
    paint()
    if (reduced.matches || renderer || graphicsFailed || preparing || !available()) return
    preparing = true
    try {
      const { createBrandRenderer } = await import('./brand-renderer')
      if (!available() || reduced.matches) return
      renderer = createBrandRenderer()
      // State/theme changes retain this renderer and its original fifteen meshes.
      paint()
    } catch {
      graphicsFailed = true
      paint()
    } finally {
      preparing = false
    }
  }

  function resize() {
    const box = canvas.getBoundingClientRect()
    const nextSize = Math.min(288, Math.max(0, Math.ceil(Math.min(box.width, box.height) * 2)))
    if (size === nextSize) return
    size = nextSize
    canvas.width = canvas.height = size || 1
    void prepare()
  }
  const resizeObserver = new ResizeObserver(resize)
  resizeObserver.observe(canvas)
  const intersection = new IntersectionObserver((entries) => {
    visible = entries[0].isIntersecting
    if (visible) {
      resize()
      void prepare()
    } else stop()
  })
  intersection.observe(canvas)
  function visibilityChanged() {
    if (document.hidden) stop()
    else void prepare()
  }
  function preferenceChanged() {
    stop()
    if (reduced.matches) {
      renderer?.destroy()
      renderer = undefined
    }
    void prepare()
  }
  document.addEventListener('visibilitychange', visibilityChanged)
  reduced.addEventListener('change', preferenceChanged)
  return {
    update(nextWorking: boolean, nextDark: boolean) {
      if (working === nextWorking && dark === nextDark && still) return
      working = nextWorking
      dark = nextDark
      void prepare()
    },
    destroy() {
      disposed = true
      pending++
      stop()
      renderer?.destroy()
      resizeObserver.disconnect()
      intersection.disconnect()
      document.removeEventListener('visibilitychange', visibilityChanged)
      reduced.removeEventListener('change', preferenceChanged)
    },
  }
}
