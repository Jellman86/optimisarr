<script lang="ts">
  import { api, type Candidate, type Exclusion, type Library, type LibraryAccess, type LibraryOptions, type SaveLibrary } from '../api'
  import { router } from '../stores/ui.svelte'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Toggle from '../components/Toggle.svelte'
  import Icon from '../components/Icon.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Banner from '../components/Banner.svelte'
  import EmptyState from '../components/EmptyState.svelte'
  import CandidateTable from '../components/CandidateTable.svelte'

  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions>({
    mediaTypes: [],
    ruleProfiles: [],
    ruleProfileSpecs: [],
    hdrHandlings: [],
    videoCodecs: [],
    containers: [],
    encoderPresets: [],
    imageFormats: [],
  })

  // Named queue-priority levels, so the card uses a dropdown instead of a raw number.
  const priorityLevels = [
    { value: 2, label: 'Highest' },
    { value: 1, label: 'High' },
    { value: 0, label: 'Normal' },
    { value: -1, label: 'Low' },
    { value: -2, label: 'Lowest' },
  ]

  const resolutionLimits = [
    { value: null, label: 'No limit' },
    { value: 2160, label: '2160p (4K)' },
    { value: 1440, label: '1440p' },
    { value: 1080, label: '1080p' },
    { value: 720, label: '720p' },
    { value: 480, label: '480p' },
  ]

  const DEFAULT_CRF = 23
  const DEFAULT_VMAF_HARMONIC = 93
  const DEFAULT_VMAF_MIN = 80
  const DEFAULT_IMAGE_QUALITY = 80

  // Plain-language summary of each preset, shown under the picker so a first-time
  // user can choose without knowing codecs.
  const presetSummaries: Record<string, string> = {
    ConservativeHevc: 'Space-saving HEVC (H.265) in MP4 — plays on virtually all phones, TVs, and Apple devices. A good default. (AAC audio recommended; audio is kept as-is unless you choose otherwise in Advanced.)',
    CompatibilityH264: 'H.264 in MP4 — plays literally everywhere, at the cost of larger files. (AAC audio recommended.)',
    ExperimentalAv1: 'Smallest files using AV1 in MKV, where hardware allows. Slower to encode. (Opus audio recommended.)',
    RemuxCleanup: 'Container cleanup only — no re-encode. Fast and lossless.',
    ScottsSettings: "Scott's Settings — HEVC (H.265) in MP4 at CRF 24, HDR preserved, and audio re-encoded to AAC 96 kbps downmixed to stereo. A compatibility-first, space-saving bundle that avoids CPU-heavy HDR-to-SDR tone mapping (the same AAC 96 kbps stereo target applies to a music library).",
  }

  // The re-encode profiles form a single compatibility→efficiency axis, shown as a slider so the
  // common case is one simple choice; "Scott's Settings" rides along as a named all-in-one preset
  // at the end. Remux/Cleanup is "don't re-encode at all", so it sits as a separate toggle above
  // the slider rather than on the quality axis. The exact codec/container/CRF/audio knobs stay in
  // Advanced options.
  const encodeProfiles = ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings']
  // "Custom" is one stop past the real presets — selecting it hands the codec/container/quality to
  // the operator (set in Advanced) instead of following a preset, so it stays on the same control.
  const encodeStopLabels = ['Compatibility', 'Balanced', 'Efficiency', "Scott's", 'Custom']
  const customStopIndex = encodeProfiles.length

  // Friendly display names for raw rule-profile ids so a badge reads "Scott's Settings", not the
  // PascalCase enum name "ScottsSettings".
  const profileLabels: Record<string, string> = {
    ConservativeHevc: 'Conservative HEVC',
    CompatibilityH264: 'Compatibility H.264',
    ExperimentalAv1: 'Experimental AV1',
    RemuxCleanup: 'Remux / cleanup',
    ScottsSettings: "Scott's Settings",
  }
  function profileLabel(profile: string): string {
    return profileLabels[profile] ?? profile
  }

  // Friendly display names for raw codec ids so a badge reads "HEVC (H.265)", not "hevc".
  const codecLabels: Record<string, string> = { h264: 'H.264', hevc: 'HEVC (H.265)', av1: 'AV1' }
  function prettyCodec(codec: string | null): string {
    if (!codec) return 'No re-encode'
    return codecLabels[codec.toLowerCase()] ?? codec.toUpperCase()
  }
  type PresetSpec = { codec: string; container: string; crf: number | null }
  const FALLBACK_SPEC: PresetSpec = { codec: 'HEVC (H.265)', container: 'MP4', crf: 24 }

  // The concrete codec/container/CRF each preset selects, sourced from the backend's
  // RuleProfileDefaults via /api/library-options so the slider can never drift from what the
  // server actually does. Keyed by RuleProfile name.
  const presetSpecs = $derived.by(() => {
    const map: Record<string, PresetSpec> = {}
    for (const spec of options.ruleProfileSpecs) {
      map[spec.profile] = {
        codec: prettyCodec(spec.codec),
        container: (spec.container ?? '').toUpperCase(),
        crf: spec.crf,
      }
    }
    return map
  })
  function specFor(profile: string): PresetSpec {
    return presetSpecs[profile] ?? presetSpecs.ConservativeHevc ?? FALLBACK_SPEC
  }
  // The effective selection, accounting for any Advanced overrides the operator has set.
  const effectiveVideoSpec = $derived.by(() => {
    const base = specFor(form.ruleProfile)
    return {
      codec: form.targetVideoCodec ? form.targetVideoCodec.toUpperCase() : base.codec,
      container: form.targetContainer ? form.targetContainer.toUpperCase() : base.container,
      crf: form.qualityCrf ?? base.crf,
    }
  })

  function toggleCustomQuality(on: boolean) {
    form.qualityCrf = on ? (form.qualityCrf ?? DEFAULT_CRF) : null
  }

  function toggleCustomImageQuality(on: boolean) {
    form.imageQuality = on ? (form.imageQuality ?? DEFAULT_IMAGE_QUALITY) : null
  }

  function toggleVmafOverride(on: boolean) {
    form.minVmafHarmonicMean = on ? (form.minVmafHarmonicMean ?? DEFAULT_VMAF_HARMONIC) : null
    form.minVmafMin = on ? (form.minVmafMin ?? DEFAULT_VMAF_MIN) : null
  }

  function priorityLabel(value: number): string {
    return priorityLevels.find((level) => level.value === value)?.label ?? 'Normal'
  }


  function hdrLabel(hdr: string): string {
    if (hdr === 'TonemapToSdr') return 'Tonemap to SDR'
    if (hdr === 'Exclude') return 'Exclude (skip HDR)'
    if (hdr === 'Preserve') return 'Preserve HDR'
    return hdr
  }
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)
  let busyId = $state<number | null>(null)
  let pickerOpen = $state(false)
  let targetPickerOpen = $state(false)

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
  // Per-library eligible/skipped tallies for the list cards (counts only — see /api/candidates/summary).
  let summaries = $state<Record<number, { eligible: number; skipped: number }>>({})
  let form = $state<SaveLibrary>(blankForm())
  // Advanced (encoding/eligibility) settings are collapsed by default to keep the
  // common case simple; opened automatically when editing a library that uses them.
  let showAdvanced = $state(false)
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
    return editingId === null || !isDirty || confirm('You have unsaved changes. Discard them?')
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

  // Advanced controls are scoped to the library's media type: video knobs for Film/TV, audio for
  // Music, images for Photo, and everything for a mixed "Other" library that may hold any of them.
  const showVideoOptions = $derived(isVideoType(form.mediaType))
  const showAudioOptions = $derived(isAudioType(form.mediaType))
  const showImageOptions = $derived(isImageType(form.mediaType))

  const isRemuxProfile = $derived(form.ruleProfile === 'RemuxCleanup')

  // Custom mode lets the operator fine-tune codec/container themselves instead of following a
  // preset. It is the honest framing for an override — a deliberate "Custom" config — rather than a
  // caution that the slider's preset is being ignored. Derived from the Custom slider stop OR any
  // codec/container override, so editing those in Advanced reads as Custom with no warning. The
  // slider still sets the baseline that every non-overridden value follows. (resetToPreset is a
  // hoisted function declaration below, so referencing it here is fine.)
  let customSelected = $state(false)
  const isCustom = $derived(
    showVideoOptions && !isRemuxProfile && (customSelected || form.targetVideoCodec != null || form.targetContainer != null),
  )
  function selectPresetMode() {
    customSelected = false
    resetToPreset() // drop overrides so the slider fully describes the config again
  }
  function selectCustomMode() {
    customSelected = true
  }

  // Where the current profile sits on the slider; the Custom stop when the operator is hand-tuning,
  // else the matching preset (Balanced/HEVC for Remux/unknown).
  const encodeStop = $derived(isCustom ? customStopIndex : Math.max(0, encodeProfiles.indexOf(form.ruleProfile)))

  function setEncodeStop(value: string) {
    const index = Number(value)
    // The last stop is "Custom": hand the config to the operator (codec/container/quality in
    // Advanced) instead of a preset, keeping the current profile as the baseline for anything they
    // leave on "Profile default".
    if (index >= customStopIndex) {
      selectCustomMode()
      return
    }

    // Landing on a preset stop is a deliberate choice of that preset, so drop any override and leave
    // Custom mode rather than silently keeping a divergent config.
    customSelected = false
    resetToPreset()
    const profile = encodeProfiles[index] ?? 'ConservativeHevc'
    form.ruleProfile = profile
    // "Scott's Settings" bundles audio + HDR choices the slider can't show on its own. Fill the
    // matching form fields so the Advanced panel honestly reflects what the preset does — the
    // stereo downmix in particular is an explicit per-library switch, so it must be set here to
    // actually take effect rather than only living in the profile's server-side default.
    if (profile === 'ScottsSettings') {
      form.videoAudioCodec = 'aac'
      form.videoAudioBitrateKbps = 96
      form.downmixToStereo = true
      form.hdrHandling = 'Preserve'
    }
  }

  function toggleRemux(checked: boolean) {
    // Ticking Remux switches off re-encoding; unticking returns to the Balanced default.
    form.ruleProfile = checked ? 'RemuxCleanup' : 'ConservativeHevc'
  }

  // The slider only picks a baseline profile; an explicit codec/container override in Advanced
  // takes precedence, so the slider can imply a codec that isn't actually used. Surface that
  // divergence instead of hiding it — the slider stays editable (it still sets the baseline the
  // non-overridden values follow).
  const presetOverridden = $derived(!isRemuxProfile && (form.targetVideoCodec != null || form.targetContainer != null))

  function overrideSummary(): string {
    const parts: string[] = []
    if (form.targetVideoCodec) parts.push(`codec (${form.targetVideoCodec.toUpperCase()})`)
    if (form.targetContainer) parts.push(`container (.${form.targetContainer})`)
    return parts.join(' and ')
  }

  function resetToPreset() {
    form.targetVideoCodec = null
    form.targetContainer = null
  }

  // A photo library gets its own compatibility→efficiency slider — the image counterpart of the
  // video preset — mapping a single choice onto JPEG / WebP / AVIF. It is shown only for Photo
  // libraries (a mixed "Other" library keeps the video slider and sets the format in Advanced).
  const imageFormats = ['jpeg', 'webp', 'avif'] as const
  const showImagePreset = $derived(isImageType(form.mediaType) && !isVideoType(form.mediaType))
  const imageStop = $derived(Math.max(0, imageFormats.indexOf((form.targetImageFormat ?? 'jpeg') as (typeof imageFormats)[number])))
  function setImageStop(value: string) {
    form.targetImageFormat = imageFormats[Number(value)] ?? 'jpeg'
  }
  const imagePresetSummaries: Record<string, string> = {
    jpeg: 'JPEG — maximum compatibility. Displays on every media server and client, including Plex. The safe default; the smallest savings.',
    webp: 'WebP — a good balance. Smaller than JPEG with broad support (Jellyfin, modern browsers and apps), but Plex does not display WebP photos.',
    avif: 'AVIF — maximum efficiency. The smallest files, but only newer clients (e.g. recent Jellyfin) render it; not supported by Plex.',
  }

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
  $effect(() => router.guardLeave(confirmDiscardIfDirty))

  function blankForm(): SaveLibrary {
    return {
      name: '',
      path: '',
      mediaType: 'Film',
      ruleProfile: 'ConservativeHevc',
      enabled: true,
      priority: 0,
      minFileSizeBytes: null,
      maxHeight: null,
      reencodeSameCodecAboveBytes: null,
      skipEfficientSources: true,
      targetVideoCodec: null,
      targetContainer: null,
      hdrHandling: null,
      optimiseDolbyVision: false,
      excludePaths: null,
      qualityCrf: null,
      encoderPreset: null,
      audioTargetCodec: null,
      audioBitrateKbps: null,
      videoAudioCodec: null,
      videoAudioBitrateKbps: null,
      downmixToStereo: false,
      reencodeLossyAudio: false,
      targetImageFormat: null,
      imageQuality: null,
      reencodeLossyImages: false,
      imageDownscaleMode: 'None',
      imageDownscaleValue: 0,
      moveOnComplete: false,
      targetFolder: null,
      moveOverwrite: false,
      minVmafHarmonicMean: null,
      minVmafMin: null,
      autoEnqueueEnabled: false,
      autoEnqueueWindowStart: '00:00',
      autoEnqueueWindowEnd: '00:00',
      autoReplace: false,
    }
  }

  async function load() {
    error = null
    try {
      ;[libraries, options] = await Promise.all([api.libraries(), api.libraryOptions()])
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load libraries'
    }
    // Tallies are a best-effort enhancement of the list; a failure here must not blank the page.
    void loadSummaries()
    // Proactively flag any path Optimisarr can't reach/read/write before the user hits a failure.
    void checkAllAccess()
  }

  // Per-library filesystem access (exists / readable / writable), keyed by library id.
  let access = $state<Record<number, LibraryAccess>>({})
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
      editorCandidatesError = err instanceof Error ? err.message : 'Unable to load candidates'
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
      editorExclusionsError = err instanceof Error ? err.message : 'Unable to load excluded files'
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
      editorExclusionsError = err instanceof Error ? err.message : 'Unable to remove exclusion'
    }
  }

  function startAdd() {
    if (!confirmDiscardIfDirty()) return
    form = blankForm()
    if (options.mediaTypes.length) form.mediaType = options.mediaTypes[0]
    if (options.ruleProfiles.length) form.ruleProfile = options.ruleProfiles[0]
    customSelected = false
    minSizeMb = ''
    sameCodecGb = ''
    showAdvanced = false
    activeTab = 'rules'
    editorCandidates = []
    editingId = 0
    markPristine()
  }

  function startEdit(library: Library) {
    if (!confirmDiscardIfDirty()) return
    form = {
      name: library.name,
      path: library.path,
      mediaType: library.mediaType,
      ruleProfile: library.ruleProfile,
      enabled: library.enabled,
      priority: library.priority,
      minFileSizeBytes: library.minFileSizeBytes,
      maxHeight: library.maxHeight,
      reencodeSameCodecAboveBytes: library.reencodeSameCodecAboveBytes,
      skipEfficientSources: library.skipEfficientSources,
      targetVideoCodec: library.targetVideoCodec,
      targetContainer: library.targetContainer,
      hdrHandling: library.hdrHandling,
      optimiseDolbyVision: library.optimiseDolbyVision,
      excludePaths: library.excludePaths,
      qualityCrf: library.qualityCrf,
      encoderPreset: library.encoderPreset,
      audioTargetCodec: library.audioTargetCodec,
      audioBitrateKbps: library.audioBitrateKbps,
      videoAudioCodec: library.videoAudioCodec,
      videoAudioBitrateKbps: library.videoAudioBitrateKbps,
      downmixToStereo: library.downmixToStereo,
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
      autoEnqueueEnabled: library.autoEnqueueEnabled,
      autoEnqueueWindowStart: library.autoEnqueueWindowStart,
      autoEnqueueWindowEnd: library.autoEnqueueWindowEnd,
      autoReplace: library.autoReplace,
    }
    minSizeMb = library.minFileSizeBytes != null ? Math.round(library.minFileSizeBytes / BYTES_PER_MB) : ''
    sameCodecGb = library.reencodeSameCodecAboveBytes != null ? Math.round(library.reencodeSameCodecAboveBytes / BYTES_PER_GB) : ''
    // A loaded library reads as Custom only when it actually carries a codec/container override
    // (isCustom derives that); the explicit flag starts clear so it doesn't leak between edits.
    customSelected = false
    // Advanced always starts collapsed — the simple choice is up front; expand to reveal knobs.
    showAdvanced = false
    activeTab = 'rules'
    editingId = library.id
    markPristine()
    editorCandidates = []
    editorExclusions = []
    void loadEditorCandidates(library.id)
    void loadEditorExclusions(library.id)
  }

  function cancelEdit() {
    if (!confirmDiscardIfDirty()) return
    editingId = null
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
      priority: Number(form.priority) || 0,
      targetVideoCodec: emptyToNull(form.targetVideoCodec),
      targetContainer: emptyToNull(form.targetContainer),
      hdrHandling: emptyToNull(form.hdrHandling),
      optimiseDolbyVision: form.optimiseDolbyVision,
      excludePaths: emptyToNull(form.excludePaths),
      qualityCrf: form.qualityCrf == null ? null : Number(form.qualityCrf),
      encoderPreset: emptyToNull(form.encoderPreset),
      audioTargetCodec: emptyToNull(form.audioTargetCodec),
      audioBitrateKbps: toNullableNumber(form.audioBitrateKbps),
      videoAudioCodec: emptyToNull(form.videoAudioCodec),
      videoAudioBitrateKbps: toNullableNumber(form.videoAudioBitrateKbps),
      targetImageFormat: emptyToNull(form.targetImageFormat),
      imageQuality: toNullableNumber(form.imageQuality),
      imageDownscaleValue: Number(form.imageDownscaleValue) || 0,
      targetFolder: form.moveOnComplete ? emptyToNull(form.targetFolder) : null,
      minVmafHarmonicMean: toNullableNumber(form.minVmafHarmonicMean),
      minVmafMin: toNullableNumber(form.minVmafMin),
    }
  }

  function toNullableNumber(value: number | null): number | null {
    if (value === null || (value as unknown) === '') return null
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : null
  }

  async function save() {
    error = null
    message = null
    try {
      if (editingId === 0) {
        // Keep the workspace open on the just-created library so its Candidates tab is reachable.
        const created = await api.createLibrary(payload())
        editingId = created.id
        message = `Added library "${form.name}".`
      } else if (editingId) {
        await api.updateLibrary(editingId, payload())
        message = `Updated library "${form.name}".`
      }
      // The saved values are now the baseline, so the form is no longer dirty.
      markPristine()
      await load()
      // Re-resolve what the now-saved rules select, so the Candidates tab reflects this Save.
      if (editingId) await loadEditorCandidates(editingId)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to save library'
    }
  }

  async function scan(library: Library) {
    busyId = library.id
    error = null
    message = null
    try {
      const summary = await api.scanLibrary(library.id)
      message = `"${library.name}": ${summary.discovered} found, ${summary.added} new, ${summary.updated} updated, ${summary.skippedUnsettled} settling.`
      await load()
      // A scan changes what's probed, so refresh the open library's candidate list too.
      if (editingId === library.id) await loadEditorCandidates(library.id)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Scan failed'
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
      message = `"${library.name}": queued ${result.enqueued} job(s) (${result.alreadyQueued} already queued, ${result.ineligible} not eligible`
      if (result.importing > 0) message += `, ${result.importing} held back while Sonarr/Radarr imports`
      message += ').'
      if (result.enqueued > 0) message += ' See the Queue page.'
      // Enqueued files are no longer offered, so refresh the open library's candidate list.
      if (editingId === library.id) await loadEditorCandidates(library.id)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Enqueue failed'
    } finally {
      busyId = null
    }
  }

  async function remove(library: Library) {
    if (!confirm(`Delete "${library.name}"? This removes its ${library.fileCount} inventory entries (your media files are not touched).`)) {
      return
    }
    busyId = library.id
    error = null
    try {
      await api.deleteLibrary(library.id)
      message = `Deleted library "${library.name}".`
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Delete failed'
    } finally {
      busyId = null
    }
  }
</script>

<header class="mb-6 flex items-start justify-between">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Libraries</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">
      One library per content type. Pick a preset and you're done — or open Advanced options to fine-tune the codec, quality, and limits.
    </p>
  </div>
  {#if editingId !== 0}
    <button class="btn btn-primary" onclick={startAdd}>
      <Icon name="plus" class="h-4 w-4" />
      Add library
    </button>
  {/if}
</header>

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

{#if targetPickerOpen}
  <FolderPicker
    initialPath={form.targetFolder ?? ''}
    onSelect={(path) => {
      form.targetFolder = path
      targetPickerOpen = false
    }}
    onClose={() => (targetPickerOpen = false)}
  />
{/if}

{#snippet configForm()}
  <div class="grid gap-4 sm:grid-cols-2">
    <div>
      <label class="label" for="lib-name">Name</label>
      <input id="lib-name" class="input" placeholder="Films" bind:value={form.name} />
    </div>
    <div>
      <label class="label" for="lib-path">Path</label>
      <div class="flex gap-2">
        <input id="lib-path" class="input" readonly placeholder="Choose a folder…" value={form.path} />
        <button type="button" class="btn flex-shrink-0" onclick={() => (pickerOpen = true)}>Browse</button>
      </div>
    </div>
    <div>
      <label class="label" for="lib-type">Media type</label>
      <select id="lib-type" class="input" bind:value={form.mediaType}>
        {#each options.mediaTypes as type}<option value={type}>{type}</option>{/each}
      </select>
    </div>
  </div>

  <!-- Optimisation preset: the simple primary choice, scoped to the library's media type.
       The compatibility→efficiency axis is a *video* decision (it picks H.264/HEVC/AV1), so a
       Music (audio-only) library shows its audio default instead. Exact codec/container/CRF/audio
       knobs live under Advanced options. -->
  <div class="mt-4">
    <div class="flex items-center gap-2">
      <span class="label mb-0">Optimisation preset <InfoTip text="One slider from most-compatible to most-efficient, ending in Custom for a hand-tuned config. Each preset stop picks a researched codec, container, and quality; Scott's Settings preserves HDR and re-encodes audio to AAC 96 kbps stereo. Fine-tune anything in Advanced options." /></span>
    </div>

    {#if showVideoOptions}
      <label class="flex cursor-pointer items-start gap-2 text-sm">
        <input
          type="checkbox"
          class="checkbox mt-0.5"
          checked={isRemuxProfile}
          onchange={(e) => toggleRemux(e.currentTarget.checked)}
        />
        <span>
          Just clean up containers — no re-encode
          <span class="mt-0.5 block text-xs font-normal text-slate-400">
            Fast and lossless: remux into a clean container without touching the video or audio.
          </span>
        </span>
      </label>

      <div class="mt-3 {isRemuxProfile ? 'pointer-events-none opacity-40' : ''}">
        <input
          type="range"
          min="0"
          max={encodeStopLabels.length - 1}
          step="1"
          class="w-full"
          aria-label="Compatibility to efficiency"
          value={encodeStop}
          disabled={isRemuxProfile}
          oninput={(e) => setEncodeStop(e.currentTarget.value)}
        />
        <!-- Every position is explicit: each stop shows the codec it resolves to, with the active
             stop highlighted and its full container/CRF spelled out in the "Selects:" row below. -->
        <div class="mt-1 flex justify-between gap-2 text-xs text-slate-500 dark:text-slate-400">
          {#each encodeStopLabels as stop, i}
            {@const active = !isRemuxProfile && encodeStop === i}
            <span class="flex flex-col {i === 0 ? 'items-start' : i === encodeStopLabels.length - 1 ? 'items-end text-right' : 'items-center text-center'}">
              <span class={active ? 'font-semibold text-slate-700 dark:text-slate-200' : ''}>{stop}</span>
              <span class="text-[10px] {active ? 'text-cyan-700 dark:text-cyan-300' : 'text-slate-400 dark:text-slate-500'}">{i < encodeProfiles.length ? specFor(encodeProfiles[i]).codec : active ? effectiveVideoSpec.codec : '—'}</span>
            </span>
          {/each}
        </div>
      </div>

      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        {isCustom
          ? 'Custom configuration — set the codec, container, and quality yourself in Advanced options. The slider above only sets the baseline for anything left on “Profile default.”'
          : (presetSummaries[form.ruleProfile] ?? 'Custom preset.')}
      </p>

      <!-- Explicit, concrete selection so the slider isn't a mystery. -->
      <div class="mt-2 flex flex-wrap items-center gap-1.5 text-xs">
        <span class="text-slate-400">Selects:</span>
        <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{effectiveVideoSpec.codec}</span>
        {#if !isRemuxProfile}
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{effectiveVideoSpec.container}</span>
          {#if effectiveVideoSpec.crf != null}
            <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">CRF {effectiveVideoSpec.crf}</span>
          {/if}
        {:else}
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{effectiveVideoSpec.container} container</span>
        {/if}
      </div>

      {#if isCustom}
        <!-- Neutral, not amber: a custom config is a deliberate choice, not a warning. -->
        <div class="mt-2 rounded-md border border-slate-200 bg-slate-50 p-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-300">
          {#if presetOverridden}
            <span>Custom — the {overrideSummary()} {form.targetVideoCodec && form.targetContainer ? 'are' : 'is'} set in Advanced options. The slider sets the baseline for everything you haven't overridden.</span>
          {:else}
            <span>Custom — set the codec, container, and quality in Advanced options. The slider sets the baseline for anything left on “Profile default.”</span>
          {/if}
          <button type="button" class="ml-1 font-medium underline" onclick={selectPresetMode}>Use a preset instead</button>
        </div>
      {/if}
    {:else if showImagePreset}
      <!-- Image compatibility→efficiency slider (Photo libraries): JPEG → WebP → AVIF. -->
      <div class="mt-1">
        <input
          class="w-full accent-cyan-600"
          type="range"
          min="0"
          max={imageFormats.length - 1}
          step="1"
          value={imageStop}
          oninput={(e) => setImageStop(e.currentTarget.value)}
          aria-label="Image compatibility to efficiency"
        />
        <div class="mt-1 flex justify-between text-xs text-slate-500 dark:text-slate-400">
          {#each ['JPEG', 'WebP', 'AVIF'] as stop, i}
            <span class={imageStop === i ? 'font-semibold text-slate-700 dark:text-slate-200' : ''}>{stop}</span>
          {/each}
        </div>
        <div class="mt-1 flex justify-between text-[10px] uppercase tracking-wide text-slate-400">
          <span>Most compatible</span>
          <span>Most efficient</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{imagePresetSummaries[form.targetImageFormat ?? 'jpeg']}</p>
        <div class="mt-2 flex flex-wrap items-center gap-1.5 text-xs">
          <span class="text-slate-400">Selects:</span>
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{(form.targetImageFormat ?? 'jpeg').toUpperCase()} (.{(form.targetImageFormat ?? 'jpeg') === 'jpeg' ? 'jpg' : form.targetImageFormat})</span>
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">Quality {form.imageQuality ?? 80}</span>
        </div>
      </div>
    {:else}
      <p class="mt-1 text-xs text-slate-500 dark:text-slate-400">
        Music library — the compatibility/efficiency video preset doesn't apply. Lossless audio is
        re-encoded to Opus 128&nbsp;kbps by default; fine-tune the codec and bitrate in Advanced options.
      </p>
    {/if}
  </div>

  <!-- Simple, always-visible switches. The technical encoding knobs live under
       "Advanced options" so the common case stays uncluttered. -->
  <div class="mt-5 space-y-4 border-t border-slate-200 pt-5 dark:border-slate-700">
    <Toggle bind:checked={form.enabled} label="Library enabled" hint="Included in scans and eligible for the queue." />

    <Toggle
      bind:checked={form.autoEnqueueEnabled}
      label="Optimise automatically"
      hint="Inside the window below, this library's eligible files are queued and run automatically. Scanning is global (Settings → Library scan interval); jobs still obey the concurrency limit and disk/activity gates."
    />
    {#if form.autoEnqueueEnabled}
      <div class="flex flex-wrap items-end gap-4 pl-1">
        <div>
          <label class="label" for="lib-auto-start">Window start</label>
          <input id="lib-auto-start" class="input w-32" type="time" bind:value={form.autoEnqueueWindowStart} />
        </div>
        <div>
          <label class="label" for="lib-auto-end">Window end</label>
          <input id="lib-auto-end" class="input w-32" type="time" bind:value={form.autoEnqueueWindowEnd} />
        </div>
        <p class="max-w-xs text-xs text-slate-500 dark:text-slate-400">
          Equal times = any time. Eligible files are queued and run while inside the window.
        </p>
      </div>
    {/if}

    <Toggle
      bind:checked={form.autoReplace}
      label="Replace automatically when verified"
      hint="When a job passes every verification gate, replace the original without waiting for a manual Replace. The original is still quarantined first and can be rolled back (kept for the quarantine-retention period). Off by default."
    />
  </div>

  <!-- Advanced options: codec / quality / eligibility overrides, hidden by default. The header and
       body form one tinted, bordered "drawer" so the Advanced zone is clearly set apart from the
       simple controls above. -->
  <div class="mt-5 overflow-hidden rounded-xl border {showAdvanced ? 'border-slate-300 dark:border-slate-600' : 'border-slate-200 dark:border-slate-700'}">
    <button
      type="button"
      class="flex w-full items-center gap-2 px-4 py-3 text-sm font-medium text-slate-600 transition-colors hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800/60 {showAdvanced ? 'bg-slate-100/80 dark:bg-slate-800/70' : ''}"
      onclick={() => (showAdvanced = !showAdvanced)}
      aria-expanded={showAdvanced}
    >
      <Icon name="sliders" class="h-4 w-4 text-slate-400" />
      <span>Advanced options</span>
      <span class="text-xs font-normal text-slate-400">codec, quality, limits</span>
      <Icon name="chevron" class="ml-auto h-4 w-4 text-slate-400 transition-transform {showAdvanced ? 'rotate-180' : ''}" />
    </button>

  {#if showAdvanced}
    <!-- divide-y draws a separator between whichever sections are shown for this media type. -->
    <div class="border-t border-slate-200 bg-slate-50/60 px-4 divide-y divide-slate-200 dark:border-slate-700 dark:bg-slate-900/30 dark:divide-slate-800">

      {#if showVideoOptions}
      <!-- VIDEO — scoped to Film/TV/Other libraries. -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Video</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">How video files are re-encoded. Leave a control on “Profile default” to follow the preset.</p>

        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-codec">Target codec <InfoTip text="The video codec a re-encode targets. Leave on “Profile default” to follow the preset (HEVC for Balanced/Scott's, H.264 for Compatibility, AV1 for Efficiency)." /></label>
            <select id="lib-codec" class="input" bind:value={form.targetVideoCodec}>
              <option value={null}>Profile default</option>
              {#each options.videoCodecs as codec}<option value={codec}>{codec.toUpperCase()}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-container">Container <InfoTip text="The output container the file is muxed into. MP4 is the most compatible; MKV carries image-based subtitles (Blu-ray/DVD) that MP4 can't. Leave on “Profile default” to follow the preset." /></label>
            <select id="lib-container" class="input" bind:value={form.targetContainer}>
              <option value={null}>Profile default</option>
              {#each options.containers as container}<option value={container}>.{container}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-hdr">HDR / Dolby Vision <InfoTip text="What to do with HDR sources. Exclude skips them; Preserve keeps the HDR signal; Tonemap to SDR converts to SDR for maximum playback compatibility (the tonemap runs on CPU)." /></label>
            <select id="lib-hdr" class="input" bind:value={form.hdrHandling}>
              <option value={null}>Profile default</option>
              {#each options.hdrHandlings as hdr}<option value={hdr}>{hdrLabel(hdr)}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-preset">Encoder preset <InfoTip text="The encoder speed/quality trade-off (x264/x265 only). Slower presets produce smaller files for the same quality but take longer to encode. “Encoder default” lets the encoder choose." /></label>
            <select id="lib-preset" class="input" bind:value={form.encoderPreset}>
              <option value={null}>Encoder default</option>
              {#each options.encoderPresets as preset}<option value={preset}>{preset}</option>{/each}
            </select>
          </div>
        </div>

        <div class="mt-4">
          <div class="mb-1 flex items-center justify-between">
            <label class="label mb-0" for="lib-crf">Quality (CRF) <InfoTip text="Constant Rate Factor: lower = higher quality and larger files, higher = smaller files. 18–24 is a good transparent range. Leave unticked to use the preset's quality." /></label>
            <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
              <input type="checkbox" class="checkbox" checked={form.qualityCrf != null} onchange={(e) => toggleCustomQuality(e.currentTarget.checked)} />
              Customise
            </label>
          </div>
          {#if form.qualityCrf != null}
            <div class="flex items-center gap-3">
              <span class="text-xs text-slate-400">Smaller</span>
              <input id="lib-crf" class="flex-1 accent-cyan-600" type="range" min="14" max="40" step="1" bind:value={form.qualityCrf} />
              <span class="text-xs text-slate-400">Sharper</span>
              <span class="badge w-10 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.qualityCrf}</span>
            </div>
          {:else}
            <p class="text-xs text-slate-400">Using the preset's quality.</p>
          {/if}
        </div>

        <div class="mt-4 grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-video-audio-codec">Audio track <InfoTip text="When re-encoding video, optionally transcode its audio too. “Copy” leaves the audio untouched; AAC is the most compatible re-encode target. Scott's Settings uses AAC here." /></label>
            <select id="lib-video-audio-codec" class="input" bind:value={form.videoAudioCodec}>
              <option value={null}>Copy (leave audio untouched)</option>
              {#each ['aac', 'opus', 'mp3'] as codec}<option value={codec}>Re-encode to {codec}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-video-audio-bitrate">Audio bitrate (kbps) <InfoTip text="Target bitrate for the audio re-encode (32–512 kbps). Only applies when the audio track is re-encoded above. 96 kbps stereo AAC is transparent for most listening." /></label>
            <input
              id="lib-video-audio-bitrate"
              class="input"
              type="number"
              min="32"
              max="512"
              placeholder="Default (160)"
              disabled={!form.videoAudioCodec}
              bind:value={form.videoAudioBitrateKbps}
            />
          </div>
        </div>

        <div class="mt-4">
          <div class="mb-1 flex items-center justify-between">
            <span class="label mb-0">Quality-gate thresholds (VMAF) <InfoTip text="Per-library override for the perceptual-quality gate. Only used when that gate is enabled in Settings. Higher = stricter (near-lossless): an output scoring below these is rejected and the original kept." /></span>
            <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
              <input type="checkbox" class="checkbox" checked={form.minVmafHarmonicMean != null || form.minVmafMin != null} onchange={(e) => toggleVmafOverride(e.currentTarget.checked)} />
              Override
            </label>
          </div>
          {#if form.minVmafHarmonicMean != null || form.minVmafMin != null}
            <div class="grid gap-3 sm:grid-cols-2">
              <div class="flex items-center gap-3">
                <span class="w-20 text-xs text-slate-500 dark:text-slate-400">Average</span>
                <input class="flex-1 accent-cyan-600" type="range" min="0" max="100" step="0.5" bind:value={form.minVmafHarmonicMean} />
                <span class="badge w-12 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.minVmafHarmonicMean}</span>
              </div>
              <div class="flex items-center gap-3">
                <span class="w-20 text-xs text-slate-500 dark:text-slate-400">Worst frame</span>
                <input class="flex-1 accent-cyan-600" type="range" min="0" max="100" step="0.5" bind:value={form.minVmafMin} />
                <span class="badge w-12 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.minVmafMin}</span>
              </div>
            </div>
          {:else}
            <p class="text-xs text-slate-400">Using the global thresholds from Settings.</p>
          {/if}
        </div>

        <!-- Capture oversized files that already match the target codec (e.g. huge HEVC remuxes
             under an HEVC target). Off by default; the size-saving gate still protects the original. -->
        <div class="mt-4">
          <label class="flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" checked={sameCodecGb !== ''} onchange={(e) => toggleSameCodec(e.currentTarget.checked)} />
            <span>
              Re-encode large files already in the target codec
              <InfoTip text="Normally a file already in the target codec (e.g. HEVC under an HEVC preset) is skipped. Enable this to re-encode the big ones anyway — useful for shrinking oversized same-codec remuxes. Verification still rejects any output that doesn't actually get smaller, so the original is never lost." />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                Targets oversized same-codec files; smaller ones are still left untouched.
              </span>
            </span>
          </label>
          {#if sameCodecGb !== ''}
            <div class="mt-2 flex items-center gap-2 pl-6 text-sm">
              <span class="text-slate-500 dark:text-slate-400">Re-encode when larger than</span>
              <input class="input w-24" type="number" min="1" step="1" bind:value={sameCodecGb} />
              <span class="text-slate-500 dark:text-slate-400">GB</span>
            </div>
          {/if}
        </div>

        <!-- Skip sources already so efficiently encoded that re-encoding won't shrink them. On by
             default; the size-saving gate still protects the original either way. -->
        <div class="mt-4">
          <label class="flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.skipEfficientSources} />
            <span>
              Skip already-efficient sources
              <InfoTip text="A file already encoded at a very low bitrate for its resolution can't be made meaningfully smaller, so it's skipped before transcoding instead of wasting an encode that the size-saving gate would reject. Turn this off to send every eligible file to the encoder anyway." />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                Avoids wasted encodes on files that can't shrink. Turn off to re-encode everything.
              </span>
            </span>
          </label>
        </div>

        <!-- Dolby Vision is left untouched by default: a re-encode drops the DV layer and a Profile 5
             source comes out green/pink. Opt in only if losing the DV presentation is acceptable. -->
        <div class="mt-4">
          <label class="flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.optimiseDolbyVision} />
            <span>
              Optimise Dolby Vision sources
              <InfoTip text="Dolby Vision needs its dynamic-metadata RPU to render correctly, and that can't survive a re-encode — the file degrades to HDR10/SDR, and a Profile 5 source comes out green/pink. Off by default, so DV files are left untouched whatever the HDR setting. Turn on only if losing the Dolby Vision presentation is acceptable for this library." />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                Off by default — DV sources are skipped to avoid colour shifts. Turn on to re-encode them anyway.
              </span>
            </span>
          </label>
        </div>
      </section>
      {/if}

      {#if showAudioOptions}
      <!-- AUDIO — scoped to Music/Other libraries (audio-only files). -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Audio</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">How audio-only files (music) are re-encoded.</p>

        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-audio-codec">Target codec <InfoTip text="The codec lossless audio (e.g. FLAC) is re-encoded to. Opus is the most efficient; AAC and MP3 trade some efficiency for broader player compatibility." /></label>
            <select id="lib-audio-codec" class="input" bind:value={form.audioTargetCodec}>
              <option value={null}>Default (Opus)</option>
              {#each ['opus', 'aac', 'mp3'] as codec}<option value={codec}>{codec}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-audio-bitrate">Bitrate (kbps) <InfoTip text="Target bitrate for the audio re-encode (32–512 kbps). 128 is transparent for most stereo; 96 is a good space-saver." /></label>
            <input
              id="lib-audio-bitrate"
              class="input"
              type="number"
              min="32"
              max="512"
              placeholder="Default (128)"
              bind:value={form.audioBitrateKbps}
            />
          </div>
        </div>

        <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.reencodeLossyAudio} />
          <span>
            Re-encode lossy audio too
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              By default only lossless audio (e.g. FLAC) is re-encoded. Enable to also re-encode lossy sources (e.g. a 320 kbps MP3) — but only when their bitrate is high enough above the target to genuinely save space.
            </span>
          </span>
        </label>
      </section>
      {/if}

      {#if showImageOptions}
      <!-- IMAGES — scoped to Photo and mixed "Other" libraries (still images). -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Images</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">How still images are re-encoded. Lossless sources (PNG/BMP/TIFF/GIF) are converted to a modern format.</p>

        <div class="grid gap-4 sm:grid-cols-2">
          {#if !showImagePreset}
          <div>
            <label class="label" for="lib-image-format">Target format <InfoTip text="JPEG plays everywhere (incl. Plex); WebP is smaller and works on Jellyfin/modern clients; AVIF is smallest but needs newer clients." /></label>
            <select id="lib-image-format" class="input" bind:value={form.targetImageFormat}>
              <option value={null}>Default (JPEG)</option>
              {#each options.imageFormats as format}<option value={format}>{format.toUpperCase()}</option>{/each}
            </select>
          </div>
          {/if}
          <div>
            <div class="mb-1 flex items-center justify-between">
              <label class="label mb-0" for="lib-image-quality">Quality <InfoTip text="Encoder quality 1–100: higher = better quality and larger files. 80 is visually transparent. Leave unticked to use the default (80)." /></label>
              <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
                <input type="checkbox" class="checkbox" checked={form.imageQuality != null} onchange={(e) => toggleCustomImageQuality(e.currentTarget.checked)} />
                Customise
              </label>
            </div>
            {#if form.imageQuality != null}
              <div class="flex items-center gap-3">
                <span class="text-xs text-slate-400">Smaller</span>
                <input id="lib-image-quality" class="flex-1 accent-cyan-600" type="range" min="1" max="100" step="1" bind:value={form.imageQuality} />
                <span class="text-xs text-slate-400">Sharper</span>
                <span class="badge w-10 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.imageQuality}</span>
              </div>
            {:else}
              <p class="text-xs text-slate-400">Using the default (80).</p>
            {/if}
          </div>
        </div>

        <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.reencodeLossyImages} />
          <span>
            Re-encode lossy images too
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              By default only lossless images (PNG/BMP/TIFF/GIF) are re-encoded. Enable to also re-encode already-compressed sources (e.g. a JPEG) — this trades a little quality for a smaller file.
            </span>
          </span>
        </label>

        <!-- Downscale: optional dimension reduction. Aspect ratio is always kept and images are
             never enlarged; an intentional downscale is allowed past verification. -->
        <div class="mt-5 border-t border-slate-200 pt-4 dark:border-slate-800">
          <div class="grid gap-4 sm:grid-cols-2">
            <div>
              <label class="label" for="lib-image-downscale">Downscale <InfoTip text="Optionally shrink large images on re-encode. Aspect ratio is always kept and images are never enlarged; an intentional downscale passes verification." /></label>
              <select id="lib-image-downscale" class="input" value={downscaleChoice} onchange={(e) => setDownscaleChoice(e.currentTarget.value as DownscaleChoice)}>
                <option value="none">None (keep original size)</option>
                <option value="4k">Fit within 4K (3840 px long edge)</option>
                <option value="1080p">Fit within 1080p (1920 px long edge)</option>
                <option value="longedge">Custom max long edge…</option>
                <option value="percent">Percentage of original…</option>
              </select>
            </div>
            {#if downscaleChoice === 'longedge'}
              <div>
                <label class="label" for="lib-image-longedge">Max long edge (px)</label>
                <input id="lib-image-longedge" class="input" type="number" min="16" max="100000" step="1" bind:value={form.imageDownscaleValue} />
                <p class="mt-1 text-xs text-slate-400">The longer side is capped to this; the shorter side scales to match.</p>
              </div>
            {:else if downscaleChoice === 'percent'}
              <div>
                <label class="label" for="lib-image-percent">Scale to (%)</label>
                <input id="lib-image-percent" class="input" type="number" min="1" max="99" step="1" bind:value={form.imageDownscaleValue} />
                <p class="mt-1 text-xs text-slate-400">Both dimensions scale to this percentage of the original.</p>
              </div>
            {/if}
          </div>
        </div>
      </section>
      {/if}

      {#if showVideoOptions || showAudioOptions}
      <!-- AUDIO CHANNELS — applies wherever audio is re-encoded (video or audio jobs); not for a
           Photo library, which has no audio. -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Audio channels</h3>
        <p class="mt-0.5 mb-3 text-xs text-slate-400">Applies wherever audio is re-encoded; copied tracks keep their layout.</p>
        <label class="flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.downmixToStereo} />
          <span>
            Downmix surround to stereo (2.0)
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              Reduces multichannel audio (e.g. 5.1) to stereo, saving space where surround isn't needed.
            </span>
          </span>
        </label>
      </section>
      {/if}

      <!-- ELIGIBILITY & QUEUE -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Eligibility &amp; queue</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">Which files this library picks up, and how its jobs are prioritised.</p>
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <div class="mb-1 flex items-center justify-between">
              <label class="label mb-0" for="lib-priority">Queue priority <InfoTip text="Higher-priority libraries run their jobs sooner when the queue is busy. Leave at Normal unless one library should jump ahead." /></label>
              <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{priorityLabel(form.priority)}</span>
            </div>
            <input id="lib-priority" class="w-full accent-cyan-600" type="range" min="-2" max="2" step="1" bind:value={form.priority} />
          </div>
          {#if showVideoOptions}
          <div>
            <label class="label" for="lib-maxheight">Skip files above <InfoTip text="Files taller than this resolution are left untouched — handy to leave 4K masters alone while optimising everything below them." /></label>
            <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
              {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
          </div>
          {/if}
          <div>
            <label class="label" for="lib-minsize">Minimum file size (MB) <InfoTip text="Files smaller than this are skipped — they rarely save enough to be worth a re-encode. Blank uses the preset's default." /></label>
            <input id="lib-minsize" class="input" type="number" min="0" placeholder="Profile default" bind:value={minSizeMb} />
          </div>
        </div>
        <div class="mt-4">
          <label class="label" for="lib-exclude">Exclude paths (one per line) <InfoTip text="Any file whose path contains one of these substrings is skipped — e.g. Extras, Featurettes, Samples. Case-insensitive." /></label>
          <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
        </div>
      </section>

      <!-- COMPLETED OUTPUT -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Completed output</h3>
        <p class="mt-0.5 mb-3 text-xs text-slate-400">What happens to a finished file. Your originals are never touched either way.</p>
        <Toggle
          bind:checked={form.moveOnComplete}
          label="Move output to a target folder instead of replacing"
          hint="Off: outputs stay in the work directory as “ready to replace”. On: the finished file is moved to the folder below — useful for testing without re-copying source files."
        />
        {#if form.moveOnComplete}
          <div class="mt-3 max-w-xl">
            <label class="label" for="lib-target">Target folder</label>
            <div class="flex gap-2">
              <input id="lib-target" class="input" readonly placeholder="Choose a folder…" value={form.targetFolder ?? ''} />
              <button type="button" class="btn flex-shrink-0" onclick={() => (targetPickerOpen = true)}>Browse</button>
            </div>
          </div>
          <label class="mt-3 flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.moveOverwrite} />
            <span>
              Overwrite an existing converted file
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                On: if a converted file is already in the target folder, replace it. Off (default): the
                job fails with a clear reason instead of overwriting, leaving the new output in the work
                directory. Your originals are never affected either way.
              </span>
            </span>
          </label>
        {/if}
      </section>
    </div>
  {/if}
  </div>
  <div class="mt-5 flex items-center gap-2">
    <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path || !isDirty}>
      <Icon name="check" class="h-4 w-4" />
      Save
    </button>
    <button class="btn" onclick={cancelEdit}>
      <Icon name="x" class="h-4 w-4" />
      Cancel
    </button>
    {#if isDirty}
      <span class="ml-1 text-xs text-amber-600 dark:text-amber-400">Unsaved changes</span>
    {/if}
  </div>
{/snippet}

{#if editingId === 0}
  <div class="card mb-6 p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Add library</h2>
    {@render configForm()}
  </div>
{/if}

{#if libraries.length > 0}
  <div class="grid gap-4">
    {#each libraries as library (library.id)}
      <div class="card p-4">
        <div class="flex flex-wrap items-center justify-between gap-4">
          <div class="min-w-0">
            <div class="flex flex-wrap items-center gap-2">
              <span class="font-semibold text-slate-800 dark:text-slate-100">{library.name}</span>
              <span class="badge bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300">{library.mediaType}</span>
              <!-- The rule profile is a video preset; only show it for video libraries (it is
                   meaningless for Music/Photo, which use their own audio/image rules). -->
              {#if isVideoType(library.mediaType)}
                <span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300">{profileLabel(library.ruleProfile)}</span>
              {/if}
              {#if library.priority !== 0}
                <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">priority {library.priority}</span>
              {/if}
              {#if !library.enabled}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">disabled</span>
              {/if}
              {#if library.autoEnqueueEnabled}
                <span class="badge bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-300" title="Eligible files are queued automatically while inside this window">
                  auto-optimise {library.autoEnqueueWindowStart === library.autoEnqueueWindowEnd ? 'any time' : `${library.autoEnqueueWindowStart}–${library.autoEnqueueWindowEnd}`}
                </span>
              {/if}
              {#if library.autoReplace}
                <span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300" title="Verified outputs replace the original automatically">auto-replace</span>
              {/if}
              {#if access[library.id]}
                {@const a = access[library.id]}
                {#if a.ok}
                  <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400" title={a.message}>access ok</span>
                {:else if !a.exists}
                  <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300" title={a.message}>path missing</span>
                {:else if !a.readable}
                  <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300" title={a.message}>can't read</span>
                {:else}
                  <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title={a.message}>not writable — replace will fail</span>
                {/if}
              {/if}
            </div>
            <div class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400">{library.path}</div>
            <div class="mt-1 text-xs text-slate-400">
              {library.fileCount.toLocaleString()} files discovered
              {#if summaries[library.id]}
                · <span class="text-emerald-600 dark:text-emerald-400">{summaries[library.id].eligible.toLocaleString()} eligible</span>
                · {summaries[library.id].skipped.toLocaleString()} skipped
              {/if}
              {#if library.autoEnqueueEnabled && library.lastAutoEnqueueAt}
                · last auto-run {new Date(library.lastAutoEnqueueAt).toLocaleString()}
              {/if}
            </div>
            {#if access[library.id] && !access[library.id].ok}
              <div class="mt-2 flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-400">
                <Icon name="warning" class="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
                <span>{access[library.id].message}</span>
              </div>
            {/if}
          </div>
          <div class="flex flex-wrap gap-2">
            <button class="btn btn-primary" onclick={() => scan(library)} disabled={busyId === library.id || !library.enabled}>
              <Icon name={busyId === library.id ? 'rotate' : 'search'} class="h-4 w-4 {busyId === library.id ? 'animate-spin' : ''}" />
              {busyId === library.id ? 'Working' : 'Scan'}
            </button>
            <button class="btn" onclick={() => enqueue(library)} disabled={busyId === library.id || !library.enabled} title="Queue this library's eligible files">
              <Icon name="plus" class="h-4 w-4" />
              Enqueue
            </button>
            <button class="btn" onclick={() => (editingId === library.id ? cancelEdit() : startEdit(library))} disabled={busyId === library.id}>
              <Icon name={editingId === library.id ? 'x' : 'sliders'} class="h-4 w-4" />
              {editingId === library.id ? 'Close' : 'Configure'}
            </button>
            <button class="btn btn-danger" onclick={() => remove(library)} disabled={busyId === library.id}>
              <Icon name="trash" class="h-4 w-4" />
              Delete
            </button>
          </div>
        </div>

        {#if editingId === library.id}
          <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-700">
            <!-- Rules | Candidates: tune the rules and see what they select without leaving the library. -->
            <div class="mb-5 flex gap-1 border-b border-slate-200 dark:border-slate-700">
              <button
                class="-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === 'rules'
                  ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
                  : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}"
                onclick={() => (activeTab = 'rules')}
              >
                Rules
                {#if isDirty}<span class="ml-1 text-amber-500" title="Unsaved changes">●</span>{/if}
              </button>
              <button
                class="-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === 'candidates'
                  ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
                  : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}"
                onclick={() => (activeTab = 'candidates')}
              >
                Candidates{#if !editorCandidatesLoading} ({editorEligibleCount}){/if}
              </button>
              <button
                class="-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === 'excluded'
                  ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
                  : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}"
                onclick={() => { activeTab = 'excluded'; if (editingId) void loadEditorExclusions(editingId) }}
              >
                Excluded{#if !editorExclusionsLoading} ({editorExclusions.length}){/if}
              </button>
            </div>

            {#if activeTab === 'rules'}
              {@render configForm()}
            {:else if activeTab === 'candidates'}
              {#if editorCandidatesError}
                <Banner kind="error" class="mb-3">{editorCandidatesError}</Banner>
              {/if}
              <p class="mb-3 text-xs text-slate-500 dark:text-slate-400">
                What this library's <strong>saved</strong> rules select right now — it updates after you Save on the Rules tab. Enqueue these from the Enqueue button above; nothing here changes a file.
              </p>
              {#if editorCandidatesLoading}
                <div class="card p-8 text-center text-slate-400">Loading…</div>
              {:else}
                <CandidateTable candidates={editorCandidates} scoped />
              {/if}
            {:else}
              {#if editorExclusionsError}
                <Banner kind="error" class="mb-3">{editorExclusionsError}</Banner>
              {/if}
              <p class="mb-3 text-xs text-slate-500 dark:text-slate-400">
                Files you've told Optimisarr to never optimise (from here or the Queue's <strong>Exclude</strong> action). They're skipped by scans, candidates, and auto-optimise until you remove them here. Your original files are untouched.
              </p>
              {#if editorExclusionsLoading}
                <div class="card p-8 text-center text-slate-400">Loading…</div>
              {:else if editorExclusions.length === 0}
                <div class="rounded-lg border border-dashed border-slate-200 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
                  No excluded files. Use <strong>Exclude</strong> on a stuck job in the Queue to add one.
                </div>
              {:else}
                <div class="divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
                  {#each editorExclusions as ex (ex.id)}
                    {@const auto = ex.source === 'RepeatedFailures'}
                    <div class="flex items-center justify-between gap-3 px-3 py-2">
                      <div class="flex min-w-0 items-center gap-3">
                        <!-- An icon badge distinguishes an automatically-excluded file (kept failing)
                             from one the operator excluded by hand. -->
                        <span
                          class="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-full {auto
                            ? 'bg-amber-100 text-amber-600 dark:bg-amber-950 dark:text-amber-400'
                            : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}"
                          title={auto ? 'Excluded automatically after repeated failures' : 'Excluded manually'}
                        >
                          <Icon name={auto ? 'warning' : 'ban'} class="h-4 w-4" />
                        </span>
                        <div class="min-w-0">
                          <div class="truncate font-mono text-xs text-slate-700 dark:text-slate-200">{ex.relativePath ?? ex.path}</div>
                          <div class="mt-0.5 text-xs text-slate-400">
                            <span class={auto ? 'text-amber-600 dark:text-amber-400' : ''}>{auto ? 'Auto-excluded — repeated failures' : 'Manually excluded'}</span>
                            {#if ex.reason} · {ex.reason}{/if}
                            · {new Date(ex.createdAt).toLocaleDateString()}
                          </div>
                        </div>
                      </div>
                      <button class="btn btn-ghost flex-shrink-0 px-2 py-1 text-xs" onclick={() => unexclude(ex.id)} title="Remove from the exclusion list — the file becomes eligible again">
                        Remove
                      </button>
                    </div>
                  {/each}
                </div>
              {/if}
            {/if}
          </div>
        {/if}
      </div>
    {/each}
  </div>
{:else if editingId !== 0}
  <EmptyState icon="folder" title="No libraries yet" hint="Add one to start discovering media.">
    <button class="btn btn-primary" onclick={startAdd}>
      <Icon name="plus" class="h-4 w-4" />
      Add library
    </button>
  </EmptyState>
{/if}
