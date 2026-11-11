<script lang="ts">
  import { tick, untrack } from 'svelte'
  import { api, newLibraryDefaults, type Candidate, type Exclusion, type Library, type LibraryAccess, type LibraryOptions, type RuleProfileSpec, type SaveLibrary, type TargetFolderCheck, type WorkPlacement } from '../api'
  import { i18n, mediaTypeLabel, plural, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Toggle from '../components/Toggle.svelte'
  import FilesChoice from '../components/FilesChoice.svelte'
  import Icon from '../components/Icon.svelte'
  import SettingRow, { type SettingOrigin, type SettingTag } from '../components/SettingRow.svelte'
  import LibrarySummary from '../components/LibrarySummary.svelte'
  import Banner from '../components/Banner.svelte'
  import EmptyState from '../components/EmptyState.svelte'
  import CandidateTable from '../components/CandidateTable.svelte'
  import BatchResult from '../components/BatchResult.svelte'
  import ConfirmDialog from '../components/ConfirmDialog.svelte'
  import ConfigSection from '../components/ConfigSection.svelte'
  import ActionMenu from '../components/ActionMenu.svelte'
  import AudioEncodingPreset from '../components/AudioEncodingPreset.svelte'
  import SelectionCheckbox from '../components/SelectionCheckbox.svelte'
  import SelectionToolbar from '../components/SelectionToolbar.svelte'
  import { createSelection } from '../selection.svelte'
  import { audioPresetBitrate, audioEncodingMode, audioBitrateAfterCodecChange, type AudioEncodingMode } from '../audio-encoding-presets'
  import { ruleProfileLabel } from '../rule-profile-label'
  import { filesChoice } from '../files-choice'
  import { withMediaType } from '../library-defaults'
  import { codecName, presetChanges, presetName, summariseLibrary, vmafLevelName, vmafLevels, type SummaryTopic } from '../library-summary'
  import { encoderFamily, encoderIgnores, encoderName, type EncoderSetting } from '../encoder-support'

  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions>({
    mediaTypes: [],
    ruleProfiles: [],
    ruleProfileSpecs: [],
    hdrHandlings: [],
    videoCodecs: [],
    containers: [],
    encoderPresets: [],
    legacyEncoderPresets: [],
    imageFormats: [],
    hardwareQualityHeadroom: 0,
  })

  // Named queue-priority levels, so the slider reads as a word rather than a raw number.
  const priorityLevels = $derived([
    { value: 2, label: i18n.m.libraries.priority_highest },
    { value: 1, label: i18n.m.libraries.priority_high },
    { value: 0, label: i18n.m.libraries.priority_normal },
    { value: -1, label: i18n.m.libraries.priority_low },
    { value: -2, label: i18n.m.libraries.priority_lowest },
  ])

  const resolutionLimits = $derived([
    { value: null, label: i18n.m.libraries.resolution_no_limit },
    { value: 2160, label: '2160p (4K)' },
    { value: 1440, label: '1440p' },
    { value: 1080, label: '1080p' },
    { value: 720, label: '720p' },
    { value: 480, label: '480p' },
  ])

  // Only two stops: 60 catches high-frame-rate captures, 30 catches broadcast 50/60. Anything
  // finer invites a cap the planner would mostly have to refuse.
  const frameRateCaps = $derived([
    { value: null, label: i18n.m.libraries.video_fps_cap_none },
    { value: 60, label: '60 fps' },
    { value: 30, label: '30 fps' },
  ])

  const DEFAULT_VMAF_HARMONIC = 93
  const DEFAULT_VMAF_MIN = 80
  const DEFAULT_VMAF_CATASTROPHIC = 50
  const DEFAULT_IMAGE_QUALITY = 80

  // One plain sentence per preset, shown under the picker so a first-time user can choose
  // without knowing codecs.
  const presetSummaries: Record<string, string> = $derived({
    ConservativeHevc: i18n.m.presetInfo.balanced,
    CompatibilityH264: i18n.m.presetInfo.compatibility,
    ExperimentalAv1: i18n.m.presetInfo.efficiency,
    ScottsSettings: i18n.m.presetInfo.scotts,
  })

  // The re-encode presets, from most compatible to smallest files, with "Scott's" as a named
  // all-in-one at the end. A preset is a starting point: choosing one fills in its values, and
  // each value can then be changed and reset on its own. Remux and track clean-up are not
  // presets but other things to do, chosen above them.
  const encodeProfiles = ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings']

  function profileLabel(profile: string): string {
    return ruleProfileLabel(profile, i18n.m.libraries)
  }

  function prettyCodec(codec: string | null): string {
    return codec ? codecName(codec) : i18n.m.libraries.codec_none
  }
  type PresetSpec = {
    codec: string
    container: string
    crf: number | null
    hdrHandling: string
    videoAudioCodec: string | null
    videoAudioBitrateKbps: number
    audioTargetCodec: string
    audioBitrateKbps: number
    downmixToStereo: boolean
  }
  const FALLBACK_SPEC: PresetSpec = {
    codec: 'HEVC (H.265)',
    container: 'MP4',
    crf: 24,
    hdrHandling: 'Exclude',
    videoAudioCodec: 'aac',
    videoAudioBitrateKbps: 160,
    audioTargetCodec: 'aac',
    audioBitrateKbps: 128,
    downmixToStereo: false,
  }

  // The concrete codec/container/CRF each preset selects, sourced from the backend's
  // RuleProfileDefaults via /api/library-options so the preset choices never drift from what the
  // server actually does. Keyed by RuleProfile name.
  const presetSpecs = $derived.by(() => {
    const map: Record<string, PresetSpec> = {}
    for (const spec of options.ruleProfileSpecs) {
      map[spec.profile] = {
        codec: prettyCodec(spec.codec),
        container: (spec.container ?? '').toUpperCase(),
        crf: spec.crf,
        hdrHandling: spec.hdrHandling,
        videoAudioCodec: spec.videoAudioCodec,
        videoAudioBitrateKbps: spec.videoAudioBitrateKbps,
        audioTargetCodec: spec.audioTargetCodec ?? 'aac',
        // Older API responses omit standalone defaults. Retain their known profile values
        // during rolling updates until the new server-owned fields are available.
        audioBitrateKbps: spec.audioBitrateKbps ?? (spec.profile === 'ScottsSettings' ? 96 : 128),
        downmixToStereo: spec.downmixToStereo,
      }
    }
    return map
  })
  function specFor(profile: string): PresetSpec {
    return presetSpecs[profile] ?? presetSpecs.ConservativeHevc ?? FALLBACK_SPEC
  }
  // The server's own values for the chosen preset, as sent: what a blank setting falls back to.
  const presetRaw = $derived<RuleProfileSpec | null>(
    options.ruleProfileSpecs.find((spec) => spec.profile === form.ruleProfile) ?? null,
  )

  type VmafMode = 'off' | 'space-saver' | 'balanced' | 'high' | 'lossless' | 'archival' | 'custom'
  const vmafModes = ['space-saver', 'balanced', 'high', 'lossless', 'archival'] as const
  const vmafPresets = vmafLevels.map((level, index) => ({ ...level, mode: vmafModes[index] }))
  // Keep an explicitly selected Custom mode visible even while its initial values happen to match
  // a named preset. Loaded libraries still derive Custom from any non-preset values.
  let vmafCustomSelected = $state(false)

  // These are numeric shortcuts, not calibrated quality tiers. Equal spacing follows orders of
  // magnitude so useful small distances do not bunch together at the end of a linear 0–1 range.
  const audioDifferenceStops = [0, 0.0001, 0.001, 0.01, 0.1] as const
  let audioDifferenceCustomSelected = $state(false)
  // SSIMULACRA2's authors publish these anchors (very high, high, medium, low quality); they
  // name the metric's scale, not an Optimisarr default.
  const imageScoreStops = [90, 70, 50, 30] as const
  let imageScoreCustomSelected = $state(false)
  let soundtrackDifferenceCustomSelected = $state(false)

  // Reporting, the gate and "choose by measurement" were three switches per kind of media, and
  // the server refuses the combinations that make no sense. They read as one choice of four.
  type MeasureMode = 'off' | 'report' | 'require' | 'target'
  type MeasureKind = 'audio' | 'soundtrack' | 'image'
  function measureMode(kind: MeasureKind): MeasureMode {
    const [reporting, gate, adaptive] = kind === 'audio'
      ? [form.audioQualityReportingEnabled, form.audioQualityGateEnabled, form.adaptiveAudioQuality]
      : kind === 'soundtrack'
        ? [form.soundtrackQualityReportingEnabled, form.soundtrackQualityGateEnabled, form.adaptiveSoundtrackQuality]
        : [form.imagePerceptualReportingEnabled, form.imagePerceptualGateEnabled, form.adaptiveImageQuality]
    if (gate) return adaptive ? 'target' : 'require'
    return reporting ? 'report' : 'off'
  }
  function setMeasureMode(kind: MeasureKind, mode: MeasureMode) {
    const reporting = mode !== 'off'
    const gate = mode === 'require' || mode === 'target'
    const adaptive = mode === 'target'
    if (kind === 'audio') {
      form.audioQualityReportingEnabled = reporting; form.audioQualityGateEnabled = gate; form.adaptiveAudioQuality = adaptive
    } else if (kind === 'soundtrack') {
      form.soundtrackQualityReportingEnabled = reporting; form.soundtrackQualityGateEnabled = gate; form.adaptiveSoundtrackQuality = adaptive
    } else {
      form.imagePerceptualReportingEnabled = reporting; form.imagePerceptualGateEnabled = gate; form.adaptiveImageQuality = adaptive
    }
  }
  const measureModes = $derived<{ value: MeasureMode; label: string }[]>([
    { value: 'off', label: i18n.m.common.off },
    { value: 'report', label: i18n.m.libraryEditor.mode_report },
    { value: 'require', label: i18n.m.libraryEditor.mode_require },
    { value: 'target', label: i18n.m.libraryEditor.mode_target },
  ])

  // A limit is one of the named stops, or a number of the operator's own. '' means none chosen yet.
  function differenceChoice(value: number | null | undefined, custom: boolean): string {
    if (custom) return 'custom'
    if (value == null) return ''
    return audioDifferenceStops.some((stop) => stop === Number(value)) ? String(Number(value)) : 'custom'
  }
  function setAudioDifference(choice: string) {
    audioDifferenceCustomSelected = choice === 'custom'
    if (choice !== 'custom' && choice !== '') form.maximumAudioQualityDistance = Number(choice)
  }
  function setSoundtrackDifference(choice: string) {
    soundtrackDifferenceCustomSelected = choice === 'custom'
    if (choice !== 'custom' && choice !== '') form.maximumSoundtrackQualityDistance = Number(choice)
  }
  const imageScoreChoice = $derived.by(() => {
    if (imageScoreCustomSelected) return 'custom'
    if (form.minimumImagePerceptualScore == null) return ''
    return imageScoreStops.some((stop) => stop === Number(form.minimumImagePerceptualScore)) ? String(Number(form.minimumImagePerceptualScore)) : 'custom'
  })
  function setImageScore(choice: string) {
    imageScoreCustomSelected = choice === 'custom'
    if (choice !== 'custom' && choice !== '') form.minimumImagePerceptualScore = Number(choice)
  }

  let audioEncodingSelection = $state<AudioEncodingMode | null>(null)
  let videoAudioEncodingSelection = $state<AudioEncodingMode | null>(null)

  function chooseAudioEncoding(mode: AudioEncodingMode, video = false) {
    if (video) {
      videoAudioEncodingSelection = mode
      if (mode === 'custom') {
        form.videoAudioBitrateKbps ??= specFor(form.ruleProfile).videoAudioBitrateKbps
      }
      else form.videoAudioBitrateKbps = mode === 'default' ? null : audioPresetBitrate(form.videoAudioCodec ?? presetRaw?.videoAudioCodec ?? 'aac', mode) ?? form.videoAudioBitrateKbps
    } else {
      audioEncodingSelection = mode
      if (mode === 'custom') {
        form.audioBitrateKbps ??= specFor(form.ruleProfile).audioBitrateKbps
      }
      else form.audioBitrateKbps = mode === 'default' ? null : audioPresetBitrate(form.audioTargetCodec ?? specFor(form.ruleProfile).audioTargetCodec, mode) ?? form.audioBitrateKbps
    }
  }

  function changeAudioCodec(codec: string, video = false) {
    if (video) {
      // An empty codec means "as the preset has it", which is stored as no choice at all.
      const presetCodec = presetRaw?.videoAudioCodec ?? 'copy'
      const next = codec || presetCodec
      form.videoAudioBitrateKbps = audioBitrateAfterCodecChange(form.videoAudioCodec ?? presetCodec, next, form.videoAudioBitrateKbps, videoAudioEncodingSelection)
      form.videoAudioCodec = codec || null
      if (next === 'copy') videoAudioEncodingSelection = null
    } else {
      const defaultCodec = specFor(form.ruleProfile).audioTargetCodec
      form.audioBitrateKbps = audioBitrateAfterCodecChange(form.audioTargetCodec ?? defaultCodec, codec || defaultCodec, form.audioBitrateKbps, audioEncodingSelection)
      form.audioTargetCodec = codec || null
    }
  }

  const vmafMode = $derived.by<VmafMode>(() => {
    if (form.vmafQualityGateEnabled !== true) return 'off'
    if (vmafCustomSelected) return 'custom'
    const preset = form.vmafQualityGateEnabled === true ? vmafPresets.find((candidate) =>
      candidate.harmonic === form.minVmafHarmonicMean
      && candidate.fifth === form.minVmafMin
      && candidate.catastrophic === form.minVmafCatastrophicMin) : undefined
    return preset?.mode ?? 'custom'
  })

  function validVmafScore(value: number | null): value is number {
    return value != null && Number.isFinite(value) && value >= 0 && value <= 100
  }

  const vmafError = $derived.by<string | null>(() => {
    if (vmafMode === 'off') return null
    if (!validVmafScore(form.minVmafHarmonicMean)
      || !validVmafScore(form.minVmafMin)
      || !validVmafScore(form.minVmafCatastrophicMin)) {
      return i18n.m.settings.validation_vmaf
    }
    if (form.minVmafCatastrophicMin > form.minVmafMin
      || form.minVmafMin > form.minVmafHarmonicMean) {
      return i18n.m.settings.validation_vmaf_order
    }
    if (form.vmafFrameSubsample == null
      || !Number.isInteger(form.vmafFrameSubsample)
      || form.vmafFrameSubsample < 1
      || form.vmafFrameSubsample > 10) {
      return i18n.m.settings.validation_vmaf_subsample
    }
    return null
  })

  const verificationError = $derived.by<string | null>(() => {
    if (form.requireSizeReduction && showVideoOptions && !isNoEncodeProfile
      && form.minimumSizeSavingPercent != null
      && (!Number.isFinite(Number(form.minimumSizeSavingPercent))
        || Number(form.minimumSizeSavingPercent) <= 0
        || Number(form.minimumSizeSavingPercent) > 99)) {
      return i18n.m.settings.validation_minimum_saving
    }
    if (form.requireSizeReduction && showVideoOptions && !isNoEncodeProfile
      && form.maximumSizeSavingPercent != null
      && (!Number.isFinite(Number(form.maximumSizeSavingPercent))
        || Number(form.maximumSizeSavingPercent) <= 0
        || Number(form.maximumSizeSavingPercent) > 99)) {
      return i18n.m.settings.validation_maximum_saving
    }
    if (form.requireSizeReduction && showVideoOptions && !isNoEncodeProfile
      && form.minimumSizeSavingPercent != null && form.maximumSizeSavingPercent != null
      && Number(form.minimumSizeSavingPercent) > Number(form.maximumSizeSavingPercent)) {
      return i18n.m.settings.validation_saving_order
    }
    if (!Number.isFinite(Number(form.durationTolerancePercent))
      || Number(form.durationTolerancePercent) < 0) {
      return i18n.m.settings.validation_duration
    }
    if (!Number.isFinite(Number(form.maxLoudnessDriftLufs))
      || Number(form.maxLoudnessDriftLufs) < 0) {
      return i18n.m.settings.validation_loudness
    }
    if (!Number.isFinite(Number(form.maxTruePeakDbtp))) {
      return i18n.m.settings.validation_true_peak
    }
    if (!Number.isFinite(Number(form.minimumImageSsim))
      || Number(form.minimumImageSsim) < 0
      || Number(form.minimumImageSsim) > 1) {
      return i18n.m.settings.validation_ssim
    }
    if (showImageOptions && form.adaptiveImageQuality && form.imagePerceptualGateEnabled && form.imageDownscaleMode !== 'None') return i18n.m.quality_first.image_no_resize
    if (showAudioOptions && form.adaptiveAudioQuality && form.audioQualityGateEnabled && form.downmixToStereo) return i18n.m.quality_first.audio_no_downmix
    if (showVideoOptions && form.soundtrackQualityGateEnabled
      && (form.maximumSoundtrackQualityDistance == null || !Number.isFinite(Number(form.maximumSoundtrackQualityDistance))
        || Number(form.maximumSoundtrackQualityDistance) < 0 || Number(form.maximumSoundtrackQualityDistance) > 1)) {
      return i18n.m.soundtrack_quality.validation
    }
    if (showImageOptions && form.imagePerceptualGateEnabled
      && (form.minimumImagePerceptualScore == null || !Number.isFinite(Number(form.minimumImagePerceptualScore))
        || Number(form.minimumImagePerceptualScore) < 0 || Number(form.minimumImagePerceptualScore) > 100)) {
      return i18n.m.image_quality.validation
    }
    if (showAudioOptions && form.audioQualityGateEnabled
      && (form.maximumAudioQualityDistance == null || !Number.isFinite(Number(form.maximumAudioQualityDistance))
        || Number(form.maximumAudioQualityDistance) < 0 || Number(form.maximumAudioQualityDistance) > 1)) {
      return i18n.m.audio_quality.validation
    }
    return null
  })

  // The picture check as it was before it was switched off, so switching it back on in the same
  // visit restores the person's own floors and sampling instead of a weaker default.
  type RememberedVmaf = { harmonic: number | null; fifth: number | null; catastrophic: number | null; clip: boolean | null; subsample: number | null; custom: boolean }
  let rememberedVmaf: RememberedVmaf | null = null

  function restorePictureCheck() {
    if (!rememberedVmaf) {
      setVmafMode('lossless')
      return
    }
    form.vmafQualityGateEnabled = true
    form.minVmafHarmonicMean = rememberedVmaf.harmonic
    form.minVmafMin = rememberedVmaf.fifth
    form.minVmafCatastrophicMin = rememberedVmaf.catastrophic
    form.clipVmafEnabled = rememberedVmaf.clip ?? true
    form.vmafFrameSubsample = rememberedVmaf.subsample ?? 1
    vmafCustomSelected = rememberedVmaf.custom
  }

  function setVmafMode(mode: VmafMode) {
    if (mode === 'off' && form.videoQualityStrategy === 'AdaptiveVmaf') return
    if (mode === 'off' && form.vmafQualityGateEnabled === true) {
      rememberedVmaf = {
        harmonic: form.minVmafHarmonicMean, fifth: form.minVmafMin, catastrophic: form.minVmafCatastrophicMin,
        clip: form.clipVmafEnabled, subsample: form.vmafFrameSubsample, custom: vmafCustomSelected,
      }
    }
    vmafCustomSelected = mode === 'custom'
    if (mode === 'off') {
      form.vmafQualityGateEnabled = false
      form.minVmafHarmonicMean = null
      form.minVmafMin = null
      form.minVmafCatastrophicMin = null
      form.clipVmafEnabled = null
      form.vmafFrameSubsample = null
      return
    }

    const preset = vmafPresets.find((candidate) => candidate.mode === mode)
    form.vmafQualityGateEnabled = true
    form.minVmafHarmonicMean = preset?.harmonic ?? form.minVmafHarmonicMean ?? DEFAULT_VMAF_HARMONIC
    form.minVmafMin = preset?.fifth ?? form.minVmafMin ?? DEFAULT_VMAF_MIN
    form.minVmafCatastrophicMin = preset?.catastrophic
      ?? form.minVmafCatastrophicMin
      ?? DEFAULT_VMAF_CATASTROPHIC
    form.clipVmafEnabled ??= true
    form.vmafFrameSubsample ??= 1
  }

  function placementName(placement: WorkPlacement): string {
    switch (placement) {
      case 'LocalOnly': return i18n.m.libraries.placement_local
      case 'PreferWorker': return i18n.m.libraries.placement_prefer
      case 'WorkerOnly': return i18n.m.libraries.placement_worker_only
      default: return i18n.m.libraries.placement_anywhere
    }
  }

  function placementDescription(placement: WorkPlacement): string {
    switch (placement) {
      case 'LocalOnly': return i18n.m.libraries.placement_local_desc
      case 'PreferWorker': return i18n.m.libraries.placement_prefer_desc
      case 'WorkerOnly': return i18n.m.libraries.placement_worker_only_desc
      default: return i18n.m.libraries.placement_anywhere_desc
    }
  }

  function setVideoQualityStrategy(strategy: 'Fixed' | 'AdaptiveVmaf') {
    form.videoQualityStrategy = strategy
    // Adaptive selection needs a concrete target to make a decision. Choose the existing
    // visually-lossless default when it was off, then leave the normal VMAF picker visible so the
    // operator can deliberately change it before saving.
    if (strategy === 'AdaptiveVmaf' && vmafMode === 'off') {
      restorePictureCheck()
    }
  }

  // How quality is chosen, whether the picture is checked, and against what, are one decision.
  // They are stored as a strategy, a gate switch and three floors, and shown as one group.
  type QualityMode = 'target' | 'fixed-checked' | 'fixed-unchecked'
  const qualityMode = $derived.by<QualityMode>(() =>
    form.videoQualityStrategy === 'AdaptiveVmaf' ? 'target' : vmafMode === 'off' ? 'fixed-unchecked' : 'fixed-checked')
  function setQualityMode(mode: QualityMode) {
    if (mode === 'target') {
      setVideoQualityStrategy('AdaptiveVmaf')
      return
    }
    form.videoQualityStrategy = 'Fixed'
    if (mode === 'fixed-unchecked') setVmafMode('off')
    else if (vmafMode === 'off') restorePictureCheck()
  }

  // Sampling and frame interval are one question: how much of the file is checked. Three answers
  // cover what people choose; any other saved pair is kept and shown as it is.
  type SamplingChoice = 'samples' | 'full' | 'full-3' | 'custom'
  const samplingChoice = $derived.by<SamplingChoice>(() => {
    if (form.vmafFrameSubsample === 1) return form.clipVmafEnabled ? 'samples' : 'full'
    return form.vmafFrameSubsample === 3 && !form.clipVmafEnabled ? 'full-3' : 'custom'
  })
  function setSampling(choice: string) {
    if (choice === 'custom') return
    form.clipVmafEnabled = choice === 'samples'
    form.vmafFrameSubsample = choice === 'full-3' ? 3 : 1
  }

  // Equal times have always meant "any time". That is now a choice of its own, saved the same way.
  // Equal times are read as "any time" only when a library is loaded: while someone is typing a
  // time, passing through an equal pair must not take the two inputs away.
  let windowChoice = $state<'any' | 'between'>('any')
  function readWindowChoice() {
    windowChoice = form.autoEnqueueWindowStart === form.autoEnqueueWindowEnd ? 'any' : 'between'
  }
  function setWindowChoice(choice: string) {
    if (choice === 'any') {
      form.autoEnqueueWindowStart = '00:00'
      form.autoEnqueueWindowEnd = '00:00'
      windowChoice = 'any'
      return
    }
    if (form.autoEnqueueWindowStart === form.autoEnqueueWindowEnd) {
      form.autoEnqueueWindowStart = '01:00'
      form.autoEnqueueWindowEnd = '06:00'
    }
    windowChoice = 'between'
  }


  function hdrLabel(hdr: string): string {
    if (hdr === 'TonemapToSdr') return i18n.m.libraries.hdr_tonemap
    if (hdr === 'Exclude') return i18n.m.libraries.hdr_exclude
    if (hdr === 'Preserve') return i18n.m.libraries.hdr_preserve
    return hdr
  }

  const encoderEffortLabels: Record<string, string> = $derived({
    quick: i18n.m.libraries.encoder_effort_fast,
    balanced: i18n.m.libraries.encoder_effort_balanced,
    efficient: i18n.m.libraries.encoder_effort_efficient,
  })
  function encoderEffortLabel(effort: string): string {
    return encoderEffortLabels[effort] ?? effort
  }

  const FLASH_KEY = 'optimisarr.library.flash'
  function takeFlashMessage(): string | null {
    const value = sessionStorage.getItem(FLASH_KEY)
    sessionStorage.removeItem(FLASH_KEY)
    return value
  }

  let error = $state<string | null>(null)
  let message = $state<string | null>(takeFlashMessage())
  let busyId = $state<number | null>(null)
  let pickerOpen = $state(false)
  // Set by the files choice while the chosen folder is one the server would refuse on Save.
  // The server's answer about the save folder, asked here rather than only by the control that
  // edits it: that control is on one page of the editor, and Save is on all of them.
  let targetFolderCheck = $state<TargetFolderCheck | null>(null)
  // The same question for each saved library, so a folder that has stopped being usable shows on
  // its card instead of only when a job fails.
  let refusedSaveFolders = $state<Record<number, TargetFolderCheck>>({})

  // null = nothing open; 0 = adding a new library; >0 = editing that card.
  let editingId = $state<number | null>(null)
  // Within an open library, switch between tuning its Rules and seeing the Candidates they select.
  let activeTab = $state<'rules' | 'candidates' | 'excluded'>('rules')
  // The candidate decisions for the library currently open in the editor. Re-fetched when a
  // library is opened and after each Save/Scan/Enqueue, so the list always reflects saved rules.
  let editorCandidates = $state<Candidate[]>([])
  let editorCandidatesLoading = $state(false)
  let editorCandidatesError = $state<string | null>(null)
  const editorEligibleCount = $derived(editorCandidates.filter((c) => c.eligible).length)
  // The library's excluded (never-optimise) files, shown on the Excluded tab.
  let editorExclusions = $state<Exclusion[]>([])
  let editorExclusionsLoading = $state(false)
  let editorExclusionsError = $state<string | null>(null)
  // The Excluded tab's own selection: which of the shown exclusions are marked for removal.
  // It is reset whenever a different library is opened, so ids from one library are never
  // carried into another.
  const exclusionSelection = createSelection()
  let exclusionConfirmOpen = $state(false)
  let exclusionBusy = $state(false)
  let exclusionOutcome = $state<{ succeeded: number; failures: { id: number; code: string; message: string }[] } | null>(null)
  let exclusionIds = $derived(editorExclusions.map((exclusion) => exclusion.id))
  // Per-library eligible/skipped tallies for the list cards (counts only — see /api/candidates/summary).
  let summaries = $state<Record<number, { eligible: number; skipped: number }>>({})
  let form = $state<SaveLibrary>(blankForm())

  const MAX_LANGUAGE_LIST_LENGTH = 256
  type LanguageListInput = {
    codes: string[]
    normalised: string | null
    syntaxValid: boolean
    tooLong: boolean
  }

  // Mirrors the backend's storage validation so the form can explain a bad value before Save.
  // The API remains authoritative; this is immediate, accessible operator feedback.
  function parseLanguageListInput(value: string | null): LanguageListInput {
    const raw = value?.trim() ?? ''
    if (!raw) return { codes: [], normalised: null, syntaxValid: true, tooLong: false }

    const entries = raw.split(',').map((entry) => entry.trim()).filter(Boolean)
    if (entries.length === 0 || entries.some((entry) => !/^[A-Za-z]{2,3}$/.test(entry))) {
      return { codes: [], normalised: null, syntaxValid: false, tooLong: false }
    }

    const codes = [...new Set(entries.map((entry) => entry.toLowerCase()))]
    const normalised = codes.join(', ')
    return {
      codes,
      normalised,
      syntaxValid: true,
      tooLong: normalised.length > MAX_LANGUAGE_LIST_LENGTH,
    }
  }

  const audioLanguageInput = $derived(parseLanguageListInput(form.keepAudioLanguages))
  const audioLanguageError = $derived(
    !audioLanguageInput.syntaxValid
      ? i18n.m.libraries.keep_audio_langs_invalid
      : audioLanguageInput.tooLong
        ? i18n.m.libraries.keep_audio_langs_too_long
        : null,
  )

  function normaliseAudioLanguageInput() {
    if (!audioLanguageError) form.keepAudioLanguages = audioLanguageInput.normalised
  }

  const subtitleLanguageInput = $derived(parseLanguageListInput(form.keepSubtitleLanguages))
  const subtitleLanguageError = $derived(
    !subtitleLanguageInput.syntaxValid
      ? i18n.m.libraries.keep_audio_langs_invalid
      : subtitleLanguageInput.tooLong
        ? i18n.m.libraries.keep_audio_langs_too_long
        : null,
  )

  function normaliseSubtitleLanguageInput() {
    if (!subtitleLanguageError) form.keepSubtitleLanguages = subtitleLanguageInput.normalised
  }

  // Work can go to a remote worker only when both the saved switch and deployment allow it.
  // Decides whether the placement choice is shown; the choice itself is stored either way.
  let remoteWorkersOn = $state(false)
  // AI super-resolution is experimental: a library's switch appears only while the feature is on,
  // and a hidden choice is kept unchanged for when it is turned back on.
  let superResolutionOn = $state(false)
  // Two settings that belong to every library, shown beside the choice they change. Null until loaded.
  let quarantineRetentionDays = $state<number | null | 'unavailable'>(null)
  let dryRunOn = $state(false)
  // The encoder every job here will use, when one fixed encoder does them all; see encoder-support.ts.
  let encoderMode = $state<string | null>(null)
  const encoder = $derived(encoderFamily(encoderMode, form.targetVideoCodec ?? presetRaw?.codec, remoteWorkersOn))
  function ignoredReason(setting: EncoderSetting): string | null {
    return encoder && encoderIgnores(encoder, setting) ? t(i18n.m.libraryEditor.ignored_by, { encoder: encoderName(encoder) }) : null
  }
  const settingsLink = (room: 'encoding' | 'super-resolution', name: string) => ({
    label: `${i18n.m.nav.settings} → ${name}`,
    onclick: () => router.go(`/settings/${room}`),
  })
  const placements: WorkPlacement[] = ['Anywhere', 'LocalOnly', 'PreferWorker', 'WorkerOnly']
  // Edited in MB for friendliness; converted to bytes on save.
  let minSizeMb = $state<number | ''>('')
  // The same-codec re-encode threshold is edited in GB (these are "massive" files) and stored as bytes.
  let sameCodecGb = $state<number | ''>('')
  const DEFAULT_SAME_CODEC_GB = 20

  // Dirty tracking: a JSON snapshot of the form as it was opened. The live form compared against
  // it tells us whether there are unsaved edits, so we can warn before discarding them and only
  // enable Save when something actually changed.
  let pristine = $state('')
  function formSnapshot(): string {
    return JSON.stringify({ ...form, minSizeMb, sameCodecGb })
  }
  function markPristine() {
    pristine = formSnapshot()
  }
  const isDirty = $derived(formSnapshot() !== pristine)

  // Returns true if it is safe to leave the current form (nothing open, no edits, or the user
  // confirmed losing them). Called before opening a different form or closing the editor.
  function confirmDiscardIfDirty(): boolean {
    return editingId === null || !isDirty || confirm(i18n.m.libraries.confirm_discard)
  }

  const BYTES_PER_MB = 1024 * 1024
  const BYTES_PER_GB = 1024 * 1024 * 1024

  // Toggle the same-codec re-encode: ticking defaults the threshold to a sensible "massive" size;
  // unticking clears it (null = the conservative skip-if-already-target-codec behaviour).
  function toggleSameCodec(on: boolean) {
    sameCodecGb = on ? (sameCodecGb === '' ? DEFAULT_SAME_CODEC_GB : sameCodecGb) : ''
  }

  // Which optimisation pipelines a media type involves. Used both to scope the Advanced controls
  // and to decide whether the (video-only) preset profile is meaningful for a library.
  function isVideoType(type: string): boolean {
    return type !== 'Music' && type !== 'Photo'
  }
  function isAudioType(type: string): boolean {
    return type === 'Music' || type === 'Other'
  }
  function isImageType(type: string): boolean {
    return type === 'Photo' || type === 'Other'
  }

  function setMediaType(type: string) {
    // The rule for what a kind allows is shared with setup, which also lets a kind be chosen.
    form = withMediaType(form, type)
    if (!isVideoType(type)) vmafCustomSelected = false
  }

  // Advanced controls are scoped to the library's media type: video knobs for Film/TV, audio for
  // Music, images for Photo, and everything for a mixed "Other" library that may hold any of them.
  const showVideoOptions = $derived(isVideoType(form.mediaType))
  // Mirrors the server: soundtracks must be re-encoded. With a downmix, each file's stereo tracks are still searched.
  // A soundtrack that follows the preset counts by what the preset converts to; the server wants
  // the codec named, so payload() names it when bitrates are chosen by measurement.
  const soundtrackCodec = $derived(form.videoAudioCodec ?? presetRaw?.videoAudioCodec ?? 'copy')
  const soundtrackSearchAvailable = $derived(['aac', 'opus', 'mp3'].includes(soundtrackCodec))
  const showAudioOptions = $derived(isAudioType(form.mediaType))
  const showImageOptions = $derived(isImageType(form.mediaType))

  // The codecs worth offering as a source exclusion, by what the library actually holds. Named
  // with ffprobe's own spelling, because that is what the rule compares against. Offering a fixed
  // set rather than a text box means an operator cannot silently mistype a codec into a rule that
  // then never fires.
  const videoSourceCodecs = ['h264', 'hevc', 'av1', 'vp9', 'vp8', 'mpeg2video', 'mpeg4', 'vc1']
  const audioSourceCodecs = ['flac', 'alac', 'opus', 'aac', 'mp3', 'vorbis', 'ac3', 'dts']
  const imageSourceCodecs = ['mjpeg', 'png', 'webp', 'tiff', 'bmp', 'gif']

  const offeredSourceCodecs = $derived([
    ...(showVideoOptions ? videoSourceCodecs : []),
    ...(showAudioOptions ? audioSourceCodecs : []),
    ...(showImageOptions ? imageSourceCodecs : []),
  ])

  const skippedCodecs = $derived(
    (form.skipSourceCodecs ?? '')
      .split(',')
      .map((codec) => codec.trim().toLowerCase())
      .filter(Boolean),
  )

  function toggleSkippedCodec(codec: string) {
    const next = skippedCodecs.includes(codec)
      ? skippedCodecs.filter((c) => c !== codec)
      : [...skippedCodecs, codec]
    // Stored in the offered order rather than click order, so the saved value is stable and two
    // libraries configured the same way compare equal.
    form.skipSourceCodecs =
      offeredSourceCodecs.filter((c) => next.includes(c)).join(', ') || null
  }

  const isRemuxProfile = $derived(form.ruleProfile === 'RemuxCleanup')
  const isTrackCleanupProfile = $derived(form.ruleProfile === 'TrackCleanup')
  // Both no-encode profiles take the compatibility→efficiency slider out of play.
  const isNoEncodeProfile = $derived(isRemuxProfile || isTrackCleanupProfile)
  // Track cleanup does nothing until at least one kept-language rule is set; surface
  // that honestly in the form rather than letting the operator save a no-op library.
  const trackCleanupNeedsLanguages = $derived(
    isTrackCleanupProfile && !form.keepAudioLanguages?.trim() && !form.keepSubtitleLanguages?.trim(),
  )
  const encoderEffortError = $derived(
    options.encoderPresets.length > 0
      && form.encoderPreset != null
      && !options.encoderPresets.includes(form.encoderPreset)
      && !options.legacyEncoderPresets.includes(form.encoderPreset)
      ? t(i18n.m.libraries.encoder_effort_invalid, { value: form.encoderPreset })
      : null,
  )
  const encoderEffortIsLegacy = $derived(
    form.encoderPreset != null && options.legacyEncoderPresets.includes(form.encoderPreset),
  )

  $effect(() => {
    const path = editingId !== null && form.moveOnComplete ? form.targetFolder?.trim() : ''
    targetFolderCheck = null
    if (!path) return
    let cancelled = false
    // The name only labels a refusal, so typing it must not ask the server again.
    api.checkTargetFolder({ path, libraryId: editingId || null, libraryName: untrack(() => form.name), libraryPath: form.path })
      .then((result) => { if (!cancelled) targetFolderCheck = result })
      // Unanswered is not refused: Save asks the server again.
      .catch(() => {})
    return () => { cancelled = true }
  })
  const targetFolderRefused = $derived(form.moveOnComplete && targetFolderCheck !== null && !targetFolderCheck.allowed)

  // Saving elsewhere needs a folder the server will accept; say which before Save, not after.
  const targetFolderProblem = $derived(
    !form.moveOnComplete ? null
      : !form.targetFolder?.trim() ? i18n.m.filesChoice.folder_required
        : targetFolderRefused ? i18n.m.filesChoice.folder_refused
          : null,
  )

  // Save is offered only for a named, located library with no outstanding validation error and at
  // least one edit to persist. Derived once so every Save control shares exactly one definition.
  const canSave = $derived(
    !!form.name
      && !!form.path
      && isDirty
      && !audioLanguageError
      && !subtitleLanguageError
      && !encoderEffortError
      && !(!isNoEncodeProfile && vmafError)
      && !verificationError
      && !targetFolderProblem,
  )

  // Choosing a preset is a deliberate choice of that preset, so it drops the values that were
  // changed on top of the last one. Each can be changed again, and reset, on its own row.
  function choosePreset(profile: string) {
    if (form.ruleProfile === profile) return
    form.ruleProfile = profile
    resetToPreset()
    form.qualityCrf = null
  }

  const presetLabel = $derived(presetName(form.ruleProfile, i18n.m))
  const changedFromPreset = $derived(presetChanges(form, presetRaw))
  const presetState = $derived(
    changedFromPreset.length === 0
      ? t(i18n.m.librarySummary.preset_detail, { preset: presetLabel })
      : t(changedFromPreset.length === 1 ? i18n.m.librarySummary.preset_detail_changes_one : i18n.m.librarySummary.preset_detail_changes_other,
        { preset: presetLabel, count: changedFromPreset.length }),
  )

  /** Says where a value came from: the preset, or a change that can be reset to it. */
  function presetOrigin(key: keyof SaveLibrary, presetValue: string, reset: () => void): SettingOrigin {
    return changedFromPreset.includes(key)
      ? { changed: true, text: t(i18n.m.settingRow.preset_uses, { preset: presetLabel, value: presetValue }), onreset: reset }
      : { text: t(i18n.m.settingRow.from_preset, { preset: presetLabel }) }
  }

  type ProcessingMode = 'encode' | 'remux' | 'track-cleanup'
  const processingMode = $derived<ProcessingMode>(
    isTrackCleanupProfile ? 'track-cleanup' : isRemuxProfile ? 'remux' : 'encode',
  )

  function setProcessingMode(mode: ProcessingMode) {
    form.ruleProfile = mode === 'track-cleanup'
      ? 'TrackCleanup'
      : mode === 'remux'
        ? 'RemuxCleanup'
        : encodeProfiles.includes(form.ruleProfile)
          ? form.ruleProfile
          : 'ConservativeHevc'
    if (mode !== 'encode') {
      // A saved codec re-encodes whatever the mode says, so a no-re-encode mode must not keep one.
      form.targetVideoCodec = null
      setVideoQualityStrategy('Fixed')
      setVmafMode('off')
    }
  }

  // Keeping each file's container is the default for new libraries, not a departure from the preset.
  function isContainerOverride(container: string | null) { return container != null && container !== 'source' }

  function resetToPreset() {
    const preset = specFor(form.ruleProfile)
    videoAudioEncodingSelection = null
    form.targetVideoCodec = null
    // Keeping file names is a choice about paths, not a preset override, so a preset leaves it alone.
    if (isContainerOverride(form.targetContainer)) form.targetContainer = null
    form.hdrHandling = preset.hdrHandling
    form.videoAudioCodec = preset.videoAudioCodec ?? 'copy'
    form.videoAudioBitrateKbps = preset.videoAudioBitrateKbps
    form.downmixToStereo = preset.downmixToStereo
  }

  const imageFormats = ['jpeg', 'webp'] as const
  const imagePresetSummaries: Record<string, string> = $derived({
    jpeg: i18n.m.libraries.image_preset_jpeg,
    webp: i18n.m.libraries.image_preset_webp,
  })

  // Downscale UI: a friendly mode picker maps onto the stored (mode, value) pair. The named caps
  // are MaxLongEdge with a fixed pixel value; "custom" exposes the raw long-edge field.
  type DownscaleChoice = 'none' | '4k' | '1080p' | 'longedge' | 'percent'
  const downscaleChoice = $derived<DownscaleChoice>(
    form.imageDownscaleMode === 'Percent'
      ? 'percent'
      : form.imageDownscaleMode === 'MaxLongEdge'
        ? form.imageDownscaleValue === 3840
          ? '4k'
          : form.imageDownscaleValue === 1920
            ? '1080p'
            : 'longedge'
        : 'none',
  )
  function setDownscaleChoice(choice: DownscaleChoice) {
    switch (choice) {
      case 'none':
        form.imageDownscaleMode = 'None'
        form.imageDownscaleValue = 0
        break
      case '4k':
        form.imageDownscaleMode = 'MaxLongEdge'
        form.imageDownscaleValue = 3840
        break
      case '1080p':
        form.imageDownscaleMode = 'MaxLongEdge'
        form.imageDownscaleValue = 1920
        break
      case 'longedge':
        form.imageDownscaleMode = 'MaxLongEdge'
        if (form.imageDownscaleValue < 16) form.imageDownscaleValue = 2560
        break
      case 'percent':
        form.imageDownscaleMode = 'Percent'
        if (form.imageDownscaleValue < 1 || form.imageDownscaleValue > 99) form.imageDownscaleValue = 50
        break
    }
  }

  // One page per topic, each with every control for it. There is no page behind a page.
  type LibraryPage = 'overview' | 'files' | 'video' | 'audio' | 'images' | 'checks' | 'schedule'
  const pageNames = $derived<Record<LibraryPage, string>>({
    overview: i18n.m.libraryWorkflow.overview,
    files: i18n.m.librarySummary.topic_files,
    video: i18n.m.librarySummary.topic_video,
    audio: showVideoOptions ? i18n.m.libraryWorkflow.audio : i18n.m.libraries.audio,
    images: i18n.m.libraries.images,
    checks: i18n.m.librarySummary.topic_checks,
    schedule: i18n.m.libraryWorkflow.automate,
  })
  // A whole topic that cannot apply is left out: a film library has no Images page.
  const pages = $derived<LibraryPage[]>([
    'overview',
    'files',
    ...(showVideoOptions ? ['video' as const] : []),
    ...(showVideoOptions || showAudioOptions ? ['audio' as const] : []),
    ...(showImageOptions && !isTrackCleanupProfile ? ['images' as const] : []),
    'checks',
    'schedule',
  ])
  // Where the fourteen rooms this editor used to have went, so an old bookmark still lands.
  const formerRooms: Record<string, LibraryPage> = {
    source: 'files', 'source/advanced': 'files',
    encode: 'video', 'encode/quality': 'video', 'encode/video': 'video', 'encode/video/advanced': 'video',
    'encode/audio': 'audio', 'encode/audio/advanced': 'audio',
    'encode/images': 'images', 'encode/images/advanced': 'images',
    verify: 'checks', 'verify/advanced': 'checks',
    automate: 'schedule',
  }
  let workflowHeading: HTMLHeadingElement | undefined = $state()
  let saving = $state(false)
  const editorBase = $derived(editingId === 0 ? '/libraries/new' : `/libraries/${editingId}/configure`)
  const page = $derived.by((): LibraryPage => {
    const segment = router.path.slice(editorBase.length + 1)
    const wanted = (Object.hasOwn(pageNames, segment) ? segment : formerRooms[segment]) as LibraryPage | undefined
    if (wanted && pages.includes(wanted)) return wanted
    // An encode page this kind of library does not have: show the one it does.
    if (wanted === 'video' || wanted === 'audio' || wanted === 'images') {
      return (['video', 'audio', 'images'] as const).find((candidate) => pages.includes(candidate)) ?? 'overview'
    }
    return editingId === 0 ? 'files' : 'overview'
  })
  const editorTitle = $derived(activeTab === 'candidates' ? i18n.m.libraries.tab_candidates : activeTab === 'excluded' ? i18n.m.libraries.tab_excluded : page === 'overview' ? form.name || i18n.m.libraries.add_library : pageNames[page])
  function goPage(next: LibraryPage, anchor?: string) {
    activeTab = 'rules'
    router.go(next === 'overview' && editingId !== 0 ? editorBase : `${editorBase}/${next}`)
    if (!anchor) return
    // Changing page scrolls to the top once its fields are in place; a link to one section has to
    // win over that, and still work when the section is already on screen.
    pendingAnchor = anchor
    void tick().then(scrollToPendingAnchor)
  }
  let pendingAnchor: string | null = null
  function scrollToPendingAnchor() {
    if (!pendingAnchor) return
    const target = document.getElementById(pendingAnchor)
    if (!target) return
    pendingAnchor = null
    target.scrollIntoView({ block: 'start' })
  }
  function confirmLeavingEditor(): boolean {
    const next = window.location.hash.replace(/^#/, '')
    if (next === editorBase || next.startsWith(`${editorBase}/`)) return true
    return confirmDiscardIfDirty()
  }
  // Only the fields unmount between pages. The editor, form and validation stay alive.
  $effect(() => {
    page
    activeTab = 'rules'
    if (editingId === null) return
    void tick().then(() => {
      workflowHeading?.focus({ preventScroll: true })
      if (pendingAnchor) scrollToPendingAnchor()
      else document.querySelector('main')?.scrollTo({ top: 0 })
    })
  })

  $effect(() => {
    void load()
  })

  // Warn before a full page reload/close (refresh, tab close) while edits are unsaved.
  $effect(() => {
    if (editingId === null || !isDirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  })

  // Guard in-app navigation away from this page (e.g. clicking another sidebar item) while the
  // editor has unsaved changes — the same confirm as Cancel. Registered once for the page's life.
  $effect(() => router.guardLeave(confirmLeavingEditor))

  function blankForm(): SaveLibrary {
    return newLibraryDefaults()
  }

  async function load() {
    error = null
    try {
      ;[libraries, options] = await Promise.all([api.libraries(), api.libraryOptions()])
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_load
    }
    // Tallies are a best-effort enhancement of the list; a failure here must not blank the page.
    void loadSummaries()
    void loadRemoteWorkers()
    // Proactively flag any path Optimisarr can't reach/read/write before the user hits a failure.
    void checkAllAccess()
    void checkSaveFolders()

    const routeEditor = requestedEditorId()
    if (routeEditor === 0) {
      startAdd()
    } else if (routeEditor !== null) {
      const library = libraries.find((candidate) => candidate.id === routeEditor)
      if (library) startEdit(library)
      else error = i18n.m.libraries.error_load
    }
  }

  function requestedEditorId(): number | null {
    if (/^\/libraries\/new(?:\/|$)/.test(router.path)) return 0
    const match = router.path.match(/^\/libraries\/(\d+)\/configure(?:\/|$)/)
    return match ? Number(match[1]) : null
  }

  // Per-library filesystem access (exists / readable / writable), keyed by library id.
  let access = $state<Record<number, LibraryAccess>>({})

  // One line for how a library gets its work: the auto-optimise window (or that it is off), and
  // whether verified outputs replace originals on their own.
  async function checkSaveFolders() {
    const saving = libraries.filter((library) => library.moveOnComplete && library.targetFolder)
    const answers = await Promise.all(saving.map(async (library) => {
      try {
        return [library.id, await api.checkTargetFolder({ path: library.targetFolder!, libraryId: library.id, libraryName: library.name, libraryPath: library.path })] as const
      } catch {
        return null
      }
    }))
    refusedSaveFolders = Object.fromEntries(answers.filter((answer) => answer !== null && !answer[1].allowed).map((answer) => answer!))
  }

  function scheduleLabel(library: Library): string {
    const window = library.autoEnqueueWindowStart === library.autoEnqueueWindowEnd
      ? i18n.m.libraries.any_time
      : `${library.autoEnqueueWindowStart}–${library.autoEnqueueWindowEnd}`
    const schedule = library.autoEnqueueEnabled
      ? t(i18n.m.libraries.auto_optimise_window, { window })
      : i18n.m.libraries.auto_optimise_off
    const files = filesChoice(library)
    if (files === 'elsewhere') return `${schedule} · ${i18n.m.filesChoice.badge_elsewhere}`
    return files === 'auto' ? `${schedule} · ${i18n.m.libraries.badge_auto_replace}` : schedule
  }

  function accessMessage(value: LibraryAccess): string {
    if (!value.exists) return i18n.m.libraries.access_missing_detail
    if (!value.readable) return i18n.m.libraries.access_unreadable_detail
    if (!value.writable) return i18n.m.libraries.access_unwritable_detail
    return i18n.m.libraries.access_ok_detail
  }
  async function checkAllAccess() {
    for (const library of libraries) {
      try {
        access[library.id] = await api.libraryAccess(library.id)
      } catch {
        // Best effort: retain the previous result when an access probe is unavailable.
      }
    }
  }

  $effect(() => {
    const timer = setInterval(() => void checkAllAccess(), 60_000)
    return () => clearInterval(timer)
  })

  async function loadRemoteWorkers() {
    try {
      const settings = await api.settings()
      remoteWorkersOn = settings.remoteWorkersAvailable && settings.remoteWorkersEnabled
      superResolutionOn = settings.superResolution?.enabled ?? false
      quarantineRetentionDays = settings.replacementQuarantineRetentionDays
      dryRunOn = settings.dryRunMode
      encoderMode = settings.encoderMode ?? null
    } catch {
      remoteWorkersOn = false
      superResolutionOn = false
      quarantineRetentionDays = 'unavailable'
    }
  }

  async function loadSummaries() {
    try {
      const rows = await api.candidateSummary()
      summaries = Object.fromEntries(rows.map((r) => [r.libraryId, { eligible: r.eligible, skipped: r.skipped }]))
    } catch {
      // Leave whatever tallies we had; the cards simply omit a count.
    }
  }

  // Re-resolve the open library's candidates from its *saved* rules.
  async function loadEditorCandidates(libraryId: number) {
    editorCandidatesLoading = true
    editorCandidatesError = null
    try {
      editorCandidates = await api.candidates(libraryId)
    } catch (err) {
      editorCandidatesError = err instanceof Error ? err.message : i18n.m.libraries.error_load_candidates
    } finally {
      editorCandidatesLoading = false
    }
  }

  async function loadEditorExclusions(libraryId: number) {
    editorExclusionsLoading = true
    editorExclusionsError = null
    try {
      editorExclusions = await api.exclusions(libraryId)
    } catch (err) {
      editorExclusionsError = err instanceof Error ? err.message : i18n.m.libraries.error_load_exclusions
    } finally {
      editorExclusionsLoading = false
    }
  }

  async function unexclude(id: number) {
    try {
      await api.removeExclusion(id)
      if (editingId) {
        await loadEditorExclusions(editingId)
        // The file may now be an eligible candidate again, so refresh that list too.
        await loadEditorCandidates(editingId)
      }
    } catch (err) {
      editorExclusionsError = err instanceof Error ? err.message : i18n.m.libraries.error_remove_exclusion
    }
  }

  // The Excluded tab's selection. Shift-clicking extends the range over the shown rows, and the
  // header checkbox picks every shown row — the whole list, because this list is not paged.
  function toggleExclusionSelection(id: number, extend: boolean) {
    exclusionSelection.toggle(id, exclusionIds, extend)
  }

  function toggleSelectAllExclusions() {
    if (exclusionSelection.isEverySelected(exclusionIds)) exclusionSelection.clear()
    else exclusionSelection.selectAll(exclusionIds)
  }

  function askRemoveExclusions() {
    if (exclusionSelection.count === 0 || exclusionBusy) return
    exclusionOutcome = null
    exclusionConfirmOpen = true
  }

  async function runRemoveExclusions() {
    if (exclusionSelection.count === 0 || exclusionBusy) return
    const ids = [...exclusionSelection.ids]
    exclusionBusy = true
    editorExclusionsError = null
    try {
      const result = await api.batchRemoveExclusions(ids)
      const removed = new Set(result.succeeded)
      // The rows that were lifted leave the list; the ones that failed stay picked, so the
      // operator can try them again without picking them a second time.
      editorExclusions = editorExclusions.filter((exclusion) => !removed.has(exclusion.id))
      exclusionSelection.remove([...removed])
      exclusionOutcome = { succeeded: result.succeeded.length, failures: result.failures }
      if (editingId) {
        // A file that is no longer excluded may be offered for optimisation again.
        await loadEditorCandidates(editingId)
      }
    } catch (err) {
      editorExclusionsError = err instanceof Error ? err.message : i18n.m.libraries.error_remove_exclusion
    } finally {
      exclusionBusy = false
      exclusionConfirmOpen = false
    }
  }

  function startAdd() {
    form = blankForm()
    if (options.mediaTypes.length) form.mediaType = options.mediaTypes[0]
    if (options.ruleProfiles.length) form.ruleProfile = options.ruleProfiles[0]
    vmafCustomSelected = false
    imageScoreCustomSelected = false
    audioDifferenceCustomSelected = false
    soundtrackDifferenceCustomSelected = false
    audioEncodingSelection = null
    videoAudioEncodingSelection = null
    minSizeMb = ''
    sameCodecGb = ''
    activeTab = 'rules'
    editorCandidates = []
    editingId = 0
    readWindowChoice()
    rememberedVmaf = null
    markPristine()
  }

  function startEdit(library: Library) {
    // A cached UI can briefly talk to an older API during a rolling container update, and
    // version-one test/config clients may omit fields introduced with per-library policy.
    // Normalise those omissions before binding controls so the editor remains usable and
    // preserves the same conservative defaults as the API and database migration.
    const defaults = newLibraryDefaults()
    form = {
      name: library.name,
      path: library.path,
      // Older clients use TV while the enum API may return Tv. Select the option actually
      // offered by this server, without changing the media kind or leaving the picker blank.
      mediaType: options.mediaTypes.find(type => type.toLowerCase() === library.mediaType.toLowerCase()) ?? library.mediaType,
      ruleProfile: library.ruleProfile,
      enabled: library.enabled,
      priority: library.priority,
      minFileSizeBytes: library.minFileSizeBytes,
      maxHeight: library.maxHeight,
      videoDownscaleHeight: library.videoDownscaleHeight ?? null,
      maxFrameRate: library.maxFrameRate ?? null,
      cropBlackBars: library.cropBlackBars ?? false,
      reencodeSameCodecAboveBytes: library.reencodeSameCodecAboveBytes,
      skipEfficientSources: library.skipEfficientSources,
      targetVideoCodec: library.targetVideoCodec,
      targetContainer: library.targetContainer,
      hdrHandling: library.hdrHandling,
      optimiseDolbyVision: library.optimiseDolbyVision,
      // Coerced like the hardlink switch: an older server omits the field.
      deinterlaceVideo: library.deinterlaceVideo ?? false,
      noiseReduction: library.noiseReduction ?? 'Off',
      // Coerced like the hardlink switch: an older server omits the field, and undefined
      // must read as off rather than blank the editor.
      superResolutionEnabled: library.superResolutionEnabled ?? false,
      superResolutionQualityCheck: library.superResolutionQualityCheck ?? 'BrokenFramesOnly',
      superResolutionEngine: library.superResolutionEngine ?? null,
      superResolutionModel: library.superResolutionModel ?? null,
      superResolutionScale: library.superResolutionScale ?? null,
      excludePaths: library.excludePaths,
      // Coerced rather than passed through: a two-way binding onto a boolean prop throws
      // props_invalid_value on undefined, which takes the whole editor down rather than
      // degrading. A response that predates this field — an older server, a trimmed payload —
      // must leave the switch off, not blank the page.
      excludeHardLinkedFiles: library.excludeHardLinkedFiles ?? false,
      skipSourceCodecs: library.skipSourceCodecs ?? null,
      qualityCrf: library.qualityCrf,
      encoderPreset: library.encoderPreset,
      // Same coercion as the hardlink switch above, and for the same reason: the adaptive
      // quantisation toggle binds two-way onto a boolean, which throws on undefined and takes the
      // whole editor down. The tune drives a select, so it needs a real member to select.
      contentTune: library.contentTune ?? 'None',
      maxBitrateKbps: library.maxBitrateKbps ?? null,
      minBitrateKbps: library.minBitrateKbps ?? null,
      strongerAdaptiveQuantisation: library.strongerAdaptiveQuantisation ?? false,
      audioTargetCodec: library.audioTargetCodec,
      audioBitrateKbps: library.audioBitrateKbps,
      videoAudioCodec: library.videoAudioCodec,
      videoAudioBitrateKbps: library.videoAudioBitrateKbps,
      downmixToStereo: library.downmixToStereo,
      // A server that predates this setting never sends the field, and the conversion is on by
      // default — so an absent value means "on", not "off".
      normalizeOpusChannelLayout: library.normalizeOpusChannelLayout ?? true,
      keepAudioLanguages: library.keepAudioLanguages,
      keepSubtitleLanguages: library.keepSubtitleLanguages,
      reencodeLossyAudio: library.reencodeLossyAudio,
      targetImageFormat: library.targetImageFormat,
      imageQuality: library.imageQuality,
      reencodeLossyImages: library.reencodeLossyImages,
      imageDownscaleMode: library.imageDownscaleMode,
      imageDownscaleValue: library.imageDownscaleValue,
      moveOnComplete: library.moveOnComplete,
      targetFolder: library.targetFolder,
      moveOverwrite: library.moveOverwrite,
      minVmafHarmonicMean: library.minVmafHarmonicMean,
      minVmafMin: library.minVmafMin,
      vmafQualityGateEnabled: library.vmafQualityGateEnabled,
      minVmafCatastrophicMin: library.minVmafCatastrophicMin,
      clipVmafEnabled: library.clipVmafEnabled,
      vmafFrameSubsample: library.vmafFrameSubsample,
      durationTolerancePercent:
        library.durationTolerancePercent ?? defaults.durationTolerancePercent,
      requireAudioRetained:
        library.requireAudioRetained ?? defaults.requireAudioRetained,
      requireSubtitlesRetained:
        library.requireSubtitlesRetained ?? defaults.requireSubtitlesRetained,
      requireSizeReduction:
        library.requireSizeReduction ?? defaults.requireSizeReduction,
      minimumSizeSavingPercent: library.minimumSizeSavingPercent ?? null,
      maximumSizeSavingPercent: library.maximumSizeSavingPercent ?? null,
      audioLoudnessGateEnabled:
        library.audioLoudnessGateEnabled ?? defaults.audioLoudnessGateEnabled,
      maxLoudnessDriftLufs:
        library.maxLoudnessDriftLufs ?? defaults.maxLoudnessDriftLufs,
      adaptiveAudioQuality: library.adaptiveAudioQuality ?? false,
      adaptiveImageQuality: library.adaptiveImageQuality ?? false,
      adaptiveSoundtrackQuality: library.adaptiveSoundtrackQuality ?? false,
      imagePerceptualReportingEnabled: library.imagePerceptualReportingEnabled ?? false,
      imagePerceptualGateEnabled: library.imagePerceptualGateEnabled ?? false,
      minimumImagePerceptualScore: library.minimumImagePerceptualScore ?? null,
      soundtrackQualityReportingEnabled: library.soundtrackQualityReportingEnabled ?? false,
      soundtrackQualityGateEnabled: library.soundtrackQualityGateEnabled ?? false,
      maximumSoundtrackQualityDistance: library.maximumSoundtrackQualityDistance ?? null,
      audioQualityReportingEnabled: library.audioQualityReportingEnabled ?? false,
      audioQualityGateEnabled: library.audioQualityGateEnabled ?? false,
      maximumAudioQualityDistance: library.maximumAudioQualityDistance ?? null,
      audioClippingGateEnabled:
        library.audioClippingGateEnabled ?? defaults.audioClippingGateEnabled,
      maxTruePeakDbtp:
        library.maxTruePeakDbtp ?? defaults.maxTruePeakDbtp,
      imageQualityGateEnabled:
        library.imageQualityGateEnabled ?? defaults.imageQualityGateEnabled,
      minimumImageSsim:
        library.minimumImageSsim ?? defaults.minimumImageSsim,
      imageMetadataGateEnabled:
        library.imageMetadataGateEnabled ?? defaults.imageMetadataGateEnabled,
      videoQualityStrategy:
        library.videoQualityStrategy ?? defaults.videoQualityStrategy,
      workPlacement: library.workPlacement ?? defaults.workPlacement,
      autoEnqueueEnabled: library.autoEnqueueEnabled,
      autoEnqueueWindowStart: library.autoEnqueueWindowStart,
      autoEnqueueWindowEnd: library.autoEnqueueWindowEnd,
      autoReplace: library.autoReplace,
    }
    minSizeMb = library.minFileSizeBytes != null ? Math.round(library.minFileSizeBytes / BYTES_PER_MB) : ''
    // Six decimals undo the rounding to whole bytes on save, so 0.3 reopens as 0.3 and saving again
    // cannot move a limit, while staying finer than anyone would type.
    sameCodecGb = library.reencodeSameCodecAboveBytes != null ? Number((library.reencodeSameCodecAboveBytes / BYTES_PER_GB).toFixed(6)) : ''
    vmafCustomSelected = false
    imageScoreCustomSelected = false
    audioDifferenceCustomSelected = false
    soundtrackDifferenceCustomSelected = false
    audioEncodingSelection = null
    videoAudioEncodingSelection = null
    activeTab = 'rules'
    editingId = library.id
    readWindowChoice()
    rememberedVmaf = null
    markPristine()
    editorCandidates = []
    editorExclusions = []
    // A new library has its own exclusions, so the previous library's selection is dropped.
    exclusionSelection.clear()
    exclusionOutcome = null
    void loadEditorCandidates(library.id)
    void loadEditorExclusions(library.id)
  }

  function cancelEdit() {
    if (!confirmDiscardIfDirty()) return
    markPristine()
    // Leaving the editor drops the Excluded tab's selection with it.
    exclusionSelection.clear()
    exclusionOutcome = null
    router.go('/libraries')
  }

  function emptyToNull(value: string | null): string | null {
    const trimmed = value?.trim()
    return trimmed ? trimmed : null
  }

  function payload(): SaveLibrary {
    return {
      ...form,
      minFileSizeBytes: minSizeMb === '' ? null : Math.round(Number(minSizeMb) * BYTES_PER_MB),
      reencodeSameCodecAboveBytes: sameCodecGb === '' ? null : Math.round(Number(sameCodecGb) * BYTES_PER_GB),
      maxHeight: form.maxHeight ? Number(form.maxHeight) : null,
      videoDownscaleHeight: form.videoDownscaleHeight == null ? null : Number(form.videoDownscaleHeight),
      maxFrameRate: form.maxFrameRate == null ? null : Number(form.maxFrameRate),
      cropBlackBars: form.cropBlackBars,
      priority: Number(form.priority) || 0,
      targetVideoCodec: emptyToNull(form.targetVideoCodec),
      targetContainer: emptyToNull(form.targetContainer),
      hdrHandling: emptyToNull(form.hdrHandling),
      optimiseDolbyVision: form.optimiseDolbyVision,
      deinterlaceVideo: form.deinterlaceVideo,
      noiseReduction: form.noiseReduction,
      superResolutionEnabled: form.superResolutionEnabled,
      superResolutionQualityCheck: form.superResolutionQualityCheck,
      superResolutionEngine: form.superResolutionEngine,
      superResolutionModel: form.superResolutionModel,
      superResolutionScale: form.superResolutionScale,
      excludePaths: emptyToNull(form.excludePaths),
      excludeHardLinkedFiles: form.excludeHardLinkedFiles,
      workPlacement: form.workPlacement,
      skipSourceCodecs: emptyToNull(form.skipSourceCodecs),
      qualityCrf: form.qualityCrf == null ? null : Number(form.qualityCrf),
      encoderPreset: emptyToNull(form.encoderPreset),
      contentTune: form.contentTune,
      maxBitrateKbps: form.maxBitrateKbps == null ? null : Number(form.maxBitrateKbps),
      minBitrateKbps: form.minBitrateKbps == null ? null : Number(form.minBitrateKbps),
      strongerAdaptiveQuantisation: form.strongerAdaptiveQuantisation,
      audioTargetCodec: emptyToNull(form.audioTargetCodec),
      audioBitrateKbps: toNullableNumber(form.audioBitrateKbps),
      videoAudioCodec: emptyToNull(form.videoAudioCodec)
        ?? (showVideoOptions && soundtrackSearchAvailable && form.adaptiveSoundtrackQuality && form.soundtrackQualityGateEnabled ? soundtrackCodec : null),
      videoAudioBitrateKbps: toNullableNumber(form.videoAudioBitrateKbps),
      keepAudioLanguages: audioLanguageInput.normalised,
      keepSubtitleLanguages: subtitleLanguageInput.normalised,
      targetImageFormat: emptyToNull(form.targetImageFormat),
      imageQuality: toNullableNumber(form.imageQuality),
      imageDownscaleValue: Number(form.imageDownscaleValue) || 0,
      targetFolder: form.moveOnComplete ? emptyToNull(form.targetFolder) : null,
      // Saving elsewhere never replaces, so a leftover "replace automatically" is not sent back.
      autoReplace: form.autoReplace && !form.moveOnComplete,
      minVmafHarmonicMean: toNullableNumber(form.minVmafHarmonicMean),
      minVmafMin: toNullableNumber(form.minVmafMin),
      minVmafCatastrophicMin: toNullableNumber(form.minVmafCatastrophicMin),
      vmafFrameSubsample: toNullableNumber(form.vmafFrameSubsample),
      adaptiveAudioQuality: showAudioOptions && form.adaptiveAudioQuality && form.audioQualityGateEnabled,
      adaptiveImageQuality: showImageOptions && form.adaptiveImageQuality && form.imagePerceptualGateEnabled,
      adaptiveSoundtrackQuality: showVideoOptions && soundtrackSearchAvailable && !!form.adaptiveSoundtrackQuality && !!form.soundtrackQualityGateEnabled,
      imagePerceptualReportingEnabled: showImageOptions && form.imagePerceptualReportingEnabled,
      imagePerceptualGateEnabled: showImageOptions && form.imagePerceptualGateEnabled,
      minimumImagePerceptualScore: toNullableNumber(form.minimumImagePerceptualScore ?? null),
      soundtrackQualityReportingEnabled: showVideoOptions && form.soundtrackQualityReportingEnabled,
      soundtrackQualityGateEnabled: showVideoOptions && form.soundtrackQualityGateEnabled,
      maximumSoundtrackQualityDistance: soundtrackLimitForSave(),
      audioQualityGateEnabled: showAudioOptions && form.audioQualityGateEnabled,
      maximumAudioQualityDistance: toNullableNumber(form.maximumAudioQualityDistance ?? null),
      durationTolerancePercent: Number(form.durationTolerancePercent),
      minimumSizeSavingPercent: form.requireSizeReduction && showVideoOptions && !isNoEncodeProfile
        ? toNullableNumber(form.minimumSizeSavingPercent) : null,
      maximumSizeSavingPercent: form.requireSizeReduction && showVideoOptions && !isNoEncodeProfile
        ? toNullableNumber(form.maximumSizeSavingPercent) : null,
      maxLoudnessDriftLufs: Number(form.maxLoudnessDriftLufs),
      maxTruePeakDbtp: Number(form.maxTruePeakDbtp),
      minimumImageSsim: Number(form.minimumImageSsim),
    }
  }

  function toNullableNumber(value: number | null): number | null {
    if (value === null || (value as unknown) === '') return null
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : null
  }

  function soundtrackLimitForSave(): number | null {
    const limit = toNullableNumber(form.maximumSoundtrackQualityDistance ?? null)
    return form.soundtrackQualityGateEnabled || limit == null || (limit >= 0 && limit <= 1) ? limit : null
  }

  /** Only animevideov3 offers a choice of scale; the other Real-ESRGAN models are fixed 4x networks. */
  function chooseSuperResolutionModel(model: string) {
    form.superResolutionModel = model || null
    // Naming Real-CUGAN is what returns a library to the server's settings: a server reads a missing
    // engine as a client that knows nothing of this choice, and keeps what is saved.
    form.superResolutionEngine = model ? 'realesrgan' : 'realcugan'
    form.superResolutionScale = !model ? null : model === 'realesr-animevideov3' ? form.superResolutionScale ?? 2 : 4
  }

  async function save() {
    if (saving) return
    error = null
    message = null
    if (audioLanguageError
      || subtitleLanguageError
      || encoderEffortError
      || verificationError
      || (!isNoEncodeProfile && vmafError)) return
    saving = true
    try {
      if (editingId === 0) {
        // Replace the current /new step with the canonical editor URL. The
        // route change remounts this keyed page, so carry the success message across that boundary.
        const created = await api.createLibrary(payload())
        const success = t(i18n.m.libraries.added, { name: form.name })
        markPristine()
        sessionStorage.setItem(FLASH_KEY, success)
        router.replace(`/libraries/${created.id}/configure`)
        return
      } else if (editingId) {
        await api.updateLibrary(editingId, payload())
        message = t(i18n.m.libraries.updated, { name: form.name })
      }
      // The saved values are now the baseline, so the form is no longer dirty.
      markPristine()
      await load()
      // Re-resolve what the now-saved rules select, so the Candidates tab reflects this Save.
      if (editingId) await loadEditorCandidates(editingId)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_save
    } finally {
      saving = false
    }
  }

  async function scan(library: Library) {
    busyId = library.id
    error = null
    message = null
    try {
      const summary = await api.scanLibrary(library.id)
      message = t(i18n.m.libraries.scan_result, {
        name: library.name,
        discovered: summary.discovered,
        added: summary.added,
        updated: summary.updated,
        settling: summary.skippedUnsettled,
      })
      await load()
      // A scan changes what's probed, so refresh the open library's candidate list too.
      if (editingId === library.id) await loadEditorCandidates(library.id)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_scan
    } finally {
      busyId = null
    }
  }

  async function enqueue(library: Library) {
    busyId = library.id
    error = null
    message = null
    try {
      const result = await api.enqueueLibrary(library.id)
      message = t(i18n.m.libraries.enqueue_result, {
        name: library.name,
        enqueued: result.enqueued,
        alreadyQueued: result.alreadyQueued,
        ineligible: result.ineligible,
      })
      if (result.importing > 0) message += t(i18n.m.libraries.enqueue_importing, { importing: result.importing })
      message += i18n.m.libraries.enqueue_close
      if (result.enqueued > 0) message += i18n.m.libraries.enqueue_see_queue
      // Enqueued files are no longer offered, so refresh the open library's candidate list.
      if (editingId === library.id) await loadEditorCandidates(library.id)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_enqueue
    } finally {
      busyId = null
    }
  }

  async function remove(library: Library) {
    if (!confirm(t(i18n.m.libraries.confirm_delete, { name: library.name, count: library.fileCount }))) {
      return
    }
    busyId = library.id
    error = null
    try {
      await api.deleteLibrary(library.id)
      message = t(i18n.m.libraries.deleted, { name: library.name })
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_delete
    } finally {
      busyId = null
    }
  }

  // The overview is written from what Save would send, so it describes the library exactly.
  const summaryLines = $derived(summariseLibrary(payload(), {
    m: i18n.m,
    locale: i18n.locale,
    preset: presetRaw,
    retentionDays: quarantineRetentionDays,
    dryRun: dryRunOn,
    upscalingOn: superResolutionOn,
    // The counts come from the saved rules, so they are shown only while nothing is unsaved.
    counts: editingId && !isDirty ? summaries[editingId] ?? null : null,
  }))
  const savedLibrary = $derived(editingId ? libraries.find((library) => library.id === editingId) ?? null : null)
  const pictureChanged: SettingTag[] = $derived([{ text: i18n.m.librarySummary.tag_changes_picture }])
  const tracksRemoved: SettingTag[] = $derived([{ text: i18n.m.librarySummary.tag_removes_tracks }])
  const vmafTargetLabel = $derived.by(() => {
    const level = vmafLevelName(form, i18n.m)
    return level ? t(i18n.m.librarySummary.target_named, { score: form.minVmafHarmonicMean ?? '', level }) : String(form.minVmafHarmonicMean ?? '')
  })

  // Which page holds the control behind a validation message, so "review invalid settings" can lead there.
  const verificationErrorPage = $derived<LibraryPage>(
    verificationError === i18n.m.quality_first.image_no_resize || verificationError === i18n.m.image_quality.validation ? 'images'
      : verificationError === i18n.m.quality_first.audio_no_downmix || verificationError === i18n.m.soundtrack_quality.validation || verificationError === i18n.m.audio_quality.validation ? 'audio'
        : 'checks',
  )
  // The built-in presets side by side, as the server sends them, so none of them is a mystery.
  const presetColumns = $derived(encodeProfiles.flatMap((profile) => options.ruleProfileSpecs.filter((spec) => spec.profile === profile)))
  const presetRows = $derived<{ label: string; value: (spec: RuleProfileSpec) => string }[]>([
    { label: i18n.m.libraryEditor.codec_label, value: (spec) => codecName(spec.codec) },
    { label: i18n.m.libraries.container, value: (spec) => (spec.container ?? '').toUpperCase() },
    { label: i18n.m.libraries.quality_crf, value: (spec) => String(spec.crf ?? '—') },
    { label: i18n.m.libraryEditor.hdr_label, value: (spec) => hdrLabel(spec.hdrHandling) },
    { label: i18n.m.libraryEditor.va_codec_label, value: (spec) => spec.videoAudioCodec ? t(i18n.m.libraries.reencode_to, { codec: codecName(spec.videoAudioCodec) }) : i18n.m.libraryEditor.va_copy },
    { label: i18n.m.libraryEditor.bitrate_label, value: (spec) => spec.videoAudioCodec ? t(i18n.m.soundtrack_quality.search_rate, { bitrate: spec.videoAudioBitrateKbps }) : '—' },
    { label: i18n.m.libraryEditor.surround_label, value: (spec) => spec.downmixToStereo ? i18n.m.libraryEditor.surround_downmix : i18n.m.libraryEditor.surround_keep },
  ])
  const videoSections = $derived([
    { id: 'library-optimisation', title: i18n.m.libraryEditor.what_title },
    ...(isTrackCleanupProfile ? [] : [{ id: 'library-format', title: i18n.m.libraryEditor.format_title }]),
    ...(isRemuxProfile ? [{ id: 'library-remux-skips', title: i18n.m.libraryEditor.remux_skips_title }] : []),
    ...(isNoEncodeProfile ? [] : [
      { id: 'library-quality', title: i18n.m.libraries.quality },
      { id: 'library-picture', title: i18n.m.libraryEditor.picture_title },
      { id: 'library-encoder', title: i18n.m.libraryEditor.encoder_title },
      { id: 'library-super-resolution', title: i18n.m.libraries.super_resolution_section },
    ]),
  ])
</script>

{#if editingId === null}
  <header class="mb-6 flex items-start justify-between gap-4">
    <div>
      <h1 class="page-title">{i18n.m.nav.libraries}</h1>
      <p class="page-subtitle">{i18n.m.libraries.subtitle}</p>
    </div>
    <button class="btn btn-primary" onclick={() => router.go('/libraries/new')}>
      <Icon name="plus" class="h-4 w-4" />
      {i18n.m.libraries.add_library}
    </button>
  </header>
{:else}
  <header class="mb-6">
    <nav class="mb-4 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
      <button class="focus-ring min-h-11 min-w-11 rounded px-1 hover:text-accent" onclick={cancelEdit}>{i18n.m.nav.libraries}</button>
      <span aria-hidden="true">/</span>
      {#if page !== 'overview' || activeTab !== 'rules'}
        <button class="focus-ring min-h-11 min-w-11 rounded px-1 hover:text-accent" onclick={() => goPage('overview')}>{form.name || i18n.m.libraries.add_library}</button>
        <span aria-hidden="true">/</span>
      {/if}
      <span aria-current="page" class="break-words text-ink">{editorTitle}</span>
    </nav>
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h1 class="page-title break-words outline-none" tabindex="-1" bind:this={workflowHeading} data-workflow-heading>{editorTitle}</h1>
        <p class="page-subtitle break-words" data-library-facts>
          {form.path}{#if savedLibrary}{' · '}{plural(savedLibrary.fileCount, i18n.m.libraryEditor.files_count_one, i18n.m.libraryEditor.files_count_other, savedLibrary.fileCount.toLocaleString())}{#if summaries[savedLibrary.id]}{' · '}{t(i18n.m.libraryEditor.can_optimise, { count: summaries[savedLibrary.id].eligible.toLocaleString() })}{/if}{/if}
        </p>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <span class="badge tone-info">{i18n.m.libraries.media_type}: {mediaTypeLabel(form.mediaType, i18n.m)}</span>
        {#if savedLibrary && page === 'overview' && activeTab === 'rules'}
          <button class="btn min-h-11" onclick={() => scan(savedLibrary)} disabled={busyId === savedLibrary.id || !savedLibrary.enabled || isDirty} title={isDirty ? i18n.m.libraries.unsaved : undefined}>
            <Icon name={busyId === savedLibrary.id ? 'rotate' : 'search'} class="h-4 w-4 {busyId === savedLibrary.id ? 'animate-spin' : ''}" />
            {busyId === savedLibrary.id ? i18n.m.libraries.working : i18n.m.libraries.scan}
          </button>
        {/if}
      </div>
    </div>
  </header>
{/if}

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{:else if message}
  <Banner kind="success" class="mb-4">{message}</Banner>
{/if}

{#if pickerOpen}
  <FolderPicker
    initialPath={form.path}
    onSelect={(path) => {
      form.path = path
      pickerOpen = false
    }}
    onClose={() => (pickerOpen = false)}
  />
{/if}

<!-- A limit on measured difference: the named stops, or a number of the operator's own. -->
{#snippet differenceLimit(kind: 'audio' | 'soundtrack')}
  {@const label = kind === 'audio' ? i18n.m.audio_quality.maximum : i18n.m.soundtrack_quality.maximum}
  {@const choice = kind === 'audio'
    ? differenceChoice(form.maximumAudioQualityDistance, audioDifferenceCustomSelected)
    : differenceChoice(form.maximumSoundtrackQualityDistance, soundtrackDifferenceCustomSelected)}
  <SettingRow {label} sentence={i18n.m.libraryEditor.difference_sentence} more={`${i18n.m.audio_quality.limit_hint} ${i18n.m.audio_quality.gate_note}`} controlId="lib-{kind}-quality-stop">
    <div data-quality-limit={kind}>
      <select id="lib-{kind}-quality-stop" class="input" value={choice}
        aria-describedby={choice === '' ? `lib-${kind}-quality-missing` : undefined}
        onchange={(event) => (kind === 'audio' ? setAudioDifference : setSoundtrackDifference)(event.currentTarget.value)}>
        {#if choice === ''}<option value="" disabled>{i18n.m.libraryEditor.limit_choose}</option>{/if}
        {#each audioDifferenceStops as stop}<option value={String(stop)}>{stop === 0 ? i18n.m.libraryEditor.limit_zero : stop}</option>{/each}
        <option value="custom">{i18n.m.libraries.stop_custom}</option>
      </select>
      {#if choice === 'custom'}
        <label class="label mt-3" for="lib-{kind}-quality-limit">{i18n.m.libraries.stop_custom}</label>
        {#if kind === 'audio'}
          <input id="lib-audio-quality-limit" class="input" type="number" min="0" max="1" step="any"
            aria-invalid={verificationError === i18n.m.audio_quality.validation} aria-describedby="lib-verification-error"
            bind:value={form.maximumAudioQualityDistance} />
        {:else}
          <input id="lib-soundtrack-quality-limit" class="input" type="number" min="0" max="1" step="any"
            aria-invalid={verificationError === i18n.m.soundtrack_quality.validation} aria-describedby="lib-verification-error"
            bind:value={form.maximumSoundtrackQualityDistance} />
        {/if}
      {/if}
      {#if choice === ''}<p id="lib-{kind}-quality-missing" class="mt-2 text-xs font-medium text-warn">{i18n.m.quality_first.choose_target}</p>{/if}
    </div>
  </SettingRow>
{/snippet}

<!-- The personal quality check compares real results from this library by eye; it belongs beside the quality settings it informs. -->
{#snippet qualityCheckRow()}
  {#if editingId && editingId > 0}
    <SettingRow label={i18n.m.calibration.eyebrow} sentence={i18n.m.calibration.intro}
      disabledReason={isDirty ? i18n.m.libraryEditor.save_first : null}>
      <button type="button" class="btn min-h-11" disabled={isDirty} onclick={() => router.go(`/libraries/${editingId}/quality-check`)}>{i18n.m.calibration.eyebrow}</button>
    </SettingRow>
  {/if}
{/snippet}

{#snippet verificationAlert(shownOn: LibraryPage)}
  {#if verificationError && verificationErrorPage === shownOn}
    <p id="lib-verification-error" class="mt-3 text-xs text-bad" role="alert">{verificationError}</p>
  {/if}
{/snippet}

{#snippet filesPage()}
  <ConfigSection id="library-details" title={i18n.m.libraries.section_library} description={i18n.m.libraries.section_library_intro}>
    <SettingRow label={i18n.m.libraries.name} sentence={i18n.m.libraryWorkflow.name_hint} controlId="lib-name">
      <input id="lib-name" class="input" placeholder={i18n.m.libraries.name_ph} bind:value={form.name} />
    </SettingRow>
    <SettingRow label={i18n.m.libraryEditor.folder} sentence={i18n.m.libraryEditor.folder_sentence} controlId="lib-path">
      <div class="flex gap-2">
        <input id="lib-path" class="input" readonly placeholder={i18n.m.libraries.path_ph} value={form.path} />
        <button type="button" class="btn min-h-11 flex-shrink-0" onclick={event => { event.currentTarget.focus({ preventScroll: true }); pickerOpen = true }}>{i18n.m.libraries.browse}</button>
      </div>
    </SettingRow>
    <SettingRow label={i18n.m.libraries.media_type} sentence={i18n.m.libraryWorkflow.type_hint} controlId="lib-type">
      <select id="lib-type" class="input" value={form.mediaType} onchange={(event) => setMediaType(event.currentTarget.value)}>
        {#each options.mediaTypes as type}<option value={type}>{mediaTypeLabel(type, i18n.m)}</option>{/each}
      </select>
    </SettingRow>
    <SettingRow label={i18n.m.libraries.enabled_label} sentence={i18n.m.libraries.enabled_hint}>
      <Toggle bare bind:checked={form.enabled} label={i18n.m.libraries.enabled_label} />
    </SettingRow>
  </ConfigSection>

  <ConfigSection id="library-selection" title={i18n.m.libraryEditor.picks_title} description={i18n.m.libraryEditor.picks_intro}>
    {#if !isTrackCleanupProfile}
      <SettingRow label={i18n.m.libraries.min_file_size} sentence={i18n.m.libraries.min_file_size_tip} controlId="lib-minsize">
        <input id="lib-minsize" class="input" type="number" min="0" placeholder={i18n.m.libraryEditor.preset_minimum} bind:value={minSizeMb} />
      </SettingRow>
    {/if}
    {#if showVideoOptions && !isNoEncodeProfile}
      <!-- Skip sources already so efficiently encoded that re-encoding won't shrink them. On by
           default; the size-saving check still protects the original either way. -->
      <SettingRow label={i18n.m.libraries.skip_efficient_label} sentence={i18n.m.libraries.skip_efficient_hint} more={i18n.m.libraries.skip_efficient_tip}>
        <Toggle bare bind:checked={form.skipEfficientSources} label={i18n.m.libraries.skip_efficient_label} />
      </SettingRow>
      <!-- Capture oversized files that already match the target codec (e.g. huge HEVC remuxes
           under an HEVC target). Off by default; the size-saving check still protects the original. -->
      <SettingRow label={i18n.m.libraries.same_codec_label} sentence={i18n.m.libraries.same_codec_hint} more={i18n.m.libraries.same_codec_tip}>
        <label class="flex min-h-11 cursor-pointer items-center justify-end gap-2 text-sm">
          <input type="checkbox" class="checkbox" aria-label={i18n.m.libraries.same_codec_label} checked={sameCodecGb !== ''} onchange={(e) => toggleSameCodec(e.currentTarget.checked)} />
        </label>
        {#if sameCodecGb !== ''}
          <div class="mt-2 flex items-center justify-end gap-2 text-sm">
            <span class="text-ink-3">{i18n.m.libraries.same_codec_when}</span>
            <input class="input w-24" type="number" min="0.1" step="0.1" bind:value={sameCodecGb} aria-label={i18n.m.libraries.same_codec_when} />
            <span class="text-ink-3">{i18n.m.libraries.gb}</span>
          </div>
        {/if}
      </SettingRow>
    {/if}
    <!-- Source-codec exclusions. A fixed set of chips rather than free text: the rule matches
         ffprobe's spelling exactly, so a typed name that is subtly wrong would look configured
         and quietly do nothing. Only the codecs this library's media type can contain are offered. -->
    {#if offeredSourceCodecs.length > 0}
      <SettingRow label={i18n.m.libraries.skip_codecs} sentence={i18n.m.libraries.skip_codecs_hint}
        more={`${i18n.m.libraries.skip_codecs_tip} ${i18n.m.libraries.skip_codecs_on_detail}`} stacked>
        <div class="flex flex-wrap gap-2" role="group" aria-label={i18n.m.libraries.skip_codecs}>
          {#each offeredSourceCodecs as codec (codec)}
            <button type="button" aria-pressed={skippedCodecs.includes(codec)} onclick={() => toggleSkippedCodec(codec)}
              class="choice min-h-11 rounded-full px-3 py-1 font-mono text-xs {skippedCodecs.includes(codec) ? 'choice-selected' : 'text-ink-2'}">{codec}</button>
          {/each}
        </div>
      </SettingRow>
    {/if}
    <SettingRow label={i18n.m.libraries.exclude_paths} sentence={i18n.m.libraries.exclude_paths_tip} controlId="lib-exclude" stacked>
      <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
    </SettingRow>
    <!-- Hardlinks belong with the path exclusions rather than the video settings: every profile
         and every media kind ends in a replacement, so a shared inode is at stake for all of them. -->
    <SettingRow label={i18n.m.libraries.hardlinks_label} sentence={i18n.m.libraries.hardlinks_hint}
      more={`${i18n.m.libraries.hardlinks_tip} ${i18n.m.libraries.hardlinks_on_detail}`}>
      <Toggle bare bind:checked={form.excludeHardLinkedFiles} label={i18n.m.libraries.hardlinks_label} />
    </SettingRow>
  </ConfigSection>
{/snippet}

{#snippet videoPage()}
  <div class="xl:grid xl:grid-cols-[10rem_minmax(0,1fr)] xl:items-start xl:gap-6">
    <nav class="sticky top-2 mb-4 hidden self-start xl:block" aria-label={i18n.m.libraryEditor.on_this_page}>
      <ul class="space-y-1 text-sm">
        {#each videoSections as section (section.id)}
          <li><button type="button" class="focus-ring min-h-11 w-full rounded-lg px-3 text-left text-ink-2 hover:bg-raised" onclick={() => document.getElementById(section.id)?.scrollIntoView({ block: 'start' })}>{section.title}</button></li>
        {/each}
      </ul>
    </nav>
    <div class="min-w-0 space-y-5">
      <ConfigSection id="library-optimisation" title={i18n.m.libraryEditor.what_title} description={i18n.m.libraryEditor.what_intro}>
        <!-- items-start: the three hints differ a lot in length, and a stretched grid left the two
             shorter cards with a block of dead space under their text. -->
        <fieldset class="grid items-start gap-2 grid-cols-[repeat(auto-fit,minmax(min(100%,14rem),1fr))]">
          <legend class="sr-only">{i18n.m.libraryEditor.what_title}</legend>
          {#each [
            { value: 'encode', label: i18n.m.libraries.encode_mode, hint: i18n.m.libraries.encode_mode_hint },
            { value: 'remux', label: i18n.m.libraries.remux_label, hint: i18n.m.libraries.remux_hint },
            { value: 'track-cleanup', label: i18n.m.libraries.track_cleanup_label, hint: i18n.m.libraries.track_cleanup_hint },
          ] as mode}
            <label class="choice min-h-24 rounded-lg p-3 {processingMode === mode.value ? 'choice-selected' : ''}">
              <span class="flex items-start gap-2">
                <input type="radio" name="processing-mode" class="mt-0.5 accent-accent-fill" value={mode.value}
                  checked={processingMode === mode.value} onchange={() => setProcessingMode(mode.value as ProcessingMode)}
                  onclick={() => {
                    // Choosing the mode a library already has applies it again, which is how a codec
                    // left on a clean-up library is cleared. A radio that is already on fires no change.
                    if (processingMode === mode.value && mode.value !== 'encode') setProcessingMode(mode.value as ProcessingMode)
                  }} />
                <span class="min-w-0 break-words">
                  <span class="block text-sm font-medium text-ink">{mode.label}</span>
                  <span class="mt-1 block text-xs font-normal leading-relaxed text-ink-3">{mode.hint}</span>
                </span>
              </span>
            </label>
          {/each}
        </fieldset>

        {#if trackCleanupNeedsLanguages}
          <p class="callout tone-warn mt-3 text-xs">
            {i18n.m.libraryEditor.cleanup_needs_languages}
            <button type="button" class="focus-ring min-h-11 rounded px-1 font-medium underline" onclick={() => goPage('audio', 'library-languages')}>{pageNames.audio}</button>
          </p>
        {/if}

        {#if !isNoEncodeProfile}
          <div class="mt-5 border-t border-line pt-4">
            <SettingRow label={i18n.m.libraryEditor.preset_label} sentence={i18n.m.libraryEditor.preset_sentence} stacked>
              <div class="flex flex-wrap items-center gap-2" role="group" aria-label={i18n.m.libraryEditor.preset_label}>
                {#each encodeProfiles as profile}
                  <button type="button" class="choice min-h-11 rounded-full px-4 text-sm font-medium {form.ruleProfile === profile ? 'choice-selected' : 'text-ink-2'}"
                    aria-pressed={form.ruleProfile === profile} onclick={() => choosePreset(profile)}>
                    {presetName(profile, i18n.m)}
                    <span class="ml-1.5 font-mono text-xs font-normal text-ink-3">{specFor(profile).codec}</span>
                  </button>
                {/each}
              </div>
              <p class="mt-3 flex flex-wrap items-center gap-2 text-xs text-ink-3">
                <span class="badge tone-accent" data-preset-state>{presetState}</span>
                <span>{presetSummaries[form.ruleProfile] ?? ''}</span>
              </p>
              <!-- A preset is only mysterious until its values are on the page. These are the server's own. -->
              <details class="preset-table mt-1" data-preset-table>
                <summary class="focus-ring inline-flex min-h-11 cursor-pointer items-center gap-1.5 rounded text-xs font-medium text-accent">
                  <Icon name="info" class="h-3.5 w-3.5" />{i18n.m.libraryEditor.preset_table}
                </summary>
                <div class="overflow-x-auto">
                  <table class="w-full min-w-[34rem] text-left text-xs">
                    <thead>
                      <tr class="text-ink-3">
                        <td></td>
                        {#each presetColumns as column (column.profile)}<th scope="col" class="px-2 py-2 font-semibold {column.profile === form.ruleProfile ? 'text-accent' : ''}">{presetName(column.profile, i18n.m)}</th>{/each}
                      </tr>
                    </thead>
                    <tbody class="divide-y divide-line-soft text-ink-2">
                      {#each presetRows as presetRow (presetRow.label)}
                        <tr>
                          <th scope="row" class="py-2 pr-2 font-medium text-ink-3">{presetRow.label}</th>
                          {#each presetColumns as column (column.profile)}<td class="px-2 py-2">{presetRow.value(column)}</td>{/each}
                        </tr>
                      {/each}
                    </tbody>
                  </table>
                </div>
                <p class="mt-2 max-w-prose text-xs leading-relaxed text-ink-3">{i18n.m.libraryEditor.preset_leaves}</p>
              </details>
            </SettingRow>
          </div>
        {/if}
      </ConfigSection>

      {#if !isTrackCleanupProfile}
        <ConfigSection id="library-format" title={i18n.m.libraryEditor.format_title} description={i18n.m.libraryEditor.format_intro}>
          {#if !isRemuxProfile}
            <SettingRow label={i18n.m.libraryEditor.codec_label} sentence={i18n.m.libraryEditor.codec_sentence} controlId="lib-codec"
              origin={presetRaw ? presetOrigin('targetVideoCodec', codecName(presetRaw.codec), () => (form.targetVideoCodec = null)) : null}>
              <select id="lib-codec" class="input" value={form.targetVideoCodec ?? presetRaw?.codec ?? ''}
                onchange={(event) => (form.targetVideoCodec = event.currentTarget.value === presetRaw?.codec ? null : event.currentTarget.value)}>
                {#each options.videoCodecs as codec}<option value={codec}>{codecName(codec)}</option>{/each}
              </select>
            </SettingRow>
          {/if}
          <SettingRow label={i18n.m.libraries.container} sentence={i18n.m.libraryEditor.container_sentence} more={i18n.m.libraryEditor.container_more} controlId="lib-container"
            tags={[{ text: form.targetContainer === 'source' ? i18n.m.librarySummary.tag_renames_some : i18n.m.librarySummary.tag_renames }]}
            origin={presetRaw?.container && form.targetContainer !== 'source' ? presetOrigin('targetContainer', presetRaw.container.toUpperCase(), () => (form.targetContainer = null)) : null}>
            <select id="lib-container" class="input" value={form.targetContainer ?? presetRaw?.container ?? ''}
              onchange={(event) => (form.targetContainer = event.currentTarget.value === presetRaw?.container ? null : event.currentTarget.value)}>
              <option value="source">{i18n.m.libraries.container_source}</option>
              {#each options.containers as container}<option value={container}>{container.toUpperCase()}</option>{/each}
            </select>
          </SettingRow>
        </ConfigSection>
      {/if}

      {#if isRemuxProfile}
        <!-- A clean-up does not touch the picture, but the server still skips by height and by
             HDR, so both stay in reach. -->
        <ConfigSection id="library-remux-skips" title={i18n.m.libraryEditor.remux_skips_title} description={i18n.m.libraryEditor.remux_skips_intro}>
          <SettingRow label={i18n.m.libraries.skip_above} sentence={i18n.m.libraryEditor.remux_tall_sentence} more={i18n.m.libraries.skip_above_tip} controlId="lib-maxheight">
            <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
              {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraryEditor.hdr_label} sentence={i18n.m.libraryEditor.remux_hdr_sentence} more={i18n.m.libraries.hdr_dv_tip} controlId="lib-hdr">
            <select id="lib-hdr" class="input" value={form.hdrHandling ?? presetRaw?.hdrHandling ?? ''}
              onchange={(event) => (form.hdrHandling = event.currentTarget.value === presetRaw?.hdrHandling ? null : event.currentTarget.value)}>
              <!-- Nothing is re-encoded, so converting to SDR is not on offer; a saved value still shows. -->
              {#each options.hdrHandlings.filter((hdr) => hdr !== 'TonemapToSdr' || form.hdrHandling === hdr) as hdr}<option value={hdr}>{hdrLabel(hdr)}</option>{/each}
            </select>
          </SettingRow>
        </ConfigSection>
      {/if}
      {#if !isNoEncodeProfile}
        <ConfigSection id="library-quality" title={i18n.m.libraries.quality} description={i18n.m.libraryEditor.quality_intro}>
          <SettingRow label={i18n.m.quality_first.strategy_title} sentence={i18n.m.libraryEditor.quality_how_sentence} more={i18n.m.libraries.quality_strategy_adaptive_cost} controlId="lib-quality-mode"
            tags={qualityMode === 'target' ? [{ text: i18n.m.libraries.experimental, tone: 'accent' }] : []}>
            <select id="lib-quality-mode" class="input" value={qualityMode} onchange={(event) => setQualityMode(event.currentTarget.value as QualityMode)}>
              <option value="target">{i18n.m.libraryEditor.quality_how_target}</option>
              <option value="fixed-checked">{i18n.m.libraryEditor.quality_how_fixed_checked}</option>
              <option value="fixed-unchecked">{i18n.m.libraryEditor.quality_how_fixed_unchecked}</option>
            </select>
          </SettingRow>

          <SettingRow label={i18n.m.libraryEditor.target_label} sentence={i18n.m.libraryEditor.target_sentence} more={i18n.m.settings.vmaf_hint} controlId="lib-vmaf-policy"
            disabledReason={vmafMode === 'off' ? i18n.m.libraryEditor.target_unused : null}>
            <select id="lib-vmaf-policy" class="input" value={vmafMode === 'off' ? 'lossless' : vmafMode} disabled={vmafMode === 'off'}
              onchange={(event) => setVmafMode(event.currentTarget.value as VmafMode)}>
              {#each vmafPresets as level}<option value={level.mode}>{t(i18n.m.libraryEditor.level_score, { level: level.name(i18n.m), score: level.harmonic })}</option>{/each}
              <option value="custom">{i18n.m.libraries.stop_custom}</option>
            </select>
            {#if vmafMode === 'custom'}
              <div class="mt-3 grid gap-3 sm:grid-cols-3">
                <div>
                  <label class="label" for="lib-vmaf-harmonic">{i18n.m.libraryEditor.floor_whole}</label>
                  <input id="lib-vmaf-harmonic" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafHarmonicMean} />
                </div>
                <div>
                  <label class="label" for="lib-vmaf-fifth">{i18n.m.libraryEditor.floor_weakest}</label>
                  <input id="lib-vmaf-fifth" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafMin} />
                </div>
                <div>
                  <label class="label" for="lib-vmaf-catastrophic">{i18n.m.libraryEditor.floor_worst}</label>
                  <input id="lib-vmaf-catastrophic" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafCatastrophicMin} />
                </div>
              </div>
            {:else if vmafMode !== 'off'}
              <div class="mt-2 flex flex-wrap gap-1.5 text-xs" data-vmaf-floors>
                <span class="badge tone-neutral">{i18n.m.libraryEditor.floor_whole} {form.minVmafHarmonicMean}</span>
                <span class="badge tone-neutral">{i18n.m.libraryEditor.floor_weakest} {form.minVmafMin}</span>
                <span class="badge tone-neutral">{i18n.m.libraryEditor.floor_worst} {form.minVmafCatastrophicMin}</span>
              </div>
            {/if}
            {#if vmafError}<p id="lib-vmaf-error" class="mt-2 text-xs text-bad" role="alert">{vmafError}</p>{/if}
          </SettingRow>

          {@const presetCrf = presetRaw?.crf ?? 24}
          {@const crf = form.qualityCrf ?? presetCrf}
          <SettingRow label={qualityMode === 'target' ? i18n.m.libraryEditor.crf_label_start : i18n.m.libraries.quality_crf}
            sentence={qualityMode === 'target' ? i18n.m.libraryEditor.crf_sentence_target : i18n.m.libraryEditor.crf_sentence_fixed} controlId="lib-crf"
            origin={presetOrigin('qualityCrf', String(presetCrf), () => (form.qualityCrf = null))}>
            <div class="grid min-w-0 grid-cols-[auto_minmax(0,1fr)_auto_auto] items-center gap-2">
              <span class="text-xs text-ink-4">{i18n.m.libraries.sharper}</span>
              <input id="lib-crf" class="min-w-0 w-full accent-accent-fill" type="range" min="14" max="40" step="1" value={crf}
                oninput={(event) => (form.qualityCrf = Number(event.currentTarget.value) === presetCrf ? null : Number(event.currentTarget.value))} />
              <span class="text-xs text-ink-4">{i18n.m.libraries.smaller}</span>
              <span class="badge w-10 justify-center tone-accent" data-crf-value>{crf}</span>
            </div>
            {#if options.hardwareQualityHeadroom > 0}
              <p class="mt-2 text-xs leading-relaxed text-ink-3">{t(i18n.m.libraries.quality_hardware_note, { hardware: Math.max(0, crf - options.hardwareQualityHeadroom), offset: options.hardwareQualityHeadroom })}</p>
            {/if}
          </SettingRow>

          <SettingRow label={i18n.m.libraryEditor.sampling_label} sentence={i18n.m.libraryEditor.sampling_sentence}
            more={`${i18n.m.settings.vmaf_clip_hint} ${i18n.m.settings.vmaf_subsample_hint}`} controlId="lib-vmaf-sampling"
            disabledReason={vmafMode === 'off' ? i18n.m.libraryEditor.target_unused : null}>
            <select id="lib-vmaf-sampling" class="input" value={vmafMode === 'off' ? 'samples' : samplingChoice} disabled={vmafMode === 'off'}
              onchange={(event) => setSampling(event.currentTarget.value)}>
              <option value="samples">{i18n.m.queue.vmaf_sampling_three}</option>
              <option value="full">{i18n.m.libraryEditor.sampling_full}</option>
              <option value="full-3">{i18n.m.libraryEditor.sampling_full_third}</option>
              {#if vmafMode !== 'off' && samplingChoice === 'custom'}
                <option value="custom">{t(form.clipVmafEnabled ? i18n.m.libraryEditor.sampling_custom_samples : i18n.m.libraryEditor.sampling_custom_full, { interval: form.vmafFrameSubsample ?? 1 })}</option>
              {/if}
            </select>
          </SettingRow>

          {#if showVideoOptions && !isNoEncodeProfile}{@render qualityCheckRow()}{/if}
        </ConfigSection>

        <ConfigSection id="library-picture" title={i18n.m.libraryEditor.picture_title} description={i18n.m.libraryEditor.picture_intro}>
          <SettingRow label={i18n.m.libraryEditor.tall_label} sentence={i18n.m.libraryEditor.tall_sentence} more={`${i18n.m.libraries.video_downscale_to_tip} ${i18n.m.libraries.skip_above_tip}`}
            tags={form.videoDownscaleHeight ? pictureChanged : []}>
            <label class="label" for="lib-downscale">{i18n.m.libraries.video_downscale_to}</label>
            <select id="lib-downscale" class="input" bind:value={form.videoDownscaleHeight}>
              <option value={null}>{i18n.m.libraries.video_downscale_none}</option>
              {#each resolutionLimits.filter((l) => l.value != null) as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
            <label class="label mt-3" for="lib-maxheight">{i18n.m.libraries.skip_above}</label>
            <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
              {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraryEditor.fps_label} sentence={i18n.m.libraryEditor.fps_sentence} more={i18n.m.libraries.video_fps_cap_tip} controlId="lib-fps-cap"
            tags={form.maxFrameRate ? pictureChanged : []}>
            <select id="lib-fps-cap" class="input" bind:value={form.maxFrameRate}>
              {#each frameRateCaps as cap}<option value={cap.value}>{cap.label}</option>{/each}
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraries.crop_bars_label} sentence={i18n.m.libraryEditor.crop_sentence}
            more={`${i18n.m.libraries.crop_bars_tip} ${i18n.m.libraries.crop_bars_on_detail}`} tags={form.cropBlackBars ? pictureChanged : []}>
            <Toggle bare bind:checked={form.cropBlackBars} label={i18n.m.libraries.crop_bars_label} />
          </SettingRow>
          <SettingRow label={i18n.m.libraryEditor.hdr_label} sentence={i18n.m.libraryEditor.hdr_sentence} more={i18n.m.libraries.hdr_dv_tip} controlId="lib-hdr"
            tags={(form.hdrHandling ?? presetRaw?.hdrHandling) === 'TonemapToSdr' ? pictureChanged : []}
            origin={presetRaw ? presetOrigin('hdrHandling', hdrLabel(presetRaw.hdrHandling), () => (form.hdrHandling = null)) : null}>
            <select id="lib-hdr" class="input" value={form.hdrHandling ?? presetRaw?.hdrHandling ?? ''}
              onchange={(event) => (form.hdrHandling = event.currentTarget.value === presetRaw?.hdrHandling ? null : event.currentTarget.value)}>
              {#each options.hdrHandlings as hdr}<option value={hdr}>{hdrLabel(hdr)}</option>{/each}
            </select>
          </SettingRow>
          <!-- Dolby Vision is left untouched by default: a re-encode drops the DV layer and a Profile 5
               source comes out green and pink. Opt in only if losing the DV presentation is acceptable. -->
          <SettingRow label={i18n.m.libraryEditor.dv_label} sentence={i18n.m.libraryEditor.dv_sentence} more={i18n.m.libraries.dolby_vision_tip} controlId="lib-dolby-vision"
            tags={form.optimiseDolbyVision ? pictureChanged : []}>
            <select id="lib-dolby-vision" class="input" value={form.optimiseDolbyVision ? 'reencode' : 'skip'} onchange={(event) => (form.optimiseDolbyVision = event.currentTarget.value === 'reencode')}>
              <option value="skip">{i18n.m.libraryEditor.dv_skip}</option>
              <option value="reencode">{i18n.m.libraryEditor.dv_reencode}</option>
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraryEditor.interlaced_label} sentence={i18n.m.libraryEditor.interlaced_sentence} more={i18n.m.libraries.deinterlace_tip} controlId="lib-deinterlace"
            tags={form.deinterlaceVideo ? pictureChanged : []}>
            <select id="lib-deinterlace" class="input" value={form.deinterlaceVideo ? 'deinterlace' : 'leave'} onchange={(event) => (form.deinterlaceVideo = event.currentTarget.value === 'deinterlace')}>
              <option value="leave">{i18n.m.libraryEditor.interlaced_leave}</option>
              <option value="deinterlace">{i18n.m.libraryEditor.interlaced_deinterlace}</option>
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraries.noise_label} sentence={i18n.m.libraryEditor.noise_sentence} more={i18n.m.libraries.noise_tip} controlId="lib-noise"
            tags={form.noiseReduction !== 'Off' ? pictureChanged : []}>
            <select id="lib-noise" class="input" bind:value={form.noiseReduction}>
              <option value="Off">{i18n.m.libraries.noise_off}</option>
              <option value="Light">{i18n.m.libraries.noise_light}</option>
              <option value="Strong">{i18n.m.libraries.noise_strong}</option>
            </select>
          </SettingRow>
        </ConfigSection>

        <ConfigSection id="library-encoder" title={i18n.m.libraryEditor.encoder_title}
          description={encoder ? t(i18n.m.libraryEditor.encoder_intro_known, { encoder: encoderName(encoder) }) : i18n.m.libraryEditor.encoder_intro_unknown}>
          <SettingRow label={i18n.m.libraries.encoder_preset} sentence={i18n.m.libraryEditor.effort_sentence} more={i18n.m.libraries.encoder_preset_tip} controlId="lib-preset">
            <select id="lib-preset" class="input" aria-invalid={encoderEffortError ? 'true' : 'false'}
              aria-describedby={encoderEffortError ? 'lib-encoder-effort-error' : undefined} bind:value={form.encoderPreset}>
              <option value={null}>{i18n.m.libraries.encoder_default}</option>
              {#if (encoderEffortError || encoderEffortIsLegacy) && form.encoderPreset}
                <option value={form.encoderPreset}>
                  {encoderEffortIsLegacy ? t(i18n.m.libraries.encoder_effort_legacy, { value: form.encoderPreset }) : form.encoderPreset}
                </option>
              {/if}
              {#each options.encoderPresets as preset}<option value={preset}>{encoderEffortLabel(preset)}</option>{/each}
            </select>
            {#if encoderEffortError}<p id="lib-encoder-effort-error" class="mt-1 text-xs text-bad" role="alert">{encoderEffortError}</p>{/if}
          </SettingRow>
          <SettingRow label={i18n.m.libraries.max_bitrate} sentence={i18n.m.libraryEditor.max_bitrate_sentence} more={i18n.m.libraries.max_bitrate_tip} controlId="lib-maxbitrate"
            disabledReason={ignoredReason('maxBitrate')} reasonLink={ignoredReason('maxBitrate') ? settingsLink('encoding', i18n.m.settings.room_encoding) : null}>
            <input id="lib-maxbitrate" class="input" type="number" min="100" max="200000" placeholder={i18n.m.libraries.max_bitrate_none}
              disabled={!!ignoredReason('maxBitrate')} bind:value={form.maxBitrateKbps}
              oninput={(event) => { if (event.currentTarget.value === '') form.minBitrateKbps = null }} />
          </SettingRow>
          <SettingRow label={i18n.m.libraries.content_tune} sentence={i18n.m.libraryEditor.tune_sentence} more={i18n.m.libraries.content_tune_tip} controlId="lib-tune"
            disabledReason={ignoredReason('contentTune')} reasonLink={ignoredReason('contentTune') ? settingsLink('encoding', i18n.m.settings.room_encoding) : null}>
            <select id="lib-tune" class="input" disabled={!!ignoredReason('contentTune')} bind:value={form.contentTune}>
              <option value="None">{i18n.m.libraries.encoder_default}</option>
              <option value="Animation">{i18n.m.libraries.content_tune_animation}</option>
              <option value="Grain">{i18n.m.libraries.content_tune_grain}</option>
            </select>
          </SettingRow>
          <SettingRow label={i18n.m.libraries.adaptive_quantisation} sentence={i18n.m.libraryEditor.aq_sentence} more={i18n.m.libraries.adaptive_quantisation_tip}
            disabledReason={ignoredReason('adaptiveQuantisation')} reasonLink={ignoredReason('adaptiveQuantisation') ? settingsLink('encoding', i18n.m.settings.room_encoding) : null}>
            <Toggle bare bind:checked={form.strongerAdaptiveQuantisation} label={i18n.m.libraries.adaptive_quantisation} disabled={!!ignoredReason('adaptiveQuantisation')} />
          </SettingRow>
          <!-- A floor only means something inside the window a cap defines. It stays in view, at
               the bottom, so the reason it does nothing is said rather than left to be guessed. -->
          {@const floorReason = ignoredReason('minBitrate') ?? (form.maxBitrateKbps == null ? i18n.m.libraryEditor.min_bitrate_needs_cap : null)}
          <SettingRow label={i18n.m.libraries.min_bitrate} sentence={i18n.m.libraryEditor.min_bitrate_sentence} more={i18n.m.libraries.min_bitrate_tip} controlId="lib-minbitrate"
            disabledReason={floorReason} reasonLink={ignoredReason('minBitrate') ? settingsLink('encoding', i18n.m.settings.room_encoding) : null}>
            <input id="lib-minbitrate" class="input" type="number" min="100" max={form.maxBitrateKbps ?? undefined} placeholder={i18n.m.libraries.min_bitrate_none}
              disabled={!!floorReason} bind:value={form.minBitrateKbps} />
          </SettingRow>
          {#if !encoder}<p class="mt-4 text-xs leading-relaxed text-ink-3">{i18n.m.libraries.encoder_tuning_support}</p>{/if}
        </ConfigSection>

        <!-- AI super-resolution is experimental and per library: an opted-in library never changes how
             another library's jobs run. While the feature is off in Settings the switch stays in view,
             disabled, with where to turn it on; a saved choice is kept unchanged. -->
        <ConfigSection id="library-super-resolution" title={i18n.m.libraries.super_resolution_section} description={i18n.m.libraries.super_resolution_section_desc}>
          <SettingRow label={i18n.m.libraries.super_resolution_label} sentence={i18n.m.libraryEditor.upscale_sentence}
            more={`${i18n.m.libraries.super_resolution_tip} ${i18n.m.libraries.super_resolution_hint}`}
            tags={form.superResolutionEnabled && superResolutionOn ? pictureChanged : []}
            disabledReason={superResolutionOn ? null : i18n.m.libraryEditor.upscale_off_reason}
            reasonLink={superResolutionOn ? null : settingsLink('super-resolution', i18n.m.settings.room_super_resolution)}>
            <Toggle bare bind:checked={form.superResolutionEnabled} label={i18n.m.libraries.super_resolution_label} disabled={!superResolutionOn} />
          </SettingRow>
          {#if superResolutionOn && form.superResolutionEnabled}
            <SettingRow label={i18n.m.libraries.super_resolution_model} sentence={i18n.m.libraries.super_resolution_model_tip} controlId="library-sr-model">
              <select id="library-sr-model" class="input" value={form.superResolutionModel ?? ''} onchange={event => chooseSuperResolutionModel(event.currentTarget.value)}>
                <option value="">{i18n.m.libraries.super_resolution_model_cugan}</option>
                <option value="realesrgan-x4plus">{i18n.m.libraries.super_resolution_model_live}</option>
                <option value="realesrgan-x4plus-anime">{i18n.m.libraries.super_resolution_model_drawn}</option>
                <option value="realesr-animevideov3">{i18n.m.libraries.super_resolution_model_video}</option>
              </select>
              {#if form.superResolutionModel === 'realesr-animevideov3'}
                <label class="label mt-3" for="library-sr-scale">{i18n.m.libraries.super_resolution_scale}</label>
                <select id="library-sr-scale" class="input" bind:value={form.superResolutionScale}>
                  <option value={2}>{i18n.m.settings.sr_scale_2}</option>
                  <option value={3}>{i18n.m.settings.sr_scale_3}</option>
                  <option value={4}>{i18n.m.settings.sr_scale_4}</option>
                </select>
              {/if}
            </SettingRow>
            <SettingRow label={i18n.m.libraries.super_resolution_quality} sentence={i18n.m.libraries.super_resolution_review_note} more={i18n.m.libraries.super_resolution_quality_tip} controlId="library-sr-quality">
              <select id="library-sr-quality" class="input" bind:value={form.superResolutionQualityCheck}>
                <option value="BrokenFramesOnly">{i18n.m.libraries.super_resolution_quality_broken}</option>
                <option value="FullGate">{i18n.m.libraries.super_resolution_quality_full}</option>
                <option value="Off">{i18n.m.libraries.super_resolution_quality_off}</option>
              </select>
            </SettingRow>
          {/if}
        </ConfigSection>
      {/if}
    </div>
  </div>
{/snippet}

{#snippet bitrateRow(video: boolean)}
  {@const preset = specFor(form.ruleProfile)}
  {@const codec = video ? form.videoAudioCodec ?? presetRaw?.videoAudioCodec ?? 'copy' : form.audioTargetCodec ?? preset.audioTargetCodec}
  {@const copied = codec === 'copy'}
  {@const defaultBitrate = video ? preset.videoAudioBitrateKbps : preset.audioBitrateKbps}
  {@const bitrate = video ? form.videoAudioBitrateKbps : form.audioBitrateKbps}
  {@const mode = audioEncodingMode(codec, bitrate, video ? videoAudioEncodingSelection : audioEncodingSelection, defaultBitrate)}
  <SettingRow label={i18n.m.libraryEditor.bitrate_label} sentence={i18n.m.libraryEditor.bitrate_sentence}
    more={`${i18n.m.audio_encoding.hint} ${video ? i18n.m.libraries.audio_bitrate_tip : i18n.m.libraries.bitrate_tip}`} stacked
    disabledReason={copied ? i18n.m.libraryEditor.audio_unused : null}>
    {#if !copied}
      <AudioEncodingPreset id={video ? 'lib-video-audio-preset' : 'lib-audio-preset'} {codec} {bitrate} {defaultBitrate} {mode} {video}
        onselect={(selected) => chooseAudioEncoding(selected, video)} />
      {#if mode === 'custom'}
        <div class="mt-3 max-w-[16rem]">
          {#if video}
            <label class="label" for="lib-video-audio-bitrate">{i18n.m.libraries.audio_bitrate}</label>
            <input id="lib-video-audio-bitrate" class="input" type="number" min="32" max="512"
              oninput={() => { videoAudioEncodingSelection = 'custom' }} bind:value={form.videoAudioBitrateKbps} />
          {:else}
            <label class="label" for="lib-audio-bitrate">{i18n.m.libraries.bitrate}</label>
            <input id="lib-audio-bitrate" class="input" type="number" min="32" max="512"
              oninput={() => { audioEncodingSelection = 'custom' }} bind:value={form.audioBitrateKbps} />
          {/if}
        </div>
      {/if}
    {/if}
  </SettingRow>
{/snippet}

{#snippet surroundRow(unused: boolean)}
  <SettingRow label={i18n.m.libraryEditor.surround_label} sentence={i18n.m.libraryEditor.surround_sentence} more={i18n.m.libraries.downmix_hint} controlId="lib-surround"
    disabledReason={unused ? i18n.m.libraryEditor.audio_unused : null}>
    <select id="lib-surround" class="input" disabled={unused} value={form.downmixToStereo ? 'downmix' : 'keep'} onchange={(event) => (form.downmixToStereo = event.currentTarget.value === 'downmix')}>
      <option value="keep">{i18n.m.libraryEditor.surround_keep}</option>
      <option value="downmix">{i18n.m.libraryEditor.surround_downmix}</option>
    </select>
  </SettingRow>
{/snippet}

{#snippet opusLayoutRow()}
  <SettingRow label={i18n.m.libraries.normalize_opus_layout_label} sentence={i18n.m.libraryEditor.opus_sentence} more={i18n.m.libraries.normalize_opus_layout_hint}>
    <Toggle bare bind:checked={form.normalizeOpusChannelLayout} label={i18n.m.libraries.normalize_opus_layout_label} />
  </SettingRow>
{/snippet}

{#snippet measureRow(kind: MeasureKind, label: string, sentence: string, more: string)}
  {@const mode = measureMode(kind)}
  {@const targetUnavailable = kind === 'soundtrack' && !soundtrackSearchAvailable}
  <SettingRow {label} {sentence} {more} controlId="lib-{kind}-quality-mode"
    tags={mode === 'off' ? [] : [{ text: i18n.m.libraries.experimental, tone: 'accent' }]}
    disabledReason={targetUnavailable ? i18n.m.libraryEditor.target_needs_conversion : null}>
    <select id="lib-{kind}-quality-mode" class="input" value={mode} onchange={(event) => setMeasureMode(kind, event.currentTarget.value as MeasureMode)}>
      {#each measureModes as option}
        <option value={option.value} disabled={option.value === 'target' && targetUnavailable && mode !== 'target'}>{option.label}</option>
      {/each}
    </select>
  </SettingRow>
{/snippet}

{#snippet audioPage()}
  {@const musicCodec = form.audioTargetCodec ?? specFor(form.ruleProfile).audioTargetCodec}
  {#if showVideoOptions}
    {#if !isNoEncodeProfile}
      <ConfigSection id="library-audio" title={i18n.m.libraries.audio} description={i18n.m.libraries.audio_channels_desc}>
        <SettingRow label={i18n.m.libraryEditor.va_codec_label} sentence={i18n.m.libraryEditor.va_codec_sentence} more={i18n.m.libraries.audio_track_tip} controlId="lib-video-audio-codec"
          origin={presetRaw ? presetOrigin('videoAudioCodec', (presetRaw.videoAudioCodec ?? 'copy') === 'copy' ? i18n.m.libraryEditor.va_copy : codecName(presetRaw.videoAudioCodec), () => changeAudioCodec('', true)) : null}>
          <select id="lib-video-audio-codec" class="input" value={soundtrackCodec}
            onchange={(event) => changeAudioCodec(event.currentTarget.value === (presetRaw?.videoAudioCodec ?? 'copy') ? '' : event.currentTarget.value, true)}>
            <option value="copy">{i18n.m.libraryEditor.va_copy}</option>
            {#each ['aac', 'opus', 'mp3'] as codec}<option value={codec}>{t(i18n.m.libraries.reencode_to, { codec: codecName(codec) })}</option>{/each}
          </select>
        </SettingRow>
        {@render bitrateRow(true)}
        {@render surroundRow(soundtrackCodec === 'copy' && !showAudioOptions)}
        {#if soundtrackCodec === 'opus' || (showAudioOptions && musicCodec === 'opus')}{@render opusLayoutRow()}{/if}
      </ConfigSection>
    {/if}

    <ConfigSection id="library-languages" title={i18n.m.libraryEditor.langs_title} description={i18n.m.libraryEditor.langs_intro}>
      <!-- Keep-languages track removal applies to copied and re-encoded audio alike; tracks with no
           language tag are never removed, and a file where nothing matches is left untouched, so
           the output always keeps at least one audio track. -->
      <SettingRow label={i18n.m.libraries.keep_audio_langs} sentence={i18n.m.libraryEditor.audio_langs_sentence} more={i18n.m.libraries.keep_audio_langs_tip} controlId="lib-keep-audio-languages"
        tags={audioLanguageInput.codes.length > 0 ? tracksRemoved : []}>
        <input id="lib-keep-audio-languages" class="input" type="text" autocomplete="off" autocapitalize="none" spellcheck={false}
          maxlength={MAX_LANGUAGE_LIST_LENGTH} placeholder={i18n.m.libraries.keep_audio_langs_ph}
          aria-invalid={audioLanguageError ? 'true' : 'false'}
          aria-describedby={audioLanguageError ? 'lib-keep-audio-languages-error' : undefined}
          aria-errormessage={audioLanguageError ? 'lib-keep-audio-languages-error' : undefined}
          bind:value={form.keepAudioLanguages} onblur={normaliseAudioLanguageInput} />
        {#if audioLanguageError}
          <p id="lib-keep-audio-languages-error" class="mt-1 text-xs text-bad" role="alert">{audioLanguageError}</p>
        {:else if audioLanguageInput.codes.length > 0}
          <div class="mt-2 flex flex-wrap items-center gap-1.5">
            <span class="text-xs text-ink-4">{i18n.m.libraries.keep_audio_langs_selected}</span>
            {#each audioLanguageInput.codes as code (code)}<span class="badge tone-accent font-mono uppercase">{code}</span>{/each}
          </div>
        {/if}
      </SettingRow>
      <!-- Subtitle removal mirrors the audio rule, with one honest difference: there is no
           keep-at-least-one guard, so a file whose subtitles are all foreign ends with none. -->
      <SettingRow label={i18n.m.libraries.keep_subtitle_langs} sentence={i18n.m.libraryEditor.sub_langs_sentence} more={i18n.m.libraries.keep_subtitle_langs_tip} controlId="lib-keep-subtitle-languages"
        tags={subtitleLanguageInput.codes.length > 0 ? tracksRemoved : []}>
        <input id="lib-keep-subtitle-languages" class="input" type="text" autocomplete="off" autocapitalize="none" spellcheck={false}
          maxlength={MAX_LANGUAGE_LIST_LENGTH} placeholder={i18n.m.libraries.keep_subtitle_langs_ph}
          aria-invalid={subtitleLanguageError ? 'true' : 'false'}
          aria-describedby={subtitleLanguageError ? 'lib-keep-subtitle-languages-error' : undefined}
          aria-errormessage={subtitleLanguageError ? 'lib-keep-subtitle-languages-error' : undefined}
          bind:value={form.keepSubtitleLanguages} onblur={normaliseSubtitleLanguageInput} />
        {#if subtitleLanguageError}
          <p id="lib-keep-subtitle-languages-error" class="mt-1 text-xs text-bad" role="alert">{subtitleLanguageError}</p>
        {:else if subtitleLanguageInput.codes.length > 0}
          <div class="mt-2 flex flex-wrap items-center gap-1.5">
            <span class="text-xs text-ink-4">{i18n.m.libraries.keep_audio_langs_selected}</span>
            {#each subtitleLanguageInput.codes as code (code)}<span class="badge tone-accent font-mono uppercase">{code}</span>{/each}
          </div>
        {/if}
      </SettingRow>
    </ConfigSection>

    {#if !isNoEncodeProfile}
      <ConfigSection id="library-soundtrack-quality" title={i18n.m.libraryEditor.soundtrack_quality_label} description={i18n.m.soundtrack_quality.coverage_note}>
        {@render measureRow('soundtrack', i18n.m.libraryEditor.soundtrack_quality_label, i18n.m.libraryEditor.soundtrack_quality_sentence,
          `${i18n.m.soundtrack_quality.hint} ${i18n.m.soundtrack_quality.gate_hint} ${i18n.m.soundtrack_quality.adaptive_hint}`)}
        {#if form.soundtrackQualityGateEnabled}{@render differenceLimit('soundtrack')}{/if}
      </ConfigSection>
    {/if}
  {/if}

  {#if showAudioOptions}
    <ConfigSection id="library-music" title={showVideoOptions ? i18n.m.libraryEditor.music_title : i18n.m.libraries.audio} description={i18n.m.libraries.music_note}>
      <SettingRow label={i18n.m.libraryEditor.music_codec_label} sentence={i18n.m.libraryEditor.music_codec_sentence} more={i18n.m.libraries.audio_codec_tip} controlId="lib-audio-codec">
        <select id="lib-audio-codec" class="input" value={musicCodec}
          onchange={(event) => changeAudioCodec(event.currentTarget.value === specFor(form.ruleProfile).audioTargetCodec ? '' : event.currentTarget.value)}>
          {#each ['opus', 'aac', 'mp3'] as codec}<option value={codec}>{codecName(codec)}</option>{/each}
        </select>
      </SettingRow>
      {@render bitrateRow(false)}
      <SettingRow label={i18n.m.libraries.reencode_lossy_audio} sentence={i18n.m.libraries.reencode_lossy_audio_hint}>
        <Toggle bare bind:checked={form.reencodeLossyAudio} label={i18n.m.libraries.reencode_lossy_audio} />
      </SettingRow>
      {#if !showVideoOptions || isNoEncodeProfile}
        {@render surroundRow(false)}
        {#if musicCodec === 'opus'}{@render opusLayoutRow()}{/if}
      {/if}
      {@render measureRow('audio', i18n.m.libraryEditor.audio_quality_label, i18n.m.libraryEditor.audio_quality_sentence,
        `${i18n.m.audio_quality.hint} ${i18n.m.audio_quality.gate_hint} ${i18n.m.audio_quality.adaptive_hint}`)}
      {#if form.audioQualityGateEnabled}{@render differenceLimit('audio')}{/if}
      {#if !showVideoOptions || isNoEncodeProfile}{@render qualityCheckRow()}{/if}
    </ConfigSection>
  {/if}
  {@render verificationAlert('audio')}
{/snippet}

{#snippet imagesPage()}
  {@const format = form.targetImageFormat ?? 'jpeg'}
  <ConfigSection id="library-images" title={i18n.m.libraries.images} description={i18n.m.libraries.images_desc}>
    <SettingRow label={i18n.m.libraries.target_format} sentence={imagePresetSummaries[format] ?? i18n.m.libraries.target_format_tip} controlId="lib-image-format">
      <select id="lib-image-format" class="input" value={format} onchange={(event) => (form.targetImageFormat = event.currentTarget.value === 'jpeg' ? null : event.currentTarget.value)}>
        {#each imageFormats.filter((candidate) => candidate === 'jpeg' || options.imageFormats.includes(candidate)) as candidate}
          <option value={candidate}>{codecName(candidate)}</option>
        {/each}
      </select>
    </SettingRow>
    <SettingRow label={i18n.m.libraries.quality} sentence={i18n.m.libraryEditor.image_quality_sentence} controlId="lib-image-quality">
      <input id="lib-image-quality" class="input" type="number" min="1" max="100" step="1" placeholder={String(DEFAULT_IMAGE_QUALITY)} bind:value={form.imageQuality} />
    </SettingRow>
    <SettingRow label={i18n.m.libraries.reencode_lossy_images} sentence={i18n.m.libraries.reencode_lossy_images_hint}>
      <Toggle bare bind:checked={form.reencodeLossyImages} label={i18n.m.libraries.reencode_lossy_images} />
    </SettingRow>
    <!-- Downscale: optional dimension reduction. Aspect ratio is always kept and images are
         never enlarged; an intentional downscale is allowed past verification. -->
    <SettingRow label={i18n.m.libraries.downscale} sentence={i18n.m.libraries.downscale_tip} controlId="lib-image-downscale"
      tags={downscaleChoice !== 'none' ? pictureChanged : []}>
      <select id="lib-image-downscale" class="input" value={downscaleChoice} onchange={(e) => setDownscaleChoice(e.currentTarget.value as DownscaleChoice)}>
        <option value="none">{i18n.m.libraries.downscale_none}</option>
        <option value="4k">{i18n.m.libraries.downscale_4k}</option>
        <option value="1080p">{i18n.m.libraries.downscale_1080p}</option>
        <option value="longedge">{i18n.m.libraries.downscale_longedge}</option>
        <option value="percent">{i18n.m.libraries.downscale_percent}</option>
      </select>
      {#if downscaleChoice === 'longedge'}
        <label class="label mt-3" for="lib-image-longedge">{i18n.m.libraries.max_long_edge}</label>
        <input id="lib-image-longedge" class="input" type="number" min="16" max="100000" step="1" bind:value={form.imageDownscaleValue} />
        <p class="mt-1 text-xs text-ink-4">{i18n.m.libraries.max_long_edge_hint}</p>
      {:else if downscaleChoice === 'percent'}
        <label class="label mt-3" for="lib-image-percent">{i18n.m.libraries.scale_to}</label>
        <input id="lib-image-percent" class="input" type="number" min="1" max="99" step="1" bind:value={form.imageDownscaleValue} />
        <p class="mt-1 text-xs text-ink-4">{i18n.m.libraries.scale_to_hint}</p>
      {/if}
    </SettingRow>
  </ConfigSection>

  <ConfigSection id="library-image-quality" title={i18n.m.libraryEditor.image_quality_label} description={i18n.m.image_quality.limits}>
    {@render measureRow('image', i18n.m.libraryEditor.image_quality_label, i18n.m.libraryEditor.image_quality_measure_sentence,
      `${i18n.m.image_quality.report_hint} ${i18n.m.image_quality.gate_hint} ${i18n.m.image_quality.adaptive_hint}`)}
    {#if form.imagePerceptualGateEnabled}
      <SettingRow label={i18n.m.image_quality.minimum} sentence={i18n.m.quality_first.image_scale} controlId="lib-image-quality-stop">
        <div data-quality-limit="image">
          <select id="lib-image-quality-stop" class="input" value={imageScoreChoice} onchange={(event) => setImageScore(event.currentTarget.value)}>
            {#if imageScoreChoice === ''}<option value="" disabled>{i18n.m.libraryEditor.limit_choose}</option>{/if}
            {#each [i18n.m.quality_first.image_very_high, i18n.m.quality_first.image_high, i18n.m.quality_first.image_medium, i18n.m.quality_first.image_low] as level, index}
              <option value={String(imageScoreStops[index])}>{t(i18n.m.libraryEditor.level_score, { level, score: imageScoreStops[index] })}</option>
            {/each}
            <option value="custom">{i18n.m.libraries.stop_custom}</option>
          </select>
          {#if imageScoreChoice === 'custom'}
            <label class="label mt-3" for="lib-image-perceptual-score">{i18n.m.libraries.stop_custom}</label>
            <input id="lib-image-perceptual-score" class="input" type="number" min="0" max="100" step="0.1"
              aria-invalid={verificationError === i18n.m.image_quality.validation} aria-describedby="lib-verification-error"
              bind:value={form.minimumImagePerceptualScore} />
          {/if}
          {#if imageScoreChoice === ''}<p class="mt-2 text-xs font-medium text-warn">{i18n.m.quality_first.choose_target}</p>{/if}
          {#if form.adaptiveImageQuality && form.imageQualityGateEnabled}
            <p class="callout tone-warn mt-3 text-xs">{t(i18n.m.quality_first.ssim_also, { value: form.minimumImageSsim })}</p>
          {/if}
        </div>
      </SettingRow>
    {/if}
    {#if !showVideoOptions && !showAudioOptions}{@render qualityCheckRow()}{/if}
  </ConfigSection>
  {@render verificationAlert('images')}
{/snippet}

<!-- A check that is set on another page: shown here so the list of checks is whole, with the way to it. -->
{#snippet elsewhereCheck(label: string, value: string, target: LibraryPage, anchor: string)}
  <SettingRow {label} sentence={i18n.m.libraryEditor.set_elsewhere}>
    <div class="flex flex-wrap items-center justify-end gap-2">
      <span class="badge tone-neutral" data-check-value>{value}</span>
      <button type="button" class="btn min-h-11" onclick={() => goPage(target, anchor)}>{pageNames[target]}</button>
    </div>
  </SettingRow>
{/snippet}

{#snippet checksPage()}
  <ConfigSection id="library-core-checks" title={i18n.m.libraryEditor.always_title} description={i18n.m.libraryEditor.always_intro}>
    <ul class="space-y-2 text-sm leading-relaxed text-ink-2" data-core-checks>
      {#each showVideoOptions || showAudioOptions
        ? [i18n.m.libraryEditor.always_plays, i18n.m.libraryEditor.always_length, ...(showVideoOptions ? [i18n.m.libraryEditor.always_picture] : [])]
        : [i18n.m.libraryEditor.always_opens] as check}
        <li class="flex items-start gap-2"><Icon name="check" class="mt-1 h-3.5 w-3.5 flex-shrink-0 text-ok" /><span>{check}</span></li>
      {/each}
    </ul>
    {#if showVideoOptions || showAudioOptions}
      <div class="mt-4 border-t border-line pt-4">
        <SettingRow label={i18n.m.settings.duration_tolerance} sentence={i18n.m.settings.duration_tolerance_tip} controlId="lib-duration-tolerance">
          <div class="flex min-w-0 items-center gap-2">
            <input id="lib-duration-tolerance" class="input min-w-0 flex-1" type="number" min="0" step="0.1"
              aria-invalid={verificationError === i18n.m.settings.validation_duration} aria-describedby="lib-verification-error"
              bind:value={form.durationTolerancePercent} />
            <span class="flex-none text-sm text-ink-3">%</span>
          </div>
        </SettingRow>
      </div>
    {/if}
  </ConfigSection>

  <ConfigSection id="library-verification" title={i18n.m.libraryEditor.optional_title} description={i18n.m.libraryEditor.optional_intro}>
    {#if showVideoOptions || showAudioOptions}
      <SettingRow label={i18n.m.settings.require_audio} sentence={i18n.m.libraryWorkflow.retained_hint}>
        <Toggle bare bind:checked={form.requireAudioRetained} label={i18n.m.settings.require_audio} />
      </SettingRow>
    {/if}
    {#if showVideoOptions}
      <SettingRow label={i18n.m.settings.require_subtitles} sentence={i18n.m.libraryWorkflow.retained_hint}>
        <Toggle bare bind:checked={form.requireSubtitlesRetained} label={i18n.m.settings.require_subtitles} />
      </SettingRow>
    {/if}
    <SettingRow label={i18n.m.settings.require_smaller} sentence={i18n.m.libraryWorkflow.size_hint}
      more={showVideoOptions && !isNoEncodeProfile ? i18n.m.settings.minimum_saving_tip : ''}>
      <Toggle bare bind:checked={form.requireSizeReduction} label={i18n.m.settings.require_smaller} />
      {#if showVideoOptions && !isNoEncodeProfile}
        <div class="mt-2 flex min-w-0 items-center justify-end gap-2 text-sm">
          <label class="text-ink-3" for="lib-minimum-saving">{i18n.m.libraryEditor.by_at_least}</label>
          <input id="lib-minimum-saving" class="input w-24" type="number" min="0.1" max="99" step="0.1" placeholder="—"
            aria-invalid={verificationError === i18n.m.settings.validation_minimum_saving} aria-describedby="lib-verification-error"
            disabled={!form.requireSizeReduction} bind:value={form.minimumSizeSavingPercent} />
          <span class="flex-none text-ink-3">%</span>
        </div>
      {/if}
    </SettingRow>
    {#if showVideoOptions && !isNoEncodeProfile}
      {@render elsewhereCheck(i18n.m.libraryEditor.picture_check_label, vmafMode === 'off' ? i18n.m.libraryEditor.not_checked : t(i18n.m.libraryEditor.must_score, { target: vmafTargetLabel }), 'video', 'library-quality')}
      {@render elsewhereCheck(i18n.m.libraryEditor.soundtrack_quality_label, measureModes.find((option) => option.value === measureMode('soundtrack'))?.label ?? '', 'audio', 'library-soundtrack-quality')}
    {/if}
    {#if showAudioOptions}
      {@render elsewhereCheck(i18n.m.libraryEditor.audio_quality_label, measureModes.find((option) => option.value === measureMode('audio'))?.label ?? '', 'audio', 'library-music')}
    {/if}
    {#if showVideoOptions || showAudioOptions}
      <SettingRow label={i18n.m.settings.loudness_label} sentence={i18n.m.libraryEditor.loudness_sentence} more={i18n.m.settings.loudness_hint}>
        <Toggle bare bind:checked={form.audioLoudnessGateEnabled} label={i18n.m.settings.loudness_label} />
        {#if form.audioLoudnessGateEnabled}
          <div class="mt-2 flex min-w-0 items-center justify-end gap-2 text-sm">
            <label class="text-ink-3" for="lib-loudness-drift">{i18n.m.settings.loudness_max}</label>
            <input id="lib-loudness-drift" class="input w-24" type="number" min="0" step="0.1"
              aria-invalid={verificationError === i18n.m.settings.validation_loudness} aria-describedby="lib-verification-error"
              bind:value={form.maxLoudnessDriftLufs} />
            <span class="flex-none text-ink-3">{i18n.m.settings.lu}</span>
          </div>
        {/if}
      </SettingRow>
      <SettingRow label={i18n.m.settings.clipping_label} sentence={i18n.m.libraryEditor.clipping_sentence} more={`${i18n.m.settings.clipping_hint} ${i18n.m.settings.true_peak_tip}`}>
        <Toggle bare bind:checked={form.audioClippingGateEnabled} label={i18n.m.settings.clipping_label} />
        {#if form.audioClippingGateEnabled}
          <div class="mt-2 flex min-w-0 items-center justify-end gap-2 text-sm">
            <label class="text-ink-3" for="lib-true-peak">{i18n.m.settings.true_peak}</label>
            <input id="lib-true-peak" class="input w-24" type="number" step="0.1"
              aria-invalid={verificationError === i18n.m.settings.validation_true_peak} aria-describedby="lib-verification-error"
              bind:value={form.maxTruePeakDbtp} />
            <span class="flex-none text-ink-3">{i18n.m.settings.dbtp}</span>
          </div>
        {/if}
      </SettingRow>
    {/if}
    {#if showImageOptions}
      {@render elsewhereCheck(i18n.m.libraryEditor.image_quality_label, measureModes.find((option) => option.value === measureMode('image'))?.label ?? '', 'images', 'library-image-quality')}
      <SettingRow label={i18n.m.settings.ssim_label} sentence={i18n.m.libraryEditor.ssim_sentence} more={`${i18n.m.settings.ssim_hint} ${i18n.m.settings.ssim_min_tip}`}>
        <Toggle bare bind:checked={form.imageQualityGateEnabled} label={i18n.m.settings.ssim_label} />
        {#if form.imageQualityGateEnabled}
          <div class="mt-2 flex min-w-0 items-center justify-end gap-2 text-sm">
            <label class="text-ink-3" for="lib-image-ssim">{i18n.m.settings.ssim_min}</label>
            <input id="lib-image-ssim" class="input w-24" type="number" step="0.01" min="0" max="1"
              aria-invalid={verificationError === i18n.m.settings.validation_ssim} aria-describedby="lib-verification-error"
              bind:value={form.minimumImageSsim} />
          </div>
        {/if}
      </SettingRow>
      <SettingRow label={i18n.m.settings.exif_label} sentence={i18n.m.libraryEditor.exif_sentence} more={`${i18n.m.settings.exif_hint} ${i18n.m.settings.exif_note}`}>
        <Toggle bare bind:checked={form.imageMetadataGateEnabled} label={i18n.m.settings.exif_label} />
      </SettingRow>
    {/if}
    {#if showVideoOptions && !isNoEncodeProfile}
      <SettingRow label={i18n.m.settings.maximum_saving} sentence={i18n.m.libraryEditor.max_saving_sentence} more={i18n.m.settings.maximum_saving_tip} controlId="lib-maximum-saving"
        disabledReason={form.requireSizeReduction ? null : i18n.m.libraryEditor.needs_smaller}>
        <div class="flex min-w-0 items-center gap-2">
          <input id="lib-maximum-saving" class="input min-w-0 flex-1" type="number" min="0.1" max="99" step="0.1" placeholder="—"
            aria-invalid={verificationError === i18n.m.settings.validation_maximum_saving || verificationError === i18n.m.settings.validation_saving_order}
            aria-describedby="lib-verification-error" disabled={!form.requireSizeReduction} bind:value={form.maximumSizeSavingPercent} />
          <span class="flex-none text-sm text-ink-3">%</span>
        </div>
      </SettingRow>
    {/if}
    {@render verificationAlert('checks')}
  </ConfigSection>
{/snippet}

{#snippet schedulePage()}
  <ConfigSection id="library-automation" title={i18n.m.libraryEditor.schedule_title} description={i18n.m.libraryEditor.schedule_intro}>
    <SettingRow label={i18n.m.libraries.auto_optimise_label} sentence={i18n.m.libraryEditor.auto_sentence} more={i18n.m.libraries.auto_optimise_hint}>
      <Toggle bare bind:checked={form.autoEnqueueEnabled} label={i18n.m.libraries.auto_optimise_label} />
    </SettingRow>
    <SettingRow label={i18n.m.libraryEditor.when_label} sentence={i18n.m.libraryEditor.when_sentence} controlId="lib-window"
      disabledReason={form.autoEnqueueEnabled ? null : t(i18n.m.libraryEditor.when_unused, { setting: i18n.m.libraries.auto_optimise_label })}>
      <select id="lib-window" class="input" value={windowChoice} disabled={!form.autoEnqueueEnabled} onchange={(event) => setWindowChoice(event.currentTarget.value)}>
        <option value="any">{i18n.m.libraryEditor.when_any}</option>
        <option value="between">{i18n.m.libraryEditor.when_between}</option>
      </select>
      {#if windowChoice === 'between'}
        <div class="mt-3 flex flex-wrap items-end gap-3">
          <div>
            <label class="label" for="lib-auto-start">{i18n.m.libraries.window_start}</label>
            <input id="lib-auto-start" class="input w-32" type="time" disabled={!form.autoEnqueueEnabled} bind:value={form.autoEnqueueWindowStart} />
          </div>
          <div>
            <label class="label" for="lib-auto-end">{i18n.m.libraries.window_end}</label>
            <input id="lib-auto-end" class="input w-32" type="time" disabled={!form.autoEnqueueEnabled} bind:value={form.autoEnqueueWindowEnd} />
          </div>
        </div>
      {/if}
    </SettingRow>
    <SettingRow label={i18n.m.libraries.queue_priority} sentence={i18n.m.libraries.queue_priority_tip} controlId="lib-priority">
      <select id="lib-priority" class="input" bind:value={form.priority}>{#each priorityLevels as level}<option value={level.value}>{level.label}</option>{/each}</select>
    </SettingRow>
    <!-- Where a re-encode may run. Only shown while work can actually go to a worker; the stored
         choice is otherwise moot, and a control that does nothing would only invite a wrong
         conclusion. A library that already holds a non-default value while workers are off says
         so in one line instead, so nothing is silently kept. -->
    {#if (showVideoOptions || isAudioType(form.mediaType)) && remoteWorkersOn}
      <SettingRow label={i18n.m.libraries.placement_label} sentence={i18n.m.libraries.placement_hint} more={i18n.m.libraries.placement_tip} stacked>
        <div class="grid gap-2 md:grid-cols-2" role="radiogroup" aria-label={i18n.m.libraries.placement_label} data-testid="work-placement">
          {#each placements as placement (placement)}
            <label class="choice flex items-start gap-3 rounded-xl p-3 {form.workPlacement === placement ? 'choice-selected' : ''}">
              <input type="radio" name="work-placement" value={placement} class="mt-0.5 h-4 w-4 flex-shrink-0 accent-accent-fill"
                checked={form.workPlacement === placement} onchange={() => (form.workPlacement = placement)} />
              <span>
                <span class="block text-sm font-semibold text-ink">{placementName(placement)}</span>
                <span class="mt-0.5 block text-xs leading-relaxed text-ink-2">{placementDescription(placement)}</span>
              </span>
            </label>
          {/each}
        </div>
        {#if form.videoQualityStrategy === 'AdaptiveVmaf' && form.workPlacement !== 'LocalOnly'}
          <div class="mt-3 rounded-lg border border-line bg-sunken p-3 text-xs leading-relaxed text-ink-2">{i18n.m.libraries.placement_adaptive_note}</div>
        {/if}
      </SettingRow>
    {:else if showVideoOptions && form.workPlacement !== 'Anywhere'}
      <p class="mt-4 text-xs text-ink-3">{t(i18n.m.libraries.placement_kept_but_off, { placement: placementName(form.workPlacement) })}</p>
    {/if}
  </ConfigSection>

  <section class="card p-4 sm:p-6" id="library-files-choice">
    <FilesChoice
      bind:autoReplace={form.autoReplace}
      bind:moveOnComplete={form.moveOnComplete}
      bind:targetFolder={form.targetFolder}
      bind:moveOverwrite={form.moveOverwrite}
      libraryId={editingId || null}
      libraryName={form.name}
      libraryPath={form.path}
      exampleRelativePath={editorCandidates[0]?.relativePath ?? null}
      retentionDays={quarantineRetentionDays}
      dryRun={dryRunOn}
    />
  </section>
{/snippet}

{#snippet overviewPage()}
  <LibrarySummary lines={summaryLines} unsaved={isDirty} onedit={(topic: SummaryTopic) => goPage(topic)} />
  {#if editingId !== null && editingId > 0}
    <section class="card flex flex-wrap items-center justify-between gap-4 p-5 sm:p-6">
      <div class="min-w-0 flex-1"><h2 class="text-base font-semibold">{i18n.m.duplicates.title}</h2><p class="mt-1 text-sm leading-relaxed text-ink-3">{i18n.m.duplicates.entryHint}</p></div>
      <button type="button" class="btn min-h-11" disabled={isDirty || saving} title={isDirty ? i18n.m.libraries.unsaved : undefined} onclick={() => router.go(`/libraries/${editingId}/duplicates`)}><Icon name="copies" class="h-4 w-4" />{i18n.m.duplicates.entry}</button>
    </section>
  {/if}
{/snippet}

{#snippet configForm()}
  <fieldset class="min-w-0 space-y-5" disabled={saving} data-library-workflow data-library-page={page}>
    {#if page === 'overview'}{@render overviewPage()}
    {:else if page === 'files'}{@render filesPage()}
    {:else if page === 'video'}{@render videoPage()}
    {:else if page === 'audio'}{@render audioPage()}
    {:else if page === 'images'}{@render imagesPage()}
    {:else if page === 'checks'}{@render checksPage()}
    {:else}{@render schedulePage()}{/if}
  </fieldset>
  {#if audioLanguageError || subtitleLanguageError || encoderEffortError || verificationError || (!isNoEncodeProfile && vmafError) || targetFolderProblem}
    <div class="callout tone-warn mt-5" role="alert">
      <p class="text-sm font-medium">{i18n.m.libraryWorkflow.review_errors}</p>
      <div class="mt-2 flex flex-wrap gap-2">
        {#if audioLanguageError || subtitleLanguageError}<button class="btn min-h-11" onclick={() => goPage('audio', 'library-languages')}>{audioLanguageError || subtitleLanguageError}</button>{/if}
        {#if encoderEffortError}<button class="btn min-h-11" onclick={() => goPage('video', 'library-encoder')}>{encoderEffortError}</button>{/if}
        {#if !isNoEncodeProfile && vmafError}<button class="btn min-h-11" onclick={() => goPage('video', 'library-quality')}>{vmafError}</button>{/if}
        {#if verificationError}<button class="btn min-h-11" onclick={() => goPage(verificationErrorPage)}>{verificationError}</button>{/if}
        {#if targetFolderProblem}<button class="btn min-h-11" onclick={() => goPage('schedule', 'library-files-choice')}>{targetFolderProblem}</button>{/if}
      </div>
    </div>
  {/if}
  <!-- Actions stay in the document flow until there is something to save. A dirty form pins them
       on normal-height screens; short landscape viewports deliberately keep them static so the
       action bar cannot consume most of the editor. -->
  <div
    data-library-actions
    class="library-action-bar {isDirty ? 'library-action-bar-dirty' : ''} card z-10 mt-4 flex flex-wrap items-center gap-2 px-4 py-3 sm:px-6"
  >
    <button class="btn btn-primary min-h-11" onclick={save} disabled={!canSave || saving}>
      <Icon name="check" class="h-4 w-4" />
      {saving ? i18n.m.settings.saving : i18n.m.libraries.save}
    </button>
    <button class="btn min-h-11" onclick={cancelEdit} disabled={saving}>
      <Icon name="x" class="h-4 w-4" />
      {i18n.m.libraries.cancel}
    </button>
    <p class="ml-auto text-xs text-ink-3">{#if isDirty}<span class="mb-1 block text-warn">{i18n.m.libraries.unsaved}</span>{/if}{i18n.m.libraryEditor.saved_together}</p>
  </div>
{/snippet}

{#if editingId !== null}
  {#if editingId !== 0}
    <nav class="segmented mb-4" aria-label={i18n.m.libraries.configure}>
      <button class="segment" aria-pressed={activeTab === 'rules'} onclick={() => (activeTab = 'rules')}>{i18n.m.nav.settings}{#if isDirty}<span class="ml-1 text-warn">●</span>{/if}</button>
      <button class="segment" aria-pressed={activeTab === 'candidates'} onclick={() => (activeTab = 'candidates')}><span>{i18n.m.libraries.tab_candidates}{#if !editorCandidatesLoading}{' '}({editorEligibleCount}){/if}</span></button>
      <button class="segment" aria-pressed={activeTab === 'excluded'} onclick={() => { activeTab = 'excluded'; if (editingId) void loadEditorExclusions(editingId) }}><span>{i18n.m.libraries.tab_excluded}{#if !editorExclusionsLoading}{' '}({editorExclusions.length}){/if}</span></button>
    </nav>
  {/if}

  {#if activeTab === 'rules' || editingId === 0}
    <!-- Every page is one step from every other; the one being read is filled. -->
    <nav class="mb-6 flex flex-wrap gap-2" aria-label={i18n.m.libraryEditor.pages_label}>
      {#each pages as candidate (candidate)}
        <button type="button" class="choice min-h-11 rounded-full px-4 text-sm font-medium {page === candidate ? 'bg-accent-fill text-accent-on' : 'text-ink-2'}"
          aria-current={page === candidate ? 'page' : undefined} onclick={() => goPage(candidate)}>{pageNames[candidate]}</button>
      {/each}
    </nav>
    <div class="space-y-4">
      {@render configForm()}
    </div>
  {:else if activeTab === 'candidates'}
    {#if editorCandidatesError}<Banner kind="error" class="mb-3">{editorCandidatesError}</Banner>{/if}
    <p class="mb-3 text-xs text-ink-3">{i18n.m.libraries.candidates_desc_1}<strong>{i18n.m.libraries.candidates_desc_saved}</strong>{i18n.m.libraries.candidates_desc_2}</p>
    {#if editorCandidatesLoading}
      <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
    {:else}
      <CandidateTable
        candidates={editorCandidates}
        scoped
        onchanged={(excluded) => {
          // An excluded file is never offered for optimisation again, so it leaves the list the
          // editor keeps as well — the two agree without a reload that would blank the result.
          editorCandidates = editorCandidates.filter((candidate) => !excluded.includes(candidate.mediaFileId))
        }}
      />
    {/if}
  {:else}
    {#if editorExclusionsError}<Banner kind="error" class="mb-3">{editorExclusionsError}</Banner>{/if}
    {#if exclusionOutcome}
      <BatchResult
        succeeded={exclusionOutcome.succeeded}
        failures={exclusionOutcome.failures}
        title={i18n.m.libraries.batch_unexclude_result}
        ondismiss={() => (exclusionOutcome = null)}
      />
    {/if}
    <p class="mb-3 text-xs text-ink-3">{i18n.m.libraries.excluded_desc_1}<strong>{i18n.m.libraries.excluded_desc_exclude}</strong>{i18n.m.libraries.excluded_desc_2}</p>
    {#if editorExclusionsLoading}
      <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
    {:else if editorExclusions.length === 0}
      <div class="rounded-lg border border-dashed border-line p-8 text-center text-sm text-ink-4">{i18n.m.libraries.excluded_empty_1}<strong>{i18n.m.libraries.excluded_empty_exclude}</strong>{i18n.m.libraries.excluded_empty_2}</div>
    {:else}
      {#if exclusionSelection.count > 0}
        <SelectionToolbar count={exclusionSelection.count} total={exclusionIds.length} onclear={() => exclusionSelection.clear()}>
          {#snippet actions()}
            <button type="button" class="btn" disabled={exclusionBusy} onclick={askRemoveExclusions}>{i18n.m.libraries.batch_unexclude}</button>
          {/snippet}
        </SelectionToolbar>
      {/if}
      <div class="divide-y divide-line-soft rounded-lg border border-line divide-line">
        <div class="flex items-center gap-3 bg-raised px-3 py-2">
          <SelectionCheckbox
            checked={exclusionSelection.isEverySelected(exclusionIds)}
            indeterminate={!exclusionSelection.isEverySelected(exclusionIds) && exclusionSelection.isSomeSelected(exclusionIds)}
            label={i18n.m.selection.select_all}
            onchange={toggleSelectAllExclusions}
          />
          <span class="text-xs text-ink-3">{i18n.m.libraries.excluded_select_hint}</span>
        </div>
        {#each editorExclusions as ex (ex.id)}
          {@const auto = ex.source === 'RepeatedFailures'}
          <div class="flex items-center gap-3 px-3 py-2">
            <SelectionCheckbox
              checked={exclusionSelection.isSelected(ex.id)}
              label={t(i18n.m.selection.select_row, { name: ex.relativePath ?? ex.path })}
              onchange={(extend) => toggleExclusionSelection(ex.id, extend)}
            />
            <div class="min-w-0 flex-1">
              <div class="truncate font-mono text-xs text-ink-2">{ex.relativePath ?? ex.path}</div>
              <div class="mt-0.5 text-xs text-ink-4"><span class={auto ? 'text-warn' : ''}>{auto ? i18n.m.libraries.excluded_auto : i18n.m.libraries.excluded_manual}</span>{#if ex.reason}{` · ${ex.reason}`}{/if} · {new Date(ex.createdAt).toLocaleDateString()}</div>
            </div>
            <button class="btn btn-ghost min-h-11 flex-shrink-0 px-3 text-xs" onclick={() => unexclude(ex.id)}>{i18n.m.libraries.remove}</button>
          </div>
        {/each}
      </div>
    {/if}
  {/if}
{:else if libraries.length > 0}
  <!-- One card per library, as many to a row as fit at a readable width. Each leads with the number that matters (how many
       files) and a plain-words status; preset, schedule and path follow as a short list. Scan is
       the primary button. Exact-copy review is a secondary entry; enqueue, configure and
       delete sit in the menu. -->
  <div class="grid gap-4 grid-cols-[repeat(auto-fill,minmax(min(100%,34rem),1fr))]">
    {#each libraries as library (library.id)}
      {@const summary = summaries[library.id]}
      {@const a = access[library.id]}
      {@const busy = busyId === library.id}
      <div class="card flex flex-col gap-3.5 p-5 {library.enabled ? '' : 'opacity-60'}" data-library-card={library.id}>
        <div class="flex items-center justify-between gap-3">
          <div class="flex min-w-0 flex-wrap items-center gap-2">
            <span class="truncate text-base font-semibold text-ink">{library.name}</span>
            <span class="badge tone-info">{mediaTypeLabel(library.mediaType, i18n.m)}</span>
            {#if library.priority !== 0}
              <span class="badge tone-muted">{t(i18n.m.libraries.badge_priority, { value: library.priority })}</span>
            {/if}
            {#if !library.enabled}
              <span class="badge tone-muted">{i18n.m.libraries.badge_disabled}</span>
            {/if}
            {#if refusedSaveFolders[library.id]}
              <span class="badge tone-warn" data-save-folder-refused>{i18n.m.filesChoice.badge_folder_refused}</span>
            {/if}
          </div>
          <ActionMenu
            label={t(i18n.m.libraries.more_actions, { name: library.name })}
            disabled={busy}
            items={[
              { label: i18n.m.libraries.enqueue, icon: 'plus', title: i18n.m.libraries.enqueue_title, disabled: !library.enabled, onSelect: () => enqueue(library) },
              { label: i18n.m.libraries.configure, icon: 'sliders', onSelect: () => router.go(`/libraries/${library.id}/configure`) },
              { label: i18n.m.libraries.delete, icon: 'trash', danger: true, onSelect: () => remove(library) },
            ]}
          />
        </div>
        {#if refusedSaveFolders[library.id]}
          <p class="text-xs leading-relaxed text-warn" data-save-folder-hint>{i18n.m.filesChoice.badge_folder_refused_hint}</p>
        {/if}

        <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span class="text-3xl font-bold leading-9 tracking-tight tabular-nums text-ink">{library.fileCount.toLocaleString()}</span>
          <span class="text-sm text-ink-3">{i18n.m.libraries.files_label}</span>
          {#if summary}
            <!-- The one place the summary lights up: only when something is actually waiting. -->
            {#if summary.eligible > 0}
              <span class="ml-auto inline-flex items-center gap-1.5 text-xs font-medium text-accent" title={summary.skipped > 0 ? t(i18n.m.libraries.skipped_hint, { count: summary.skipped.toLocaleString() }) : undefined}>
                <Icon name="plus" class="h-3.5 w-3.5" />
                {t(i18n.m.libraries.ready_to_optimise, { count: summary.eligible.toLocaleString() })}
              </span>
            {:else}
              <span class="ml-auto inline-flex items-center gap-1.5 text-xs text-ink-3" title={summary.skipped > 0 ? t(i18n.m.libraries.skipped_hint, { count: summary.skipped.toLocaleString() }) : undefined}>
                <Icon name="check" class="h-3.5 w-3.5" />
                {i18n.m.libraries.all_optimal}
              </span>
            {/if}
          {/if}
        </div>

        <div class="flex flex-col gap-1.5 text-xs text-ink-3">
          <div class="flex items-center gap-2"><Icon name="folder" class="h-3.5 w-3.5 flex-shrink-0" /><span class="truncate font-mono">{library.path}</span></div>
          <!-- The rule profile is a video preset; it is meaningless for Music/Photo libraries. -->
          {#if isVideoType(library.mediaType)}
            <div class="flex items-center gap-2"><Icon name="sliders" class="h-3.5 w-3.5 flex-shrink-0" /><span>{profileLabel(library.ruleProfile)}</span></div>
          {/if}
          <div class="flex items-center gap-2"><Icon name="clock" class="h-3.5 w-3.5 flex-shrink-0" /><span>{scheduleLabel(library)}</span></div>
        </div>

        <!-- Access is only worth a line when it is a problem; a healthy path says nothing. -->
        {#if a && !a.ok}
          <div class="flex items-start gap-1.5 text-xs {a.exists && a.readable ? 'text-warn' : 'text-bad'}">
            <Icon name="warning" class="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
            <span>{accessMessage(a)}</span>
          </div>
        {/if}

        <!-- mt-auto pins the actions to the card's foot so cards sharing a row line their buttons up. -->
        <div class="mt-auto flex flex-wrap items-center gap-2 border-t border-line pt-3">
          <button class="btn btn-primary min-h-11" onclick={() => scan(library)} disabled={busy || !library.enabled}>
            <Icon name={busy ? 'rotate' : 'search'} class="h-4 w-4 {busy ? 'animate-spin' : ''}" />
            {busy ? i18n.m.libraries.working : i18n.m.libraries.scan}
          </button>
          <a class="btn btn-ghost min-h-11" href={`#/libraries/${library.id}/duplicates`}><Icon name="copies" class="h-4 w-4" />{i18n.m.duplicates.entry}</a>
          {#if library.lastAutoEnqueueAt}
            <span class="ml-auto text-xs tabular-nums text-ink-4">{t(i18n.m.libraries.last_run, { date: new Date(library.lastAutoEnqueueAt).toLocaleString() })}</span>
          {/if}
        </div>
      </div>
    {/each}
  </div>
{:else}
  <EmptyState icon="folder" title={i18n.m.libraries.empty_title} hint={i18n.m.libraries.empty_hint}>
    <button class="btn btn-primary" onclick={() => router.go('/libraries/new')}>
      <Icon name="plus" class="h-4 w-4" />
      {i18n.m.libraries.add_library}
    </button>
  </EmptyState>
{/if}

<ConfirmDialog
  bind:open={exclusionConfirmOpen}
  title={plural(
    exclusionSelection.count,
    i18n.m.libraries.batch_unexclude_title_one,
    i18n.m.libraries.batch_unexclude_title_other,
    exclusionSelection.count.toLocaleString(),
  )}
  confirmLabel={i18n.m.libraries.batch_unexclude}
  busy={exclusionBusy}
  onconfirm={runRemoveExclusions}
  oncancel={() => { exclusionConfirmOpen = false }}
>
  {#snippet body()}
    <p>{i18n.m.libraries.batch_unexclude_body}</p>
  {/snippet}
</ConfirmDialog>

<style>
  /* Match Settings' readable labels and comfortable control targets while retaining
     the shared card surfaces, theme tokens and full-width room layout. */
  [data-library-workflow] :global(.label) {
    text-transform: none;
    letter-spacing: 0;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--ink-2);
  }
  [data-library-workflow] :global(.input) {
    min-height: 2.75rem;
  }
  .preset-table > summary {
    list-style: none;
  }
  .preset-table > summary::-webkit-details-marker {
    display: none;
  }
  [data-library-workflow] :global([data-config-section]) {
    transition: box-shadow 180ms ease;
  }
  [data-library-workflow] :global([data-config-section]:hover),
  [data-library-workflow] :global([data-config-section]:focus-within) {
    box-shadow: var(--lift-3), inset 0 1px 0 var(--edge);
  }
  @media (prefers-reduced-motion: reduce) {
    [data-library-workflow] :global([data-config-section]) { transition: none; }
  }

  @media (min-height: 501px) {
    .library-action-bar-dirty {
      position: sticky;
      bottom: 0;
    }
  }
</style>
