import type { Page } from '@playwright/test'

export type QualityCheckMedia = { Video: Buffer, Audio: Buffer, Image: Buffer }

// All variants deliberately use the same generated media. This is UI evidence, not a codec test.
export async function fabricatedQualityMedia(page: Page): Promise<QualityCheckMedia> {
  const rendered = await page.evaluate(async () => {
    const canvas = document.createElement('canvas')
    canvas.width = 960; canvas.height = 540
    const ctx = canvas.getContext('2d')!
    const draw = () => {
      const sky = ctx.createLinearGradient(0, 0, 0, 540)
      sky.addColorStop(0, '#305d79'); sky.addColorStop(1, '#e1c99b')
      ctx.fillStyle = sky; ctx.fillRect(0, 0, 960, 540)
      for (let layer = 0; layer < 3; layer++) {
        ctx.fillStyle = ['#7b9a9e', '#426c7d', '#234e66'][layer]
        ctx.beginPath(); ctx.moveTo(0, 540)
        for (let x = 0; x <= 960; x += 20) ctx.lineTo(x, 280 + layer * 55 + Math.sin(x / 100 + layer) * 55)
        ctx.lineTo(960, 540); ctx.fill()
      }
      ctx.fillStyle = '#fff4da'; ctx.font = '20px sans-serif'
      ctx.fillText('Lumen landscape · generated UI fixture', 30, 505)
    }
    draw()
    const image = canvas.toDataURL('image/png').split(',')[1]
    const chunks: BlobPart[] = []
    const stream = canvas.captureStream(12)
    const recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp8' })
    const video = new Promise<string>((resolve, reject) => {
      recorder.onerror = () => reject(new Error('Generated fixture recording failed'))
      recorder.ondataavailable = event => chunks.push(event.data)
      recorder.onstop = async () => {
        const bytes = new Uint8Array(await new Blob(chunks).arrayBuffer())
        let binary = ''
        for (const byte of bytes) binary += String.fromCharCode(byte)
        resolve(btoa(binary))
      }
    })
    recorder.start()
    const timer = setInterval(draw, 1000 / 12)
    await new Promise(resolve => setTimeout(resolve, 12_000))
    clearInterval(timer); recorder.stop()
    const result = await video
    stream.getTracks().forEach(track => track.stop())
    return { image, video: result }
  })
  const rate = 16000, samples = rate * 12
  const audio = Buffer.alloc(44 + samples * 2)
  audio.write('RIFF'); audio.writeUInt32LE(audio.length - 8, 4); audio.write('WAVEfmt ', 8)
  audio.writeUInt32LE(16, 16); audio.writeUInt16LE(1, 20); audio.writeUInt16LE(1, 22)
  audio.writeUInt32LE(rate, 24); audio.writeUInt32LE(rate * 2, 28)
  audio.writeUInt16LE(2, 32); audio.writeUInt16LE(16, 34); audio.write('data', 36)
  audio.writeUInt32LE(samples * 2, 40)
  for (let index = 0; index < samples; index++) audio.writeInt16LE(Math.round(1600 * Math.sin(index * 2 * Math.PI * 440 / rate)), 44 + index * 2)
  return { Video: Buffer.from(rendered.video, 'base64'), Audio: audio, Image: Buffer.from(rendered.image, 'base64') }
}

export async function serveQualityMedia(page: Page, kind: keyof QualityCheckMedia, media: QualityCheckMedia) {
  const contentType = kind === 'Video' ? 'video/webm' : kind === 'Audio' ? 'audio/wav' : 'image/png'
  await page.route('**/api/calibration/*/variants/*/samples/*/content', route => route.fulfill({
    contentType, body: media[kind],
  }))
}
