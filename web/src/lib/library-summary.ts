// What a library does, in plain sentences, worked out from its settings. Nothing here is written
// by hand per library: every sentence and every warning tag comes from a value, so a setting that
// changes the picture, renames a file, removes a track or deletes an original cannot be left out.
import type { RuleProfileSpec, SaveLibrary } from './api'
import { filesChoice } from './files-choice.ts'
import { formatSize } from './format.ts'
import type { Messages } from './i18n/en'

export type SummaryTopic = 'files' | 'video' | 'audio' | 'images' | 'checks' | 'schedule'

/** A warning tag says something is changed or removed; a neutral one says something is kept. */
export type SummaryTag = { tone: 'warn' | 'neutral'; text: string }

export type SummaryLine = {
  topic: SummaryTopic
  title: string
  text: string
  detail: string | null
  tags: SummaryTag[]
}

export type SummaryContext = {
  m: Messages
  locale: string
  /** The server's values for this library's preset, from /api/library-options. */
  preset: RuleProfileSpec | null
  /** Days an original stays in Quarantine; 0 keeps it until approved, null when not yet known. */
  retentionDays: number | null
  dryRun: boolean
  /** How many files the saved rules pick and skip, when known. */
  counts?: { eligible: number; skipped: number } | null
}

type VmafLevel = { harmonic: number; fifth: number; catastrophic: number; name: (m: Messages) => string }

export const vmafLevels: readonly VmafLevel[] = [
  { harmonic: 80, fifth: 60, catastrophic: 30, name: (m) => m.settings.vmaf_preset_space_saver },
  { harmonic: 85, fifth: 70, catastrophic: 40, name: (m) => m.settings.vmaf_preset_balanced },
  { harmonic: 90, fifth: 75, catastrophic: 45, name: (m) => m.settings.vmaf_preset_high },
  { harmonic: 93, fifth: 80, catastrophic: 50, name: (m) => m.settings.vmaf_preset_lossless },
  { harmonic: 96, fifth: 90, catastrophic: 70, name: (m) => m.settings.vmaf_preset_archival },
]

const noEncodeProfiles = ['RemuxCleanup', 'TrackCleanup']
const codecNames: Record<string, string> = { h264: 'H.264', hevc: 'HEVC (H.265)', av1: 'AV1', aac: 'AAC', opus: 'Opus', mp3: 'MP3', jpeg: 'JPEG', webp: 'WebP' }

export const isVideoLibrary = (mediaType: string) => mediaType !== 'Music' && mediaType !== 'Photo'
export const isAudioLibrary = (mediaType: string) => mediaType === 'Music' || mediaType === 'Other'
export const isImageLibrary = (mediaType: string) => mediaType === 'Photo' || mediaType === 'Other'

export function codecName(codec: string | null | undefined): string {
  return codec ? codecNames[codec.toLowerCase()] ?? codec.toUpperCase() : ''
}

function fill(template: string, params: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (_, key: string) => (key in params ? String(params[key]) : `{${key}}`))
}

function counted(count: number, one: string, other: string): string {
  return fill(count === 1 ? one : other, { count })
}

function list(locale: string, items: string[]): string {
  // The interface is written in British English, which joins a list without a comma before "and".
  return new Intl.ListFormat(locale === 'en' ? 'en-GB' : locale, { style: 'long', type: 'conjunction' }).format(items)
}

function commaList(value: string | null): string[] {
  return (value ?? '').split(',').map((entry) => entry.trim().toLowerCase()).filter(Boolean)
}

/** The name of the quality level these three floors make, or null when they are custom numbers. */
export function vmafLevelName(library: Pick<SaveLibrary, 'minVmafHarmonicMean' | 'minVmafMin' | 'minVmafCatastrophicMin'>, m: Messages): string | null {
  const level = vmafLevels.find((candidate) => candidate.harmonic === library.minVmafHarmonicMean
    && candidate.fifth === library.minVmafMin
    && candidate.catastrophic === library.minVmafCatastrophicMin)
  return level ? level.name(m) : null
}

/** The settings a preset decides, as this library has them; null means "as the preset has it". */
export function presetChanges(library: SaveLibrary, preset: RuleProfileSpec | null): (keyof SaveLibrary)[] {
  if (!preset) return []
  const changed: (keyof SaveLibrary)[] = []
  const differs = <K extends keyof SaveLibrary>(key: K, presetValue: SaveLibrary[K]) => {
    if (library[key] != null && library[key] !== presetValue) changed.push(key)
  }
  differs('targetVideoCodec', preset.codec)
  // Keeping each file's container is a choice about file names, not a departure from the preset.
  if (library.targetContainer != null && library.targetContainer !== 'source' && library.targetContainer !== preset.container) changed.push('targetContainer')
  differs('qualityCrf', preset.crf)
  differs('hdrHandling', preset.hdrHandling)
  differs('videoAudioCodec', preset.videoAudioCodec ?? 'copy')
  if ((library.videoAudioCodec ?? preset.videoAudioCodec ?? 'copy') !== 'copy') differs('videoAudioBitrateKbps', preset.videoAudioBitrateKbps)
  if (library.downmixToStereo !== preset.downmixToStereo) changed.push('downmixToStereo')
  return changed
}

export function presetName(profile: string, m: Messages): string {
  switch (profile) {
    case 'CompatibilityH264': return m.libraries.stop_compatibility
    case 'ExperimentalAv1': return m.libraries.stop_efficiency
    case 'ScottsSettings': return m.libraries.stop_scotts
    default: return m.libraries.stop_balanced
  }
}

export function summariseLibrary(library: SaveLibrary, context: SummaryContext): SummaryLine[] {
  const video = isVideoLibrary(library.mediaType)
  const audio = isAudioLibrary(library.mediaType)
  const image = isImageLibrary(library.mediaType)
  const lines = [filesLine(library, context)]
  if (video) lines.push(videoLine(library, context))
  if (video || audio) lines.push(audioLine(library, context))
  if (image && library.ruleProfile !== 'TrackCleanup') lines.push(imagesLine(library, context))
  lines.push(checksLine(library, context), scheduleLine(library, context))
  return lines
}

function encodesVideo(library: SaveLibrary): boolean {
  return isVideoLibrary(library.mediaType) && !noEncodeProfiles.includes(library.ruleProfile)
}

function effectiveHdr(library: SaveLibrary, preset: RuleProfileSpec | null): string {
  return library.hdrHandling ?? preset?.hdrHandling ?? 'Exclude'
}

function filesLine(library: SaveLibrary, { m, locale, preset, counts }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const video = isVideoLibrary(library.mediaType)
  const encodes = encodesVideo(library)
  const kind = library.mediaType === 'Other' ? s.kind_mixed
    : library.mediaType === 'Music' ? s.kind_audio
      : library.mediaType === 'Photo' ? s.kind_image : s.kind_video
  const sentences = [library.minFileSizeBytes
    ? fill(s.files_over, { kind, size: formatSize(library.minFileSizeBytes) })
    : fill(s.files_all, { kind })]

  const skips: string[] = []
  if (encodes) {
    const codec = codecName(library.targetVideoCodec ?? preset?.codec)
    if (codec) {
      skips.push(library.reencodeSameCodecAboveBytes
        ? fill(s.skip_same_codec_under, { codec, size: formatSize(library.reencodeSameCodecAboveBytes) })
        : fill(s.skip_same_codec, { codec }))
    }
    if (library.skipEfficientSources) skips.push(s.skip_efficient)
    if (effectiveHdr(library, preset) === 'Exclude') skips.push(s.skip_hdr)
    if (!library.optimiseDolbyVision) skips.push(s.skip_dolby_vision)
    if (!library.deinterlaceVideo) skips.push(s.skip_interlaced)
  }
  if (video && library.ruleProfile !== 'TrackCleanup' && library.maxHeight) skips.push(fill(s.skip_tall, { height: library.maxHeight }))
  const paths = (library.excludePaths ?? '').split('\n').map((path) => path.trim()).filter(Boolean)
  if (paths.length > 0) skips.push(fill(s.skip_paths, { paths: list(locale, paths.slice(0, 3)) }))
  if (library.excludeHardLinkedFiles) skips.push(s.skip_hardlinks)
  const codecs = commaList(library.skipSourceCodecs)
  if (codecs.length > 0) skips.push(fill(s.skip_codecs, { codecs: list(locale, codecs) }))
  if (skips.length > 0) sentences.push(fill(s.files_skips, { list: list(locale, skips) }))
  if (!library.enabled) sentences.push(s.files_disabled)

  return {
    topic: 'files',
    title: s.topic_files,
    text: sentences.join(' '),
    detail: counts ? fill(s.files_counts, { picked: counts.eligible.toLocaleString(locale), skipped: counts.skipped.toLocaleString(locale) }) : null,
    tags: [],
  }
}

function videoLine(library: SaveLibrary, { m, preset }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const tags: SummaryTag[] = []
  const sentences: string[] = []
  let detail: string | null = null

  if (library.ruleProfile === 'TrackCleanup') {
    sentences.push(s.video_track_cleanup)
    if (!library.keepAudioLanguages?.trim() && !library.keepSubtitleLanguages?.trim()) sentences.push(s.video_track_cleanup_noop)
  } else if (library.ruleProfile === 'RemuxCleanup') {
    sentences.push(s.video_remux)
    containerSentence(library, preset, s, sentences, tags)
  } else {
    sentences.push(fill(s.video_encode, { codec: codecName(library.targetVideoCodec ?? preset?.codec) }))
    const crf = library.qualityCrf ?? preset?.crf ?? ''
    const level = vmafLevelName(library, m)
    const score = library.minVmafHarmonicMean ?? ''
    const target = level ? fill(s.target_named, { score, level }) : String(score)
    if (library.vmafQualityGateEnabled !== true) sentences.push(fill(s.video_quality_fixed_unchecked, { crf }))
    else if (library.videoQualityStrategy === 'AdaptiveVmaf') sentences.push(fill(s.video_quality_target, { target }))
    else sentences.push(fill(s.video_quality_fixed_checked, { crf, target }))

    const picture: string[] = []
    if (library.videoDownscaleHeight) picture.push(fill(s.video_shrink, { height: library.videoDownscaleHeight }))
    if (library.maxFrameRate) picture.push(fill(s.video_fps, { fps: library.maxFrameRate }))
    if (library.cropBlackBars) picture.push(s.video_crop)
    if (effectiveHdr(library, preset) === 'TonemapToSdr') picture.push(s.video_tonemap)
    if (library.optimiseDolbyVision) picture.push(s.video_dolby_vision)
    if (library.deinterlaceVideo) picture.push(s.video_deinterlace)
    if (library.noiseReduction !== 'Off') {
      picture.push(fill(s.video_noise, { level: library.noiseReduction === 'Strong' ? m.libraries.noise_strong : m.libraries.noise_light }))
    }
    if (library.superResolutionEnabled) picture.push(s.video_upscale)
    if (picture.length === 0) sentences.push(s.video_picture_kept)
    else {
      sentences.push(...picture)
      tags.push({ tone: 'warn', text: s.tag_changes_picture })
    }
    containerSentence(library, preset, s, sentences, tags)

    const changes = presetChanges(library, preset).length
    const name = presetName(library.ruleProfile, m)
    detail = changes === 0
      ? fill(s.preset_detail, { preset: name })
      : fill(changes === 1 ? s.preset_detail_changes_one : s.preset_detail_changes_other, { preset: name, count: changes })
  }

  return { topic: 'video', title: s.topic_video, text: sentences.join(' '), detail, tags }
}

function containerSentence(
  library: SaveLibrary,
  preset: RuleProfileSpec | null,
  s: Messages['librarySummary'],
  sentences: string[],
  tags: SummaryTag[],
) {
  if (library.targetContainer === 'source') {
    sentences.push(s.video_container_source)
    tags.push({ tone: 'neutral', text: s.tag_names_kept })
    return
  }
  const container = (library.targetContainer ?? preset?.container ?? '').toUpperCase()
  if (!container) return
  sentences.push(fill(s.video_container_fixed, { container }))
  tags.push({ tone: 'warn', text: s.tag_renames })
}

function audioLine(library: SaveLibrary, { m, locale, preset }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const video = isVideoLibrary(library.mediaType)
  const tags: SummaryTag[] = []
  const sentences: string[] = []

  if (encodesVideo(library)) {
    const codec = library.videoAudioCodec ?? preset?.videoAudioCodec ?? 'copy'
    if (codec === 'copy') sentences.push(s.audio_untouched)
    else sentences.push(fill(s.audio_convert, { codec: codecName(codec) }), library.downmixToStereo ? s.audio_downmix : s.audio_surround_kept)
  }
  if (video) {
    const kept = commaList(library.keepAudioLanguages)
    const subtitles = commaList(library.keepSubtitleLanguages)
    sentences.push(kept.length > 0 ? fill(s.audio_langs_only, { langs: list(locale, kept.map((code) => code.toUpperCase())) }) : s.audio_langs_all)
    sentences.push(subtitles.length > 0 ? fill(s.subs_only, { langs: list(locale, subtitles.map((code) => code.toUpperCase())) }) : s.subs_all)
    if (kept.length > 0 || subtitles.length > 0) tags.push({ tone: 'warn', text: s.tag_removes_tracks })
  }
  if (isAudioLibrary(library.mediaType)) {
    const codec = codecName(library.audioTargetCodec ?? preset?.audioTargetCodec ?? 'aac')
    sentences.push(library.adaptiveAudioQuality && library.audioQualityGateEnabled
      ? fill(s.music_target, { codec })
      : fill(s.music_convert, { codec, kbps: library.audioBitrateKbps ?? preset?.audioBitrateKbps ?? 128 }))
    sentences.push(library.reencodeLossyAudio ? s.music_lossy_too : s.music_lossy_left)
    if (!video && library.downmixToStereo) sentences.push(s.audio_downmix)
  }

  return { topic: 'audio', title: video ? m.libraryWorkflow.audio : m.libraries.audio, text: sentences.join(' '), detail: null, tags }
}

function imagesLine(library: SaveLibrary, { m }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const tags: SummaryTag[] = []
  const format = codecName(library.targetImageFormat ?? 'jpeg')
  const sentences = [library.adaptiveImageQuality && library.imagePerceptualGateEnabled
    ? fill(s.images_target, { format, score: library.minimumImagePerceptualScore ?? '' })
    : fill(s.images_convert, { format, quality: library.imageQuality ?? 80 })]
  sentences.push(library.reencodeLossyImages ? s.images_lossy_too : s.images_lossy_left)
  if (library.imageDownscaleMode === 'MaxLongEdge') sentences.push(fill(s.images_fit, { pixels: library.imageDownscaleValue }))
  else if (library.imageDownscaleMode === 'Percent') sentences.push(fill(s.images_percent, { percent: library.imageDownscaleValue }))
  else sentences.push(s.images_not_resized)
  if (library.imageDownscaleMode !== 'None') tags.push({ tone: 'warn', text: s.tag_changes_picture })
  return { topic: 'images', title: m.libraries.images, text: sentences.join(' '), detail: null, tags }
}

/** Each optional check this library could have, and whether it is on. The core checks are not optional. */
export function optionalChecks(library: SaveLibrary): { key: string; on: boolean; phrase: (s: Messages['librarySummary']) => string }[] {
  const video = isVideoLibrary(library.mediaType)
  const audio = isAudioLibrary(library.mediaType)
  const image = isImageLibrary(library.mediaType)
  const checks: ReturnType<typeof optionalChecks> = []
  if (video || audio) checks.push({ key: 'audio', on: library.requireAudioRetained, phrase: (s) => s.check_audio })
  if (video) checks.push({ key: 'subtitles', on: library.requireSubtitlesRetained, phrase: (s) => s.check_subtitles })
  checks.push({
    key: 'smaller',
    on: library.requireSizeReduction,
    phrase: (s) => (encodesVideo(library) && library.minimumSizeSavingPercent ? fill(s.check_smaller_by, { percent: library.minimumSizeSavingPercent }) : s.check_smaller),
  })
  if (encodesVideo(library)) {
    checks.push({ key: 'picture', on: library.vmafQualityGateEnabled === true, phrase: (s) => fill(s.check_picture, { score: library.minVmafHarmonicMean ?? '' }) })
  }
  if (video) checks.push({ key: 'soundtrack', on: library.soundtrackQualityGateEnabled === true, phrase: (s) => s.check_soundtrack })
  if (audio) checks.push({ key: 'audioQuality', on: library.audioQualityGateEnabled === true, phrase: (s) => s.check_audio_quality })
  if (video || audio) {
    checks.push({ key: 'loudness', on: library.audioLoudnessGateEnabled, phrase: (s) => s.check_loudness })
    checks.push({ key: 'clipping', on: library.audioClippingGateEnabled, phrase: (s) => s.check_clipping })
  }
  if (image) {
    checks.push({ key: 'ssim', on: library.imageQualityGateEnabled, phrase: (s) => s.check_ssim })
    checks.push({ key: 'imageScore', on: library.imagePerceptualGateEnabled === true, phrase: (s) => fill(s.check_image_score, { score: library.minimumImagePerceptualScore ?? '' }) })
    checks.push({ key: 'metadata', on: library.imageMetadataGateEnabled, phrase: (s) => s.check_metadata })
  }
  return checks
}

function checksLine(library: SaveLibrary, { m, locale }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const playable = isVideoLibrary(library.mediaType) || isAudioLibrary(library.mediaType)
  const core = playable ? [s.check_plays, s.check_length] : [s.check_opens]
  const optional = optionalChecks(library)
  const on = optional.filter((check) => check.on)
  return {
    topic: 'checks',
    title: s.topic_checks,
    text: fill(s.checks_must, { list: list(locale, [...core, ...on.map((check) => check.phrase(s))]) }),
    detail: fill(s.checks_counts, { on: on.length, off: optional.length - on.length }),
    tags: [],
  }
}

function scheduleLine(library: SaveLibrary, { m, retentionDays, dryRun }: SummaryContext): SummaryLine {
  const s = m.librarySummary
  const tags: SummaryTag[] = []
  const sentences: string[] = []
  if (!library.autoEnqueueEnabled) sentences.push(s.schedule_manual)
  else if (library.autoEnqueueWindowStart === library.autoEnqueueWindowEnd) sentences.push(s.schedule_auto_any)
  else sentences.push(fill(s.schedule_auto_between, { start: library.autoEnqueueWindowStart, end: library.autoEnqueueWindowEnd }))

  const choice = filesChoice(library)
  if (choice === 'elsewhere') {
    sentences.push(library.targetFolder?.trim() ? fill(s.after_elsewhere, { folder: library.targetFolder.trim() }) : s.after_elsewhere_unset)
  } else {
    sentences.push(choice === 'auto' ? s.after_auto : s.after_ask)
    if (retentionDays !== null && retentionDays > 0) {
      sentences.push(counted(retentionDays, s.originals_days_one, s.originals_days_other))
      tags.push({ tone: 'warn', text: counted(retentionDays, s.tag_deletes_one, s.tag_deletes_other) })
    } else if (retentionDays === 0) sentences.push(s.originals_kept)
    if (dryRun) sentences.push(s.dry_run)
  }
  if (library.priority !== 0) {
    const level = library.priority >= 2 ? m.libraries.priority_highest
      : library.priority === 1 ? m.libraries.priority_high
        : library.priority === -1 ? m.libraries.priority_low : m.libraries.priority_lowest
    sentences.push(fill(s.priority, { level }))
  }
  return { topic: 'schedule', title: m.libraryWorkflow.automate, text: sentences.join(' '), detail: null, tags }
}
