<script lang="ts">
  import { api, newLibraryDefaults, type Candidate, type Exclusion, type Library, type LibraryAccess, type LibraryOptions, type SaveLibrary } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Toggle from '../components/Toggle.svelte'
  import Icon from '../components/Icon.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Banner from '../components/Banner.svelte'
  import EmptyState from '../components/EmptyState.svelte'
  import CandidateTable from '../components/CandidateTable.svelte'

  let {
    embeddedEditorId = null,
    onEmbeddedClose,
    onEmbeddedSaved,
  }: {
    embeddedEditorId?: number | null
    onEmbeddedClose?: () => void
    onEmbeddedSaved?: (library: Library) => void
  } = $props()

  const embedded = $derived(embeddedEditorId !== null)

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

  const DEFAULT_CRF = 23
  const DEFAULT_VMAF_HARMONIC = 93
  const DEFAULT_VMAF_MIN = 80
  const DEFAULT_VMAF_CATASTROPHIC = 50
  const DEFAULT_IMAGE_QUALITY = 80

  // Plain-language summary of each preset, shown under the picker so a first-time
  // user can choose without knowing codecs.
  const presetSummaries: Record<string, string> = $derived({
    ConservativeHevc: i18n.m.libraries.preset_conservative_hevc,
    CompatibilityH264: i18n.m.libraries.preset_compatibility_h264,
    ExperimentalAv1: i18n.m.libraries.preset_experimental_av1,
    RemuxCleanup: i18n.m.libraries.preset_remux_cleanup,
    ScottsSettings: i18n.m.libraries.preset_scotts_settings,
  })

  // The re-encode profiles form a single compatibility→efficiency axis, shown as a slider so the
  // common case is one simple choice; "Scott's Settings" rides along as a named all-in-one preset
  // at the end. Remux/Cleanup is "don't re-encode at all", so it sits as a separate toggle above
  // the slider rather than on the quality axis. The exact codec/container/CRF/audio knobs stay in
  // Advanced options.
  const encodeProfiles = ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings']
  // "Custom" is one stop past the real presets — selecting it hands the codec/container/quality to
  // the operator (set in Advanced) instead of following a preset, so it stays on the same control.
  const encodeStopLabels = $derived([
    i18n.m.libraries.stop_compatibility,
    i18n.m.libraries.stop_balanced,
    i18n.m.libraries.stop_efficiency,
    i18n.m.libraries.stop_scotts,
    i18n.m.libraries.stop_custom,
  ])
  const customStopIndex = encodeProfiles.length

  // Friendly display names for raw rule-profile ids so a badge reads "Scott's Settings", not the
  // PascalCase enum name "ScottsSettings".
  const profileLabels: Record<string, string> = $derived({
    ConservativeHevc: i18n.m.libraries.profile_conservative_hevc,
    CompatibilityH264: i18n.m.libraries.profile_compatibility_h264,
    ExperimentalAv1: i18n.m.libraries.profile_experimental_av1,
    RemuxCleanup: i18n.m.libraries.profile_remux_cleanup,
    ScottsSettings: i18n.m.libraries.profile_scotts_settings,
  })
  function profileLabel(profile: string): string {
    return profileLabels[profile] ?? profile
  }

  // Friendly display names for raw codec ids so a badge reads "HEVC (H.265)", not "hevc".
  const codecLabels: Record<string, string> = { h264: 'H.264', hevc: 'HEVC (H.265)', av1: 'AV1' }
  function prettyCodec(codec: string | null): string {
    if (!codec) return i18n.m.libraries.codec_none
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

  type VmafMode = 'off' | 'space-saver' | 'balanced' | 'high' | 'lossless' | 'archival' | 'custom'
  const vmafPresets = [
    { mode: 'space-saver', harmonic: 80, fifth: 60, catastrophic: 30 },
    { mode: 'balanced', harmonic: 85, fifth: 70, catastrophic: 40 },
    { mode: 'high', harmonic: 90, fifth: 75, catastrophic: 45 },
    { mode: 'lossless', harmonic: 93, fifth: 80, catastrophic: 50 },
    { mode: 'archival', harmonic: 96, fifth: 90, catastrophic: 70 },
  ] as const
  // Keep an explicitly selected Custom mode visible even while its initial values happen to match
  // a named preset. Loaded libraries still derive Custom from any non-preset values.
  let vmafCustomSelected = $state(false)

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

  function setVmafMode(mode: VmafMode) {
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

  function priorityLabel(value: number): string {
    return priorityLevels.find((level) => level.value === value)?.label ?? i18n.m.libraries.priority_normal
  }


  function hdrLabel(hdr: string): string {
    if (hdr === 'TonemapToSdr') return i18n.m.libraries.hdr_tonemap
    if (hdr === 'Exclude') return i18n.m.libraries.hdr_exclude
    if (hdr === 'Preserve') return i18n.m.libraries.hdr_preserve
    return hdr
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

  const MAX_AUDIO_LANGUAGE_LIST_LENGTH = 256
  type AudioLanguageInput = {
    codes: string[]
    normalised: string | null
    syntaxValid: boolean
    tooLong: boolean
  }

  // Mirrors the backend's storage validation so the form can explain a bad value before Save.
  // The API remains authoritative; this is immediate, accessible operator feedback.
  function parseAudioLanguageInput(value: string | null): AudioLanguageInput {
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
      tooLong: normalised.length > MAX_AUDIO_LANGUAGE_LIST_LENGTH,
    }
  }

  const audioLanguageInput = $derived(parseAudioLanguageInput(form.keepAudioLanguages))
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
    if (form.targetVideoCodec) parts.push(t(i18n.m.libraries.override_codec, { codec: form.targetVideoCodec.toUpperCase() }))
    if (form.targetContainer) parts.push(t(i18n.m.libraries.override_container, { container: form.targetContainer }))
    return parts.join(i18n.m.libraries.override_join)
  }

  function resetToPreset() {
    form.targetVideoCodec = null
    form.targetContainer = null
  }

  // A photo library gets its own compatibility→efficiency slider — the image counterpart of the
  // video preset — mapping a single choice onto JPEG / WebP. It is shown only for Photo
  // libraries (a mixed "Other" library keeps the video slider and sets the format in Advanced).
  const imageFormats = ['jpeg', 'webp'] as const
  const showImagePreset = $derived(isImageType(form.mediaType) && !isVideoType(form.mediaType))
  const imageStop = $derived(Math.max(0, imageFormats.indexOf((form.targetImageFormat ?? 'jpeg') as (typeof imageFormats)[number])))
  function setImageStop(value: string) {
    form.targetImageFormat = imageFormats[Number(value)] ?? 'jpeg'
  }
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
    // Proactively flag any path Optimisarr can't reach/read/write before the user hits a failure.
    void checkAllAccess()

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
    if (embeddedEditorId !== null) return embeddedEditorId
    if (router.path === '/libraries/new') return 0
    const match = router.path.match(/^\/libraries\/(\d+)\/configure$/)
    return match ? Number(match[1]) : null
  }

  // Per-library filesystem access (exists / readable / writable), keyed by library id.
  let access = $state<Record<number, LibraryAccess>>({})

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

  function startAdd() {
    form = blankForm()
    if (options.mediaTypes.length) form.mediaType = options.mediaTypes[0]
    if (options.ruleProfiles.length) form.ruleProfile = options.ruleProfiles[0]
    customSelected = false
    vmafCustomSelected = false
    minSizeMb = ''
    sameCodecGb = ''
    showAdvanced = false
    activeTab = 'rules'
    editorCandidates = []
    editingId = 0
    markPristine()
  }

  function startEdit(library: Library) {
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
      keepAudioLanguages: library.keepAudioLanguages,
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
    vmafCustomSelected = false
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
    markPristine()
    if (embedded) onEmbeddedClose?.()
    else router.go('/libraries')
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
      keepAudioLanguages: audioLanguageInput.normalised,
      targetImageFormat: emptyToNull(form.targetImageFormat),
      imageQuality: toNullableNumber(form.imageQuality),
      imageDownscaleValue: Number(form.imageDownscaleValue) || 0,
      targetFolder: form.moveOnComplete ? emptyToNull(form.targetFolder) : null,
      minVmafHarmonicMean: toNullableNumber(form.minVmafHarmonicMean),
      minVmafMin: toNullableNumber(form.minVmafMin),
      minVmafCatastrophicMin: toNullableNumber(form.minVmafCatastrophicMin),
      vmafFrameSubsample: toNullableNumber(form.vmafFrameSubsample),
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
    if (audioLanguageError) return
    try {
      if (editingId === 0) {
        // Replace /new with the canonical editor URL so Back returns to the library list. The
        // route change remounts this keyed page, so carry the success message across that boundary.
        const created = await api.createLibrary(payload())
        const success = t(i18n.m.libraries.added, { name: form.name })
        markPristine()
        if (embedded) {
          onEmbeddedSaved?.(created)
          return
        }
        sessionStorage.setItem(FLASH_KEY, success)
        router.replace(`/libraries/${created.id}/configure`)
        return
      } else if (editingId) {
        const updated = await api.updateLibrary(editingId, payload())
        message = t(i18n.m.libraries.updated, { name: form.name })
        if (embedded) {
          markPristine()
          onEmbeddedSaved?.(updated)
          return
        }
      }
      // The saved values are now the baseline, so the form is no longer dirty.
      markPristine()
      await load()
      // Re-resolve what the now-saved rules select, so the Candidates tab reflects this Save.
      if (editingId) await loadEditorCandidates(editingId)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.libraries.error_save
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
</script>

{#if editingId === null}
  <header class="mb-6 flex items-start justify-between gap-4">
    <div>
      <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.libraries}</h1>
      <p class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.libraries.subtitle}</p>
    </div>
    <button class="btn btn-primary" onclick={() => router.go('/libraries/new')}>
      <Icon name="plus" class="h-4 w-4" />
      {i18n.m.libraries.add_library}
    </button>
  </header>
{:else}
  <header class="mb-5 border-b border-slate-200 pb-4 dark:border-slate-800">
    <button class="mb-3 inline-flex min-h-11 items-center gap-2 text-sm font-medium text-slate-500 transition-colors hover:text-cyan-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-cyan-500 dark:text-slate-400 dark:hover:text-cyan-300" onclick={cancelEdit}>
      <Icon name="arrow-left" class="h-4 w-4" />
      {i18n.m.nav.libraries}
    </button>
    <div class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <div class="text-xs font-semibold uppercase tracking-wide text-cyan-700 dark:text-cyan-300">{i18n.m.libraries.configure}</div>
        <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{editingId === 0 ? i18n.m.libraries.add_library_heading : form.name}</h1>
        {#if form.path}<p class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400">{form.path}</p>{/if}
      </div>
      <div class="flex flex-wrap items-center justify-end gap-2">
        {#if editingId !== 0}
          <span class="badge bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300">{form.mediaType}</span>
          {#if showVideoOptions}<span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300">{profileLabel(form.ruleProfile)}</span>{/if}
        {/if}
        <div class="hidden items-center gap-2 sm:flex">
          <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path || !isDirty || !!audioLanguageError || !!vmafError}>
            <Icon name="check" class="h-4 w-4" />
            {i18n.m.libraries.save}
          </button>
          <button class="btn" onclick={cancelEdit}>
            <Icon name="x" class="h-4 w-4" />
            {i18n.m.libraries.cancel}
          </button>
        </div>
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
      <label class="label" for="lib-name">{i18n.m.libraries.name}</label>
      <input id="lib-name" class="input" placeholder={i18n.m.libraries.name_ph} bind:value={form.name} />
    </div>
    <div>
      <label class="label" for="lib-path">{i18n.m.libraries.path}</label>
      <div class="flex gap-2">
        <input id="lib-path" class="input" readonly placeholder={i18n.m.libraries.path_ph} value={form.path} />
        <button type="button" class="btn flex-shrink-0" onclick={() => (pickerOpen = true)}>{i18n.m.libraries.browse}</button>
      </div>
    </div>
    <div>
      <label class="label" for="lib-type">{i18n.m.libraries.media_type}</label>
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
      <span class="label mb-0">{i18n.m.libraries.preset_label} <InfoTip text={i18n.m.libraries.preset_tip} /></span>
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
          {i18n.m.libraries.remux_label}
          <span class="mt-0.5 block text-xs font-normal text-slate-400">
            {i18n.m.libraries.remux_hint}
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
          aria-label={i18n.m.libraries.slider_aria}
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
          ? i18n.m.libraries.custom_config_summary
          : (presetSummaries[form.ruleProfile] ?? i18n.m.libraries.custom_preset_fallback)}
      </p>

      <!-- Explicit, concrete selection so the slider isn't a mystery. -->
      <div class="mt-2 flex flex-wrap items-center gap-1.5 text-xs">
        <span class="text-slate-400">{i18n.m.libraries.selects}</span>
        <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{effectiveVideoSpec.codec}</span>
        {#if !isRemuxProfile}
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{effectiveVideoSpec.container}</span>
          {#if effectiveVideoSpec.crf != null}
            <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{t(i18n.m.libraries.crf_badge, { crf: effectiveVideoSpec.crf })}</span>
          {/if}
        {:else}
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{t(i18n.m.libraries.container_badge, { container: effectiveVideoSpec.container })}</span>
        {/if}
      </div>

      {#if isCustom}
        <!-- Neutral, not amber: a custom config is a deliberate choice, not a warning. -->
        <div class="mt-2 rounded-md border border-slate-200 bg-slate-50 p-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-300">
          {#if presetOverridden}
            <span>{t(i18n.m.libraries.custom_overridden, { summary: overrideSummary(), verb: form.targetVideoCodec && form.targetContainer ? i18n.m.libraries.custom_overridden_are : i18n.m.libraries.custom_overridden_is })}</span>
          {:else}
            <span>{i18n.m.libraries.custom_plain}</span>
          {/if}
          <button type="button" class="ml-1 font-medium underline" onclick={selectPresetMode}>{i18n.m.libraries.use_preset_instead}</button>
        </div>
      {/if}
    {:else if showImagePreset}
      <!-- Image compatibility→efficiency slider (Photo libraries): JPEG → WebP. -->
      <div class="mt-1">
        <input
          class="w-full accent-cyan-600"
          type="range"
          min="0"
          max={imageFormats.length - 1}
          step="1"
          value={imageStop}
          oninput={(e) => setImageStop(e.currentTarget.value)}
          aria-label={i18n.m.libraries.image_slider_aria}
        />
        <div class="mt-1 flex justify-between text-xs text-slate-500 dark:text-slate-400">
          {#each imageFormats as stop, i}
            <span class={imageStop === i ? 'font-semibold uppercase text-slate-700 dark:text-slate-200' : 'uppercase'}>{stop}</span>
          {/each}
        </div>
        <div class="mt-1 flex justify-between text-[10px] uppercase tracking-wide text-slate-400">
          <span>{i18n.m.libraries.most_compatible}</span>
          <span>{i18n.m.libraries.most_efficient}</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{imagePresetSummaries[form.targetImageFormat ?? 'jpeg']}</p>
        <div class="mt-2 flex flex-wrap items-center gap-1.5 text-xs">
          <span class="text-slate-400">{i18n.m.libraries.selects}</span>
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{(form.targetImageFormat ?? 'jpeg').toUpperCase()} (.{(form.targetImageFormat ?? 'jpeg') === 'jpeg' ? 'jpg' : form.targetImageFormat})</span>
          <span class="badge bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300">{t(i18n.m.libraries.quality_badge, { quality: form.imageQuality ?? 80 })}</span>
        </div>
      </div>
    {:else}
      <p class="mt-1 text-xs text-slate-500 dark:text-slate-400">
        {i18n.m.libraries.music_note}
      </p>
    {/if}
  </div>

  {#if showVideoOptions && !isRemuxProfile}
    <section class="mt-6 border-t border-slate-200 pt-5 dark:border-slate-700">
      <div class="max-w-3xl">
        <div>
          <label class="label" for="lib-vmaf-policy">
            {i18n.m.settings.vmaf_label}
            <InfoTip text={i18n.m.settings.vmaf_hint} />
          </label>
          <select
            id="lib-vmaf-policy"
            class="input"
            value={vmafMode}
            onchange={(event) => setVmafMode(event.currentTarget.value as VmafMode)}
          >
            <option value="off">{i18n.m.settings.vmaf_preset_off}</option>
            <option value="space-saver">{i18n.m.settings.vmaf_preset_space_saver}</option>
            <option value="balanced">{i18n.m.settings.vmaf_preset_balanced}</option>
            <option value="high">{i18n.m.settings.vmaf_preset_high}</option>
            <option value="lossless">{i18n.m.settings.vmaf_preset_lossless}</option>
            <option value="archival">{i18n.m.settings.vmaf_preset_archival}</option>
            <option value="custom">{i18n.m.libraries.stop_custom}</option>
          </select>
          <p class="mt-1 text-xs text-slate-500 dark:text-slate-400">{i18n.m.libraries.vmaf_thresholds_tip}</p>
        </div>

        <div class="mt-3 flex flex-wrap items-center gap-1.5 text-xs">
          {#if vmafMode === 'off'}
            <span class="text-slate-500 dark:text-slate-400">{i18n.m.settings.vmaf_off_desc}</span>
          {:else}
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{i18n.m.settings.vmaf_harmonic} {form.minVmafHarmonicMean}</span>
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{i18n.m.settings.vmaf_min} {form.minVmafMin}</span>
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{i18n.m.settings.vmaf_catastrophic} {form.minVmafCatastrophicMin}</span>
          {/if}
        </div>
      </div>

      {#if vmafMode === 'custom'}
        <div class="mt-4 grid gap-3 border-t border-slate-200 pt-4 sm:grid-cols-3 dark:border-slate-700">
          <div>
            <label class="label" for="lib-vmaf-harmonic">{i18n.m.settings.vmaf_harmonic}</label>
            <input id="lib-vmaf-harmonic" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafHarmonicMean} />
          </div>
          <div>
            <label class="label" for="lib-vmaf-fifth">{i18n.m.settings.vmaf_min}</label>
            <input id="lib-vmaf-fifth" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafMin} />
          </div>
          <div>
            <label class="label" for="lib-vmaf-catastrophic">{i18n.m.settings.vmaf_catastrophic}</label>
            <input id="lib-vmaf-catastrophic" class="input" type="number" min="0" max="100" step="0.5" aria-invalid={!!vmafError} aria-describedby="lib-vmaf-error" bind:value={form.minVmafCatastrophicMin} />
          </div>
        </div>
      {/if}

      {#if vmafMode !== 'off'}
        <div class="mt-4 grid gap-3 border-t border-slate-200 pt-4 sm:grid-cols-2 dark:border-slate-700">
          <div>
            <label class="label" for="lib-vmaf-sampling">{i18n.m.settings.vmaf_clip_label}</label>
            <select id="lib-vmaf-sampling" class="input" value={form.clipVmafEnabled ? 'samples' : 'full'} onchange={(event) => (form.clipVmafEnabled = event.currentTarget.value === 'samples')}>
              <option value="samples">{i18n.m.queue.vmaf_sampling_three}</option>
              <option value="full">{i18n.m.queue.vmaf_sampling_full}</option>
            </select>
          </div>
          <div>
            <label class="label" for="lib-vmaf-frames">{i18n.m.settings.vmaf_subsample_label}</label>
            <select id="lib-vmaf-frames" class="input" bind:value={form.vmafFrameSubsample}>
              <option value={1}>{i18n.m.settings.vmaf_every_frame}</option>
              {#each [2, 3, 4, 5, 10] as interval}
                <option value={interval}>{t(i18n.m.settings.vmaf_every_nth_frame, { interval })}</option>
              {/each}
            </select>
          </div>
        </div>
      {/if}
      {#if vmafError}
        <p id="lib-vmaf-error" class="mt-3 text-xs text-red-600 dark:text-red-400" role="alert">{vmafError}</p>
      {/if}
    </section>
  {/if}

  <!-- Simple, always-visible switches. The technical encoding knobs live under
       "Advanced options" so the common case stays uncluttered. -->
  <div class="mt-5 space-y-4 border-t border-slate-200 pt-5 dark:border-slate-700">
    <Toggle bind:checked={form.enabled} label={i18n.m.libraries.enabled_label} hint={i18n.m.libraries.enabled_hint} />

    <Toggle
      bind:checked={form.autoEnqueueEnabled}
      label={i18n.m.libraries.auto_optimise_label}
      hint={i18n.m.libraries.auto_optimise_hint}
    />
    {#if form.autoEnqueueEnabled}
      <div class="flex flex-wrap items-end gap-4 pl-1">
        <div>
          <label class="label" for="lib-auto-start">{i18n.m.libraries.window_start}</label>
          <input id="lib-auto-start" class="input w-32" type="time" bind:value={form.autoEnqueueWindowStart} />
        </div>
        <div>
          <label class="label" for="lib-auto-end">{i18n.m.libraries.window_end}</label>
          <input id="lib-auto-end" class="input w-32" type="time" bind:value={form.autoEnqueueWindowEnd} />
        </div>
        <p class="max-w-xs text-xs text-slate-500 dark:text-slate-400">
          {i18n.m.libraries.window_hint}
        </p>
      </div>
    {/if}

    <Toggle
      bind:checked={form.autoReplace}
      label={i18n.m.libraries.auto_replace_label}
      hint={i18n.m.libraries.auto_replace_hint}
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
      <span>{i18n.m.libraries.advanced}</span>
      <span class="text-xs font-normal text-slate-400">{i18n.m.libraries.advanced_hint}</span>
      <Icon name="chevron" class="ml-auto h-4 w-4 text-slate-400 transition-transform {showAdvanced ? 'rotate-180' : ''}" />
    </button>

  {#if showAdvanced}
    <!-- divide-y draws a separator between whichever sections are shown for this media type. -->
    <div class="border-t border-slate-200 bg-slate-50/60 px-4 divide-y divide-slate-200 dark:border-slate-700 dark:bg-slate-900/30 dark:divide-slate-800">

      {#if showVideoOptions}
      <!-- VIDEO — scoped to Film/TV/Other libraries. -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.video}</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">{i18n.m.libraries.video_desc}</p>

        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-codec">{i18n.m.libraries.target_codec} <InfoTip text={i18n.m.libraries.target_codec_tip} /></label>
            <select id="lib-codec" class="input" bind:value={form.targetVideoCodec}>
              <option value={null}>{i18n.m.libraries.profile_default}</option>
              {#each options.videoCodecs as codec}<option value={codec}>{codec.toUpperCase()}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-container">{i18n.m.libraries.container} <InfoTip text={i18n.m.libraries.container_tip} /></label>
            <select id="lib-container" class="input" bind:value={form.targetContainer}>
              <option value={null}>{i18n.m.libraries.profile_default}</option>
              {#each options.containers as container}<option value={container}>.{container}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-hdr">{i18n.m.libraries.hdr_dv} <InfoTip text={i18n.m.libraries.hdr_dv_tip} /></label>
            <select id="lib-hdr" class="input" bind:value={form.hdrHandling}>
              <option value={null}>{i18n.m.libraries.profile_default}</option>
              {#each options.hdrHandlings as hdr}<option value={hdr}>{hdrLabel(hdr)}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-preset">{i18n.m.libraries.encoder_preset} <InfoTip text={i18n.m.libraries.encoder_preset_tip} /></label>
            <select id="lib-preset" class="input" bind:value={form.encoderPreset}>
              <option value={null}>{i18n.m.libraries.encoder_default}</option>
              {#each options.encoderPresets as preset}<option value={preset}>{preset}</option>{/each}
            </select>
          </div>
        </div>

        <div class="mt-4">
          <div class="mb-1 flex items-center justify-between">
            <label class="label mb-0" for="lib-crf">{i18n.m.libraries.quality_crf} <InfoTip text={i18n.m.libraries.quality_crf_tip} /></label>
            <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
              <input type="checkbox" class="checkbox" checked={form.qualityCrf != null} onchange={(e) => toggleCustomQuality(e.currentTarget.checked)} />
              {i18n.m.libraries.customise}
            </label>
          </div>
          {#if form.qualityCrf != null}
            <div class="flex items-center gap-3">
              <span class="text-xs text-slate-400">{i18n.m.libraries.sharper}</span>
              <input id="lib-crf" class="flex-1 accent-cyan-600" type="range" min="14" max="40" step="1" bind:value={form.qualityCrf} />
              <span class="text-xs text-slate-400">{i18n.m.libraries.smaller}</span>
              <span class="badge w-10 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.qualityCrf}</span>
            </div>
          {:else}
            <p class="text-xs text-slate-400">{i18n.m.libraries.using_preset_quality}</p>
          {/if}
        </div>

        <div class="mt-4 grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-video-audio-codec">{i18n.m.libraries.audio_track} <InfoTip text={i18n.m.libraries.audio_track_tip} /></label>
            <select id="lib-video-audio-codec" class="input" bind:value={form.videoAudioCodec}>
              <option value={null}>{i18n.m.libraries.audio_profile_default}</option>
              <option value="copy">{i18n.m.libraries.audio_copy}</option>
              {#each ['aac', 'opus', 'mp3'] as codec}<option value={codec}>{t(i18n.m.libraries.reencode_to, { codec })}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-video-audio-bitrate">{i18n.m.libraries.audio_bitrate} <InfoTip text={i18n.m.libraries.audio_bitrate_tip} /></label>
            <input
              id="lib-video-audio-bitrate"
              class="input"
              type="number"
              min="32"
              max="512"
              placeholder={i18n.m.libraries.audio_bitrate_ph}
              disabled={!form.videoAudioCodec || form.videoAudioCodec === 'copy'}
              bind:value={form.videoAudioBitrateKbps}
            />
          </div>
        </div>

        <!-- Keep-languages track removal applies to copied and re-encoded audio alike; tracks
             with no language tag are never removed, and a file where nothing matches is left
             untouched, so the output always keeps at least one audio track. -->
        <div class="mt-4">
          <label class="label" for="lib-keep-audio-languages">{i18n.m.libraries.keep_audio_langs} <InfoTip text={i18n.m.libraries.keep_audio_langs_tip} /></label>
          <input
            id="lib-keep-audio-languages"
            class="input"
            type="text"
            autocomplete="off"
            autocapitalize="none"
            spellcheck={false}
            maxlength={MAX_AUDIO_LANGUAGE_LIST_LENGTH}
            placeholder={i18n.m.libraries.keep_audio_langs_ph}
            aria-invalid={audioLanguageError ? 'true' : 'false'}
            aria-describedby="lib-keep-audio-languages-hint{audioLanguageError ? ' lib-keep-audio-languages-error' : ''}"
            aria-errormessage={audioLanguageError ? 'lib-keep-audio-languages-error' : undefined}
            bind:value={form.keepAudioLanguages}
            onblur={normaliseAudioLanguageInput}
          />
          <p id="lib-keep-audio-languages-hint" class="mt-1 text-xs text-slate-400">{i18n.m.libraries.keep_audio_langs_hint}</p>
          {#if audioLanguageError}
            <p id="lib-keep-audio-languages-error" class="mt-1 text-xs text-red-600 dark:text-red-400" role="alert">{audioLanguageError}</p>
          {:else if audioLanguageInput.codes.length > 0}
            <div class="mt-2 flex flex-wrap items-center gap-1.5">
              <span class="text-xs text-slate-400">{i18n.m.libraries.keep_audio_langs_selected}</span>
              {#each audioLanguageInput.codes as code (code)}
                <span class="badge bg-cyan-100 font-mono uppercase text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-300">{code}</span>
              {/each}
            </div>
          {/if}
        </div>

        <!-- Capture oversized files that already match the target codec (e.g. huge HEVC remuxes
             under an HEVC target). Off by default; the size-saving gate still protects the original. -->
        <div class="mt-4">
          <label class="flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" checked={sameCodecGb !== ''} onchange={(e) => toggleSameCodec(e.currentTarget.checked)} />
            <span>
              {i18n.m.libraries.same_codec_label}
              <InfoTip text={i18n.m.libraries.same_codec_tip} />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                {i18n.m.libraries.same_codec_hint}
              </span>
            </span>
          </label>
          {#if sameCodecGb !== ''}
            <div class="mt-2 flex items-center gap-2 pl-6 text-sm">
              <span class="text-slate-500 dark:text-slate-400">{i18n.m.libraries.same_codec_when}</span>
              <input class="input w-24" type="number" min="1" step="1" bind:value={sameCodecGb} />
              <span class="text-slate-500 dark:text-slate-400">{i18n.m.libraries.gb}</span>
            </div>
          {/if}
        </div>

        <!-- Skip sources already so efficiently encoded that re-encoding won't shrink them. On by
             default; the size-saving gate still protects the original either way. -->
        <div class="mt-4">
          <label class="flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.skipEfficientSources} />
            <span>
              {i18n.m.libraries.skip_efficient_label}
              <InfoTip text={i18n.m.libraries.skip_efficient_tip} />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                {i18n.m.libraries.skip_efficient_hint}
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
              {i18n.m.libraries.dolby_vision_label}
              <InfoTip text={i18n.m.libraries.dolby_vision_tip} />
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                {i18n.m.libraries.dolby_vision_hint}
              </span>
            </span>
          </label>
        </div>
      </section>
      {/if}

      {#if showAudioOptions}
      <!-- AUDIO — scoped to Music/Other libraries (audio-only files). -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.audio}</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">{i18n.m.libraries.audio_desc}</p>

        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-audio-codec">{i18n.m.libraries.target_codec} <InfoTip text={i18n.m.libraries.audio_codec_tip} /></label>
            <select id="lib-audio-codec" class="input" bind:value={form.audioTargetCodec}>
              <option value={null}>{i18n.m.libraries.audio_default_aac}</option>
              {#each ['opus', 'aac', 'mp3'] as codec}<option value={codec}>{codec}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-audio-bitrate">{i18n.m.libraries.bitrate} <InfoTip text={i18n.m.libraries.bitrate_tip} /></label>
            <input
              id="lib-audio-bitrate"
              class="input"
              type="number"
              min="32"
              max="512"
              placeholder={i18n.m.libraries.bitrate_ph}
              bind:value={form.audioBitrateKbps}
            />
          </div>
        </div>

        <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.reencodeLossyAudio} />
          <span>
            {i18n.m.libraries.reencode_lossy_audio}
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              {i18n.m.libraries.reencode_lossy_audio_hint}
            </span>
          </span>
        </label>
      </section>
      {/if}

      {#if showImageOptions}
      <!-- IMAGES — scoped to Photo and mixed "Other" libraries (still images). -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.images}</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">{i18n.m.libraries.images_desc}</p>

        <div class="grid gap-4 sm:grid-cols-2">
          {#if !showImagePreset}
          <div>
            <label class="label" for="lib-image-format">{i18n.m.libraries.target_format} <InfoTip text={i18n.m.libraries.target_format_tip} /></label>
            <select id="lib-image-format" class="input" bind:value={form.targetImageFormat}>
              <option value={null}>{i18n.m.libraries.image_default_jpeg}</option>
              {#each options.imageFormats as format}<option value={format}>{format.toUpperCase()}</option>{/each}
            </select>
          </div>
          {/if}
          <div>
            <div class="mb-1 flex items-center justify-between">
              <label class="label mb-0" for="lib-image-quality">{i18n.m.libraries.quality} <InfoTip text={i18n.m.libraries.image_quality_tip} /></label>
              <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
                <input type="checkbox" class="checkbox" checked={form.imageQuality != null} onchange={(e) => toggleCustomImageQuality(e.currentTarget.checked)} />
                {i18n.m.libraries.customise}
              </label>
            </div>
            {#if form.imageQuality != null}
              <div class="flex items-center gap-3">
                <span class="text-xs text-slate-400">{i18n.m.libraries.smaller}</span>
                <input id="lib-image-quality" class="flex-1 accent-cyan-600" type="range" min="1" max="100" step="1" bind:value={form.imageQuality} />
                <span class="text-xs text-slate-400">{i18n.m.libraries.sharper}</span>
                <span class="badge w-10 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.imageQuality}</span>
              </div>
            {:else}
              <p class="text-xs text-slate-400">{i18n.m.libraries.using_default_80}</p>
            {/if}
          </div>
        </div>

        <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.reencodeLossyImages} />
          <span>
            {i18n.m.libraries.reencode_lossy_images}
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              {i18n.m.libraries.reencode_lossy_images_hint}
            </span>
          </span>
        </label>

        <!-- Downscale: optional dimension reduction. Aspect ratio is always kept and images are
             never enlarged; an intentional downscale is allowed past verification. -->
        <div class="mt-5 border-t border-slate-200 pt-4 dark:border-slate-800">
          <div class="grid gap-4 sm:grid-cols-2">
            <div>
              <label class="label" for="lib-image-downscale">{i18n.m.libraries.downscale} <InfoTip text={i18n.m.libraries.downscale_tip} /></label>
              <select id="lib-image-downscale" class="input" value={downscaleChoice} onchange={(e) => setDownscaleChoice(e.currentTarget.value as DownscaleChoice)}>
                <option value="none">{i18n.m.libraries.downscale_none}</option>
                <option value="4k">{i18n.m.libraries.downscale_4k}</option>
                <option value="1080p">{i18n.m.libraries.downscale_1080p}</option>
                <option value="longedge">{i18n.m.libraries.downscale_longedge}</option>
                <option value="percent">{i18n.m.libraries.downscale_percent}</option>
              </select>
            </div>
            {#if downscaleChoice === 'longedge'}
              <div>
                <label class="label" for="lib-image-longedge">{i18n.m.libraries.max_long_edge}</label>
                <input id="lib-image-longedge" class="input" type="number" min="16" max="100000" step="1" bind:value={form.imageDownscaleValue} />
                <p class="mt-1 text-xs text-slate-400">{i18n.m.libraries.max_long_edge_hint}</p>
              </div>
            {:else if downscaleChoice === 'percent'}
              <div>
                <label class="label" for="lib-image-percent">{i18n.m.libraries.scale_to}</label>
                <input id="lib-image-percent" class="input" type="number" min="1" max="99" step="1" bind:value={form.imageDownscaleValue} />
                <p class="mt-1 text-xs text-slate-400">{i18n.m.libraries.scale_to_hint}</p>
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
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.audio_channels}</h3>
        <p class="mt-0.5 mb-3 text-xs text-slate-400">{i18n.m.libraries.audio_channels_desc}</p>
        <label class="flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.downmixToStereo} />
          <span>
            {i18n.m.libraries.downmix_label}
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              {i18n.m.libraries.downmix_hint}
            </span>
          </span>
        </label>
      </section>
      {/if}

      <!-- ELIGIBILITY & QUEUE -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.eligibility_queue}</h3>
        <p class="mt-0.5 mb-4 text-xs text-slate-400">{i18n.m.libraries.eligibility_queue_desc}</p>
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <div class="mb-1 flex items-center justify-between">
              <label class="label mb-0" for="lib-priority">{i18n.m.libraries.queue_priority} <InfoTip text={i18n.m.libraries.queue_priority_tip} /></label>
              <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{priorityLabel(form.priority)}</span>
            </div>
            <input id="lib-priority" class="w-full accent-cyan-600" type="range" min="-2" max="2" step="1" bind:value={form.priority} />
          </div>
          {#if showVideoOptions}
          <div>
            <label class="label" for="lib-maxheight">{i18n.m.libraries.skip_above} <InfoTip text={i18n.m.libraries.skip_above_tip} /></label>
            <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
              {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
          </div>
          {/if}
          <div>
            <label class="label" for="lib-minsize">{i18n.m.libraries.min_file_size} <InfoTip text={i18n.m.libraries.min_file_size_tip} /></label>
            <input id="lib-minsize" class="input" type="number" min="0" placeholder={i18n.m.libraries.profile_default_ph} bind:value={minSizeMb} />
          </div>
        </div>
        <div class="mt-4">
          <label class="label" for="lib-exclude">{i18n.m.libraries.exclude_paths} <InfoTip text={i18n.m.libraries.exclude_paths_tip} /></label>
          <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
        </div>
      </section>

      <!-- COMPLETED OUTPUT -->
      <section class="py-6">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.libraries.completed_output}</h3>
        <p class="mt-0.5 mb-3 text-xs text-slate-400">{i18n.m.libraries.completed_output_desc}</p>
        <Toggle
          bind:checked={form.moveOnComplete}
          label={i18n.m.libraries.move_label}
          hint={i18n.m.libraries.move_hint}
        />
        {#if form.moveOnComplete}
          <div class="mt-3 max-w-xl">
            <label class="label" for="lib-target">{i18n.m.libraries.target_folder}</label>
            <div class="flex gap-2">
              <input id="lib-target" class="input" readonly placeholder={i18n.m.libraries.path_ph} value={form.targetFolder ?? ''} />
              <button type="button" class="btn flex-shrink-0" onclick={() => (targetPickerOpen = true)}>{i18n.m.libraries.browse}</button>
            </div>
          </div>
          <label class="mt-3 flex cursor-pointer items-start gap-2 text-sm">
            <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.moveOverwrite} />
            <span>
              {i18n.m.libraries.overwrite_label}
              <span class="mt-0.5 block text-xs font-normal text-slate-400">
                {i18n.m.libraries.overwrite_hint}
              </span>
            </span>
          </label>
        {/if}
      </section>
    </div>
  {/if}
  </div>
  <div class="-mx-5 mt-6 flex flex-wrap items-center gap-2 border-t border-slate-200 px-5 py-4 dark:border-slate-700">
    <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path || !isDirty || !!audioLanguageError || !!vmafError}>
      <Icon name="check" class="h-4 w-4" />
      {i18n.m.libraries.save}
    </button>
    <button class="btn" onclick={cancelEdit}>
      <Icon name="x" class="h-4 w-4" />
      {i18n.m.libraries.cancel}
    </button>
    {#if isDirty}
      <span class="ml-1 text-xs text-amber-600 dark:text-amber-400">{i18n.m.libraries.unsaved}</span>
    {/if}
  </div>
{/snippet}

{#if editingId !== null}
  {#if editingId !== 0 && !embedded}
    <nav class="mb-4 flex gap-1 overflow-x-auto border-b border-slate-200 dark:border-slate-700" aria-label={i18n.m.libraries.configure}>
      <button class="-mb-px min-h-11 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium {activeTab === 'rules' ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300' : 'border-transparent text-slate-500 dark:text-slate-400'}" onclick={() => (activeTab = 'rules')}>{i18n.m.libraries.tab_rules}{#if isDirty}<span class="ml-1 text-amber-500">●</span>{/if}</button>
      <button class="-mb-px min-h-11 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium {activeTab === 'candidates' ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300' : 'border-transparent text-slate-500 dark:text-slate-400'}" onclick={() => (activeTab = 'candidates')}>{i18n.m.libraries.tab_candidates}{#if !editorCandidatesLoading} ({editorEligibleCount}){/if}</button>
      <button class="-mb-px min-h-11 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium {activeTab === 'excluded' ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300' : 'border-transparent text-slate-500 dark:text-slate-400'}" onclick={() => { activeTab = 'excluded'; if (editingId) void loadEditorExclusions(editingId) }}>{i18n.m.libraries.tab_excluded}{#if !editorExclusionsLoading} ({editorExclusions.length}){/if}</button>
    </nav>
  {/if}

  {#if activeTab === 'rules' || editingId === 0}
    <div class="card p-5 sm:p-6">
      {@render configForm()}
    </div>
  {:else if activeTab === 'candidates'}
    {#if editorCandidatesError}<Banner kind="error" class="mb-3">{editorCandidatesError}</Banner>{/if}
    <p class="mb-3 text-xs text-slate-500 dark:text-slate-400">{i18n.m.libraries.candidates_desc_1}<strong>{i18n.m.libraries.candidates_desc_saved}</strong>{i18n.m.libraries.candidates_desc_2}</p>
    {#if editorCandidatesLoading}
      <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
    {:else}
      <CandidateTable candidates={editorCandidates} scoped />
    {/if}
  {:else}
    {#if editorExclusionsError}<Banner kind="error" class="mb-3">{editorExclusionsError}</Banner>{/if}
    <p class="mb-3 text-xs text-slate-500 dark:text-slate-400">{i18n.m.libraries.excluded_desc_1}<strong>{i18n.m.libraries.excluded_desc_exclude}</strong>{i18n.m.libraries.excluded_desc_2}</p>
    {#if editorExclusionsLoading}
      <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
    {:else if editorExclusions.length === 0}
      <div class="rounded-lg border border-dashed border-slate-200 p-8 text-center text-sm text-slate-400 dark:border-slate-700">{i18n.m.libraries.excluded_empty_1}<strong>{i18n.m.libraries.excluded_empty_exclude}</strong>{i18n.m.libraries.excluded_empty_2}</div>
    {:else}
      <div class="divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
        {#each editorExclusions as ex (ex.id)}
          {@const auto = ex.source === 'RepeatedFailures'}
          <div class="flex items-center justify-between gap-3 px-3 py-2">
            <div class="min-w-0">
              <div class="truncate font-mono text-xs text-slate-700 dark:text-slate-200">{ex.relativePath ?? ex.path}</div>
              <div class="mt-0.5 text-xs text-slate-400"><span class={auto ? 'text-amber-600 dark:text-amber-400' : ''}>{auto ? i18n.m.libraries.excluded_auto : i18n.m.libraries.excluded_manual}</span>{#if ex.reason} · {ex.reason}{/if} · {new Date(ex.createdAt).toLocaleDateString()}</div>
            </div>
            <button class="btn btn-ghost min-h-11 flex-shrink-0 px-3 text-xs" onclick={() => unexclude(ex.id)}>{i18n.m.libraries.remove}</button>
          </div>
        {/each}
      </div>
    {/if}
  {/if}
{:else if libraries.length > 0}
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
                <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">{t(i18n.m.libraries.badge_priority, { value: library.priority })}</span>
              {/if}
              {#if !library.enabled}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">{i18n.m.libraries.badge_disabled}</span>
              {/if}
              {#if library.autoEnqueueEnabled}
                <span class="badge bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-300" title={i18n.m.libraries.auto_optimise_title}>
                  {t(i18n.m.libraries.badge_auto_optimise, { window: library.autoEnqueueWindowStart === library.autoEnqueueWindowEnd ? i18n.m.libraries.any_time : `${library.autoEnqueueWindowStart}–${library.autoEnqueueWindowEnd}` })}
                </span>
              {/if}
              {#if library.autoReplace}
                <span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300" title={i18n.m.libraries.auto_replace_title}>{i18n.m.libraries.badge_auto_replace}</span>
              {/if}
              {#if access[library.id]}
                {@const a = access[library.id]}
                {#if a.ok}
                  <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400" title={accessMessage(a)}>{i18n.m.libraries.access_ok}</span>
                {:else if !a.exists}
                  <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300" title={accessMessage(a)}>{i18n.m.libraries.access_missing}</span>
                {:else if !a.readable}
                  <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300" title={accessMessage(a)}>{i18n.m.libraries.access_unreadable}</span>
                {:else}
                  <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title={accessMessage(a)}>{i18n.m.libraries.access_unwritable}</span>
                {/if}
              {/if}
            </div>
            <div class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400">{library.path}</div>
            <div class="mt-1 text-xs text-slate-400">
              {t(i18n.m.libraries.files_discovered, { count: library.fileCount.toLocaleString() })}
              {#if summaries[library.id]}
                · <span class="text-emerald-600 dark:text-emerald-400">{t(i18n.m.libraries.eligible_count, { count: summaries[library.id].eligible.toLocaleString() })}</span>
                · {t(i18n.m.libraries.skipped_count, { count: summaries[library.id].skipped.toLocaleString() })}
              {/if}
              {#if library.autoEnqueueEnabled && library.lastAutoEnqueueAt}
                · {t(i18n.m.libraries.last_auto_run, { date: new Date(library.lastAutoEnqueueAt).toLocaleString() })}
              {/if}
            </div>
            {#if access[library.id] && !access[library.id].ok}
              <div class="mt-2 flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-400">
                <Icon name="warning" class="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
                <span>{accessMessage(access[library.id])}</span>
              </div>
            {/if}
          </div>
          <div class="flex flex-wrap gap-2">
            <button class="btn btn-primary" onclick={() => scan(library)} disabled={busyId === library.id || !library.enabled}>
              <Icon name={busyId === library.id ? 'rotate' : 'search'} class="h-4 w-4 {busyId === library.id ? 'animate-spin' : ''}" />
              {busyId === library.id ? i18n.m.libraries.working : i18n.m.libraries.scan}
            </button>
            <button class="btn" onclick={() => enqueue(library)} disabled={busyId === library.id || !library.enabled} title={i18n.m.libraries.enqueue_title}>
              <Icon name="plus" class="h-4 w-4" />
              {i18n.m.libraries.enqueue}
            </button>
            <button class="btn" onclick={() => router.go(`/libraries/${library.id}/configure`)} disabled={busyId === library.id}>
              <Icon name="sliders" class="h-4 w-4" />
              {i18n.m.libraries.configure}
            </button>
            <button class="btn btn-danger" onclick={() => remove(library)} disabled={busyId === library.id}>
              <Icon name="trash" class="h-4 w-4" />
              {i18n.m.libraries.delete}
            </button>
          </div>
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
