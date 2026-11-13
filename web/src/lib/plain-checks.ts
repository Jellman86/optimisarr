// A verification report names each check the way the code does ("Decode health", "Tail
// integrity"). A person wants to know what was checked. Each of the server's stable check names
// maps to a plain label; a name this file has not been taught is shown as the server sent it,
// so a new check is never hidden.
import type { Messages } from './i18n/en'

type Labels = Messages['checkNames']

const exact: Record<string, keyof Labels> = {
  'Decode health': 'plays',
  'Output readable': 'opens',
  Duration: 'length',
  'Video stream': 'has_picture',
  'Video structure': 'picture_format',
  'Timestamp integrity': 'timing',
  'Tail integrity': 'ending',
  'Source video timeline': 'source_timing',
  'Audio tracks': 'audio_tracks',
  'Subtitle tracks': 'subtitle_tracks',
  'Audio languages': 'audio_languages',
  'Subtitle languages': 'subtitle_languages',
  'Size saving': 'smaller',
  'Compression ceiling': 'not_too_small',
  'Colour metadata': 'colour',
  'HDR signal': 'hdr',
  'Picture retention': 'picture_count',
  'A/V sync': 'sync',
  Dimensions: 'picture_size',
  'Audio codec': 'audio_format',
  'Audio codecs unchanged': 'audio_untouched',
  'Audio fidelity': 'audio_fidelity',
  'Audio metadata and artwork': 'audio_tags',
  'Container unchanged': 'container',
  'Image format': 'image_format',
  'EXIF metadata': 'photo_details',
  'ICC colour profile': 'photo_details',
  'Image metadata (EXIF/ICC)': 'photo_details',
  'File identity': 'file_identity',
}

// Checks whose server name carries a suffix, such as the VMAF sampling mode.
const prefixed: [RegExp, keyof Labels][] = [
  [/^Perceptual quality \(VMAF\)/, 'picture_quality'],
  [/^Perceptual image quality/, 'image_score'],
  [/^Image quality \(SSIM\)/, 'image_quality'],
  [/^Perceptual audio quality/, 'audio_quality'],
  [/^Audio loudness/, 'loudness'],
  [/^Audio clipping/, 'clipping'],
]

/** The plain label for a check, or the server's own name when it is not one this file knows. */
export function plainCheckName(name: string, labels: Labels): string {
  const key = exact[name] ?? prefixed.find(([pattern]) => pattern.test(name))?.[1]
  return key ? labels[key] : name
}

/** Whether a check is the picture-quality one. */
export function isPictureQualityCheck(name: string): boolean {
  return /^Perceptual quality \(VMAF\)/.test(name)
}

/**
 * Whether a failed picture-quality check has a score behind it. When the measurement itself
 * failed there is nothing to compare, so a different target would change nothing. The server
 * says which in the check's detail.
 */
export function pictureQualityWasMeasured(check: { name: string; detail: string }): boolean {
  return isPictureQualityCheck(check.name)
    && !/^Quality could not be measured|^VMAF aggregates were missing/.test(check.detail)
}
