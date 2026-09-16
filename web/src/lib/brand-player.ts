// The expensive light transport is baked offline. All visible marks share decoded WebP assets;
// the runtime only copies a tile, at 24 fps, while real work is happening and motion is welcome.
const FRAMES = 48
const COLUMNS = 8
const FRAME_MS = 1000 / 24
const FADE_MS = 450
const images = new Map<string, Promise<HTMLImageElement>>()

function loadImage(path: string) {
  let promise = images.get(path)
  if (!promise) {
    const image = new Image()
    image.src = path
    promise = image.decode().then(() => image).catch(error => {
      images.delete(path)
      throw error
    })
    images.set(path, promise)
  }
  return promise
}

export function createBrandPlayer(canvas: HTMLCanvasElement, ctx: CanvasRenderingContext2D) {
  const reduced = matchMedia('(prefers-reduced-motion: reduce)')
  const snapshot = document.createElement('canvas')
  let working = false
  let dark = true
  let visible = false
  let disposed = false
  let size = 0
  let frame = 0
  let timer = 0
  let fadeStart = 0
  let fading = false
  let pending = 0
  let still: HTMLImageElement | undefined
  let atlas: HTMLImageElement | undefined
  let atlasSize = 0
  let loadedState = ''

  function stop() {
    window.clearTimeout(timer)
    timer = 0
  }

  function paint() {
    stop()
    if (disposed || !visible || document.hidden || !size || !still) return
    const playing = working && !reduced.matches && Boolean(atlas)
    canvas.dataset.lightMotion = playing ? 'playing' : 'still'
    canvas.dataset.lightState = working ? 'excited' : 'steady'
    ctx.clearRect(0, 0, size, size)
    ctx.imageSmoothingEnabled = true
    ctx.imageSmoothingQuality = 'high'
    // The same spectral field needs more contrast against the light theme's pale surfaces.
    ctx.filter = dark ? 'none' : 'brightness(0.64) saturate(1.4)'
    if (playing && atlas) {
      const first = Math.floor(frame)
      const mix = frame - first
      ctx.globalAlpha = 1 - mix
      ctx.drawImage(atlas, (first % COLUMNS) * atlasSize, Math.floor(first / COLUMNS) * atlasSize,
        atlasSize, atlasSize, 0, 0, size, size)
      // Interpolation gives 24 display frames from 8 baked poses per second. Add premultiplied
      // contributions so overlapping transparent shafts retain their original brightness.
      const second = (first + 1) % FRAMES
      ctx.globalAlpha = mix
      ctx.globalCompositeOperation = 'lighter'
      ctx.drawImage(atlas, (second % COLUMNS) * atlasSize, Math.floor(second / COLUMNS) * atlasSize,
        atlasSize, atlasSize, 0, 0, size, size)
      ctx.globalCompositeOperation = 'source-over'
      ctx.globalAlpha = 1
      frame = (frame + 1 / 3) % FRAMES
    } else {
      const inset = size <= 80 ? still.width * 60 / 576 : 0
      ctx.drawImage(still, inset, inset, still.width - inset * 2, still.height - inset * 2, 0, 0, size, size)
    }
    ctx.filter = 'none'
    if (!dark) {
      const radius = size / 144
      const core = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, radius)
      core.addColorStop(0, 'rgba(255,255,255,1)')
      core.addColorStop(1, 'rgba(255,255,255,0)')
      ctx.fillStyle = core
      ctx.fillRect(size / 2 - radius, size / 2 - radius, radius * 2, radius * 2)
    }
    if (fading) {
      const remaining = 1 - (performance.now() - fadeStart) / FADE_MS
      if (remaining > 0 && !reduced.matches) {
        ctx.globalAlpha = remaining
        ctx.drawImage(snapshot, 0, 0, size, size)
        ctx.globalAlpha = 1
      } else fading = false
    }
    if (playing || fading) timer = window.setTimeout(paint, FRAME_MS)
  }

  async function prepare() {
    if (disposed || !visible || document.hidden || !size) return
    const desiredAtlasSize = size > 80 ? 288 : 80
    const animated = working && !reduced.matches
    const key = `${working}:${animated}:${desiredAtlasSize}`
    if (loadedState === key) { paint(); return }
    const request = ++pending
    try {
      const nextStill = await loadImage(`/brand/${working ? 'excited' : 'steady'}.webp`)
      if (disposed || request !== pending) return
      still = nextStill
      atlas = undefined
      // Show a complete icon while the animation loads. Idle/reduced-motion never fetch an atlas.
      paint()
      const nextAtlas = animated ? await loadImage(`/brand/active${desiredAtlasSize === 80 ? '-small' : ''}.webp`) : undefined
      if (disposed || request !== pending) return
      atlas = nextAtlas
      atlasSize = desiredAtlasSize
      loadedState = key
      paint()
    } catch {
      if (disposed || request !== pending) return
      // A blocked/missing animation is a still icon, never a broken UI or a retry loop.
      if (!still) {
        try { still = await loadImage('/favicon-192.png') } catch { return }
      }
      if (disposed || request !== pending) return
      atlas = undefined
      fading = false
      loadedState = key
      paint()
    }
  }

  function capture() {
    if (!still || reduced.matches || !visible || document.hidden) return
    snapshot.width = snapshot.height = size
    snapshot.getContext('2d')?.drawImage(canvas, 0, 0)
    fadeStart = performance.now()
    fading = true
  }

  function resize() {
    const box = canvas.getBoundingClientRect()
    // 2x even on a 1x screen; both exported sizes were themselves supersampled at bake time.
    const nextSize = Math.min(288, Math.max(0, Math.ceil(Math.min(box.width, box.height) * 2)))
    if (size === nextSize) return
    size = nextSize
    canvas.width = canvas.height = size || 1
    fading = false
    void prepare()
  }
  const resizeObserver = new ResizeObserver(resize)
  resizeObserver.observe(canvas)
  const intersection = new IntersectionObserver(entries => {
    visible = entries[0].isIntersecting
    if (visible) { resize(); void prepare() } else { stop(); canvas.dataset.lightMotion = 'still' }
  })
  intersection.observe(canvas)
  function visibilityChanged() {
    if (document.hidden) { stop(); canvas.dataset.lightMotion = 'still' }
    else void prepare()
  }
  function preferenceChanged() {
    stop()
    fading = false
    if (reduced.matches) canvas.dataset.lightMotion = 'still'
    void prepare()
  }
  document.addEventListener('visibilitychange', visibilityChanged)
  reduced.addEventListener('change', preferenceChanged)

  return {
    update(nextWorking: boolean, nextDark: boolean) {
      if (working === nextWorking && dark === nextDark && still) return
      if (working !== nextWorking) capture()
      working = nextWorking
      dark = nextDark
      stop()
      void prepare()
    },
    destroy() {
      disposed = true
      pending++
      stop()
      resizeObserver.disconnect()
      intersection.disconnect()
      document.removeEventListener('visibilitychange', visibilityChanged)
      reduced.removeEventListener('change', preferenceChanged)
    },
  }
}
