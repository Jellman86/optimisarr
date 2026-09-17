// Small alpha masks keep soft lighting consistent across Canvas implementations.
// Three separable box passes approximate a Gaussian without a Canvas filter dependency.
export function createSoftMask(makeCanvas: () => HTMLCanvasElement) {
  const size = 64
  const canvas = makeCanvas()
  canvas.width = canvas.height = size
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!
  const alpha = new Float32Array(size * size)
  const scratch = new Float32Array(size * size)

  function pass(source: Float32Array, target: Float32Array, radius: number, vertical: boolean) {
    const span = radius * 2 + 1
    const stride = vertical ? size : 1
    for (let line = 0; line < size; line++) {
      const start = vertical ? line : line * size
      let sum = 0
      for (let i = 0; i <= radius; i++) sum += source[start + i * stride]
      for (let i = 0; i < size; i++) {
        target[start + i * stride] = sum / span
        if (i >= radius) sum -= source[start + (i - radius) * stride]
        if (i + radius + 1 < size) sum += source[start + (i + radius + 1) * stride]
      }
    }
  }

  return {
    draw(target: CanvasRenderingContext2D, source: HTMLCanvasElement, colour: string, blur: number, opacity: number) {
      ctx.clearRect(0, 0, size, size)
      ctx.drawImage(source, 0, 0, size, size)
      const pixels = ctx.getImageData(0, 0, size, size)
      for (let i = 0; i < alpha.length; i++) alpha[i] = pixels.data[i * 4 + 3]
      const radius = Math.max(1, Math.round(blur * size / source.width / 2))
      for (let i = 0; i < 3; i++) {
        pass(alpha, scratch, radius, false)
        pass(scratch, alpha, radius, true)
      }
      for (let i = 0; i < alpha.length; i++) pixels.data[i * 4 + 3] = alpha[i]
      ctx.putImageData(pixels, 0, 0)
      ctx.globalCompositeOperation = 'source-in'
      ctx.fillStyle = colour
      ctx.fillRect(0, 0, size, size)
      ctx.globalCompositeOperation = 'source-over'
      target.save()
      target.globalAlpha = opacity
      target.imageSmoothingEnabled = true
      target.imageSmoothingQuality = 'high'
      target.drawImage(canvas, 0, 0, source.width, source.height)
      target.restore()
    },
    destroy() { canvas.width = canvas.height = 1 },
  }
}
