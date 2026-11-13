// Which encoder tuning settings the encoder this server uses will honour, so the library editor
// can keep a setting in view and say why it does nothing. Mirrors EncoderTuningPolicy on the
// server, which passes each encoder only what it understands.
export type EncoderFamily = 'x26x' | 'svtav1' | 'nvenc' | 'qsv' | 'vaapi'
export type EncoderSetting = 'contentTune' | 'maxBitrate' | 'minBitrate' | 'adaptiveQuantisation'

const ignored: Record<EncoderFamily, readonly EncoderSetting[]> = {
  x26x: [],
  svtav1: ['contentTune', 'maxBitrate', 'minBitrate'],
  nvenc: ['contentTune', 'minBitrate'],
  qsv: ['contentTune', 'maxBitrate', 'minBitrate', 'adaptiveQuantisation'],
  vaapi: ['contentTune', 'maxBitrate', 'minBitrate', 'adaptiveQuantisation'],
}

const names: Record<EncoderFamily, string> = {
  x26x: 'x264/x265', svtav1: 'SVT-AV1', nvenc: 'NVIDIA NVENC', qsv: 'Intel Quick Sync', vaapi: 'VA-API',
}

/**
 * The encoder every job in a library will use, or null when that is decided per job: in Auto
 * mode, or while remote workers may take the work with an encoder of their own.
 */
export function encoderFamily(encoderMode: string | null | undefined, codec: string | null | undefined, remoteWorkers: boolean): EncoderFamily | null {
  if (remoteWorkers) return null
  switch (encoderMode) {
    case 'Cpu': return codec?.toLowerCase() === 'av1' ? 'svtav1' : 'x26x'
    case 'NvidiaNvenc': return 'nvenc'
    case 'IntelQsv': return 'qsv'
    case 'Vaapi': return 'vaapi'
    default: return null
  }
}

export function encoderIgnores(family: EncoderFamily | null, setting: EncoderSetting): boolean {
  return family !== null && ignored[family].includes(setting)
}

export function encoderName(family: EncoderFamily): string {
  return names[family]
}
