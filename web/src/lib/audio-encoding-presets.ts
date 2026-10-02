export type AudioEncodingPreset = 'space-saver' | 'balanced' | 'high' | 'very-high'
export type AudioEncodingMode = AudioEncodingPreset | 'default' | 'custom'

const modes: readonly AudioEncodingPreset[] = ['space-saver', 'balanced', 'high', 'very-high']

// Encoding starting points, not measured listening grades or acceptance thresholds.
// MP3 uses supported MPEG-1 bitrate steps; AAC/Opus use their own budgets.
const codecBudgets: Readonly<Record<string, readonly number[]>> = {
  aac: [96, 128, 192, 256],
  opus: [96, 128, 160, 192],
  mp3: [128, 192, 256, 320],
}

export function audioEncodingPresets(codec: string): { mode: AudioEncodingPreset; bitrate: number }[] {
  return (Object.hasOwn(codecBudgets, codec) ? codecBudgets[codec] : []).map((bitrate, index) => ({ mode: modes[index], bitrate }))
}

export function audioPresetBitrate(codec: string, mode: AudioEncodingMode): number | undefined {
  return audioEncodingPresets(codec).find(preset => preset.mode === mode)?.bitrate
}

export function audioEncodingMode(codec: string, bitrate: number | null, selected: AudioEncodingMode | null, defaultBitrate: number): AudioEncodingMode {
  if (bitrate == null) return 'default'
  if (selected === 'custom') return 'custom'
  if (selected != null && audioPresetBitrate(codec, selected) === bitrate) return selected
  if (bitrate === defaultBitrate) return 'default'
  return audioEncodingPresets(codec).find(preset => preset.bitrate === bitrate)?.mode ?? 'custom'
}

export function audioBitrateAfterCodecChange(previous: string, next: string, bitrate: number | null, selected: AudioEncodingMode | null): number | null {
  // Only a tier chosen during this edit follows the codec. Opening or saving a library must
  // never reinterpret its previous bitrate, even when it happens to match a named tier.
  if (selected == null || bitrate == null || audioPresetBitrate(previous, selected) !== bitrate) return bitrate
  return audioPresetBitrate(next, selected) ?? bitrate
}
