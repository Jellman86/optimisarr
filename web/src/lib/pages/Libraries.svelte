<script lang="ts">
  import { api, type Library, type LibraryOptions, type SaveLibrary } from '../api'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Toggle from '../components/Toggle.svelte'
  import Icon from '../components/Icon.svelte'
  import Banner from '../components/Banner.svelte'
  import EmptyState from '../components/EmptyState.svelte'

  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions>({
    mediaTypes: [],
    ruleProfiles: [],
    hdrHandlings: [],
    videoCodecs: [],
    containers: [],
    encoderPresets: [],
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

  // Plain-language summary of each preset, shown under the picker so a first-time
  // user can choose without knowing codecs.
  const presetSummaries: Record<string, string> = {
    ConservativeHevc: 'Space-saving HEVC (H.265) in MP4 — plays on virtually all phones, TVs, and Apple devices. A good default. (AAC audio recommended; audio is kept as-is unless you choose otherwise in Advanced.)',
    CompatibilityH264: 'H.264 in MP4 — plays literally everywhere, at the cost of larger files. (AAC audio recommended.)',
    ExperimentalAv1: 'Smallest files using AV1 in MKV, where hardware allows. Slower to encode. (Opus audio recommended.)',
    RemuxCleanup: 'Container cleanup only — no re-encode. Fast and lossless.',
  }

  function toggleCustomQuality(on: boolean) {
    form.qualityCrf = on ? (form.qualityCrf ?? DEFAULT_CRF) : null
  }

  function toggleVmafOverride(on: boolean) {
    form.minVmafHarmonicMean = on ? (form.minVmafHarmonicMean ?? DEFAULT_VMAF_HARMONIC) : null
    form.minVmafMin = on ? (form.minVmafMin ?? DEFAULT_VMAF_MIN) : null
  }

  function priorityLabel(value: number): string {
    return priorityLevels.find((level) => level.value === value)?.label ?? 'Normal'
  }

  // Whether a library uses any setting beyond the basics, so editing it can open
  // the advanced panel already expanded instead of hiding the user's own choices.
  function usesAdvanced(library: Library): boolean {
    return (
      library.priority !== 0 ||
      library.maxHeight != null ||
      library.minFileSizeBytes != null ||
      !!library.targetVideoCodec ||
      !!library.targetContainer ||
      !!library.hdrHandling ||
      !!library.encoderPreset ||
      library.qualityCrf != null ||
      !!library.audioTargetCodec ||
      library.audioBitrateKbps != null ||
      !!library.videoAudioCodec ||
      library.videoAudioBitrateKbps != null ||
      library.downmixToStereo ||
      library.reencodeLossyAudio ||
      library.minVmafHarmonicMean != null ||
      library.minVmafMin != null ||
      !!library.excludePaths ||
      library.moveOnComplete
    )
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
  let form = $state<SaveLibrary>(blankForm())
  // Advanced (encoding/eligibility) settings are collapsed by default to keep the
  // common case simple; opened automatically when editing a library that uses them.
  let showAdvanced = $state(false)
  // Edited in MB for friendliness; converted to bytes on save.
  let minSizeMb = $state<number | ''>('')

  const BYTES_PER_MB = 1024 * 1024

  // Advanced controls are scoped to the library's media type: video knobs for Film/TV,
  // audio knobs for Music, and everything for a mixed "Other" library that may hold both.
  const showVideoOptions = $derived(form.mediaType !== 'Music')
  const showAudioOptions = $derived(form.mediaType === 'Music' || form.mediaType === 'Other')

  $effect(() => {
    void load()
  })

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
      targetVideoCodec: null,
      targetContainer: null,
      hdrHandling: null,
      excludePaths: null,
      qualityCrf: null,
      encoderPreset: null,
      audioTargetCodec: null,
      audioBitrateKbps: null,
      videoAudioCodec: null,
      videoAudioBitrateKbps: null,
      downmixToStereo: false,
      reencodeLossyAudio: false,
      moveOnComplete: false,
      targetFolder: null,
      minVmafHarmonicMean: null,
      minVmafMin: null,
      autoEnqueueEnabled: false,
      autoEnqueueWindowStart: '00:00',
      autoEnqueueWindowEnd: '00:00',
    }
  }

  async function load() {
    error = null
    try {
      ;[libraries, options] = await Promise.all([api.libraries(), api.libraryOptions()])
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load libraries'
    }
  }

  function startAdd() {
    form = blankForm()
    if (options.mediaTypes.length) form.mediaType = options.mediaTypes[0]
    if (options.ruleProfiles.length) form.ruleProfile = options.ruleProfiles[0]
    minSizeMb = ''
    showAdvanced = false
    editingId = 0
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
      targetVideoCodec: library.targetVideoCodec,
      targetContainer: library.targetContainer,
      hdrHandling: library.hdrHandling,
      excludePaths: library.excludePaths,
      qualityCrf: library.qualityCrf,
      encoderPreset: library.encoderPreset,
      audioTargetCodec: library.audioTargetCodec,
      audioBitrateKbps: library.audioBitrateKbps,
      videoAudioCodec: library.videoAudioCodec,
      videoAudioBitrateKbps: library.videoAudioBitrateKbps,
      downmixToStereo: library.downmixToStereo,
      reencodeLossyAudio: library.reencodeLossyAudio,
      moveOnComplete: library.moveOnComplete,
      targetFolder: library.targetFolder,
      minVmafHarmonicMean: library.minVmafHarmonicMean,
      minVmafMin: library.minVmafMin,
      autoEnqueueEnabled: library.autoEnqueueEnabled,
      autoEnqueueWindowStart: library.autoEnqueueWindowStart,
      autoEnqueueWindowEnd: library.autoEnqueueWindowEnd,
    }
    minSizeMb = library.minFileSizeBytes != null ? Math.round(library.minFileSizeBytes / BYTES_PER_MB) : ''
    showAdvanced = usesAdvanced(library)
    editingId = library.id
  }

  function cancelEdit() {
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
      maxHeight: form.maxHeight ? Number(form.maxHeight) : null,
      priority: Number(form.priority) || 0,
      targetVideoCodec: emptyToNull(form.targetVideoCodec),
      targetContainer: emptyToNull(form.targetContainer),
      hdrHandling: emptyToNull(form.hdrHandling),
      excludePaths: emptyToNull(form.excludePaths),
      qualityCrf: form.qualityCrf == null ? null : Number(form.qualityCrf),
      encoderPreset: emptyToNull(form.encoderPreset),
      audioTargetCodec: emptyToNull(form.audioTargetCodec),
      audioBitrateKbps: toNullableNumber(form.audioBitrateKbps),
      videoAudioCodec: emptyToNull(form.videoAudioCodec),
      videoAudioBitrateKbps: toNullableNumber(form.videoAudioBitrateKbps),
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
        await api.createLibrary(payload())
        message = `Added library "${form.name}".`
      } else if (editingId) {
        await api.updateLibrary(editingId, payload())
        message = `Updated library "${form.name}".`
      }
      editingId = null
      await load()
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
    <div>
      <label class="label" for="lib-rule">Rule profile (preset)</label>
      <select id="lib-rule" class="input" bind:value={form.ruleProfile}>
        {#each options.ruleProfiles as rule}<option value={rule}>{rule}</option>{/each}
      </select>
    </div>
  </div>

  <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{presetSummaries[form.ruleProfile] ?? 'Custom preset.'}</p>

  <!-- Simple, always-visible switches. The technical encoding knobs live under
       "Advanced options" so the common case stays uncluttered. -->
  <div class="mt-5 space-y-4 border-t border-slate-200 pt-5 dark:border-slate-700">
    <Toggle bind:checked={form.enabled} label="Library enabled" hint="Included in scans and eligible for the queue." />

    <Toggle
      bind:checked={form.autoEnqueueEnabled}
      label="Optimise automatically"
      hint="Scan and queue this library once a day, inside the window below. Jobs still only run during the global processing window, and the global concurrency limit always applies — this only fills the queue."
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
          Equal times = once a day. A window like 01:00–06:00 runs one nightly pass when it opens.
        </p>
      </div>
    {/if}
  </div>

  <!-- Advanced options: codec / quality / eligibility overrides, hidden by default. -->
  <button
    type="button"
    class="mt-5 flex w-full items-center gap-2 border-t border-slate-200 pt-4 text-sm font-medium text-slate-600 dark:border-slate-700 dark:text-slate-300"
    onclick={() => (showAdvanced = !showAdvanced)}
    aria-expanded={showAdvanced}
  >
    <Icon name="sliders" class="h-4 w-4 text-slate-400" />
    <span>Advanced options</span>
    <span class="text-xs font-normal text-slate-400">codec, quality, limits</span>
    <Icon name="chevron" class="ml-auto h-4 w-4 text-slate-400 transition-transform {showAdvanced ? 'rotate-180' : ''}" />
  </button>

  {#if showAdvanced}
    <div class="mt-4 space-y-6">
      <!-- Encoding -->
      <div>
        <h3 class="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">
          Encoding <span class="font-normal normal-case">— leave on "Profile default" to follow the preset</span>
        </h3>
        {#if showVideoOptions}
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-codec">Target video codec</label>
            <select id="lib-codec" class="input" bind:value={form.targetVideoCodec}>
              <option value={null}>Profile default</option>
              {#each options.videoCodecs as codec}<option value={codec}>{codec.toUpperCase()}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-container">Target container</label>
            <select id="lib-container" class="input" bind:value={form.targetContainer}>
              <option value={null}>Profile default</option>
              {#each options.containers as container}<option value={container}>.{container}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-hdr">HDR / Dolby Vision</label>
            <select id="lib-hdr" class="input" bind:value={form.hdrHandling}>
              <option value={null}>Profile default</option>
              {#each options.hdrHandlings as hdr}<option value={hdr}>{hdrLabel(hdr)}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="lib-preset">Encoder preset</label>
            <select id="lib-preset" class="input" bind:value={form.encoderPreset}>
              <option value={null}>Encoder default</option>
              {#each options.encoderPresets as preset}<option value={preset}>{preset}</option>{/each}
            </select>
          </div>
        </div>

        <div class="mt-4">
          <div class="mb-1 flex items-center justify-between">
            <label class="label mb-0" for="lib-crf">Quality (CRF)</label>
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
            <p class="mt-1 text-xs text-slate-400">Lower CRF = higher quality and larger files. 18–24 is a good range.</p>
          {:else}
            <p class="text-xs text-slate-400">Using the preset's quality.</p>
          {/if}
        </div>
        {/if}

        {#if showAudioOptions}
        <div class="mt-4 grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-audio-codec">Audio target codec</label>
            <select id="lib-audio-codec" class="input" bind:value={form.audioTargetCodec}>
              <option value={null}>Default (Opus)</option>
              {#each ['opus', 'aac', 'mp3'] as codec}<option value={codec}>{codec}</option>{/each}
            </select>
            <p class="mt-1 text-xs text-slate-400">Lossless audio is re-encoded to this codec.</p>
          </div>
          <div>
            <label class="label" for="lib-audio-bitrate">Audio bitrate (kbps)</label>
            <input
              id="lib-audio-bitrate"
              class="input"
              type="number"
              min="32"
              max="512"
              placeholder="Default (128)"
              bind:value={form.audioBitrateKbps}
            />
            <p class="mt-1 text-xs text-slate-400">32–512 kbps. 128 is transparent for most stereo.</p>
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
        {/if}

        {#if showVideoOptions}
        <div class="mt-4 grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="lib-video-audio-codec">Video audio</label>
            <select id="lib-video-audio-codec" class="input" bind:value={form.videoAudioCodec}>
              <option value={null}>Copy (leave audio untouched)</option>
              {#each ['aac', 'opus', 'mp3'] as codec}<option value={codec}>Re-encode to {codec}</option>{/each}
            </select>
            <p class="mt-1 text-xs text-slate-400">When re-encoding video, also transcode its audio. AAC is the most compatible.</p>
          </div>
          <div>
            <label class="label" for="lib-video-audio-bitrate">Video audio bitrate (kbps)</label>
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
            <p class="mt-1 text-xs text-slate-400">32–512 kbps. Only applies when audio is re-encoded.</p>
          </div>
        </div>
        {/if}

        <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm">
          <input type="checkbox" class="checkbox mt-0.5" bind:checked={form.downmixToStereo} />
          <span>
            Downmix surround to stereo (2.0)
            <span class="mt-0.5 block text-xs font-normal text-slate-400">
              Reduces multichannel audio to stereo when re-encoding. Only applies where audio is re-encoded; copied tracks keep their layout.
            </span>
          </span>
        </label>

        {#if showVideoOptions}
        <div class="mt-4">
          <div class="mb-1 flex items-center justify-between">
            <span class="label mb-0">Quality-gate thresholds (VMAF)</span>
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
            <p class="mt-1 text-xs text-slate-400">Only used when the perceptual-quality gate is enabled in Settings. Higher = stricter (near-lossless).</p>
          {:else}
            <p class="text-xs text-slate-400">Using the global thresholds from Settings.</p>
          {/if}
        </div>
        {/if}
      </div>

      <!-- Eligibility & queue -->
      <div>
        <h3 class="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">Eligibility &amp; queue</h3>
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <div class="mb-1 flex items-center justify-between">
              <label class="label mb-0" for="lib-priority">Queue priority</label>
              <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{priorityLabel(form.priority)}</span>
            </div>
            <input id="lib-priority" class="w-full accent-cyan-600" type="range" min="-2" max="2" step="1" bind:value={form.priority} />
          </div>
          {#if showVideoOptions}
          <div>
            <label class="label" for="lib-maxheight">Skip files above</label>
            <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
              {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
            </select>
          </div>
          {/if}
          <div>
            <label class="label" for="lib-minsize">Minimum file size (MB)</label>
            <input id="lib-minsize" class="input" type="number" min="0" placeholder="Profile default" bind:value={minSizeMb} />
          </div>
        </div>
        <div class="mt-4">
          <label class="label" for="lib-exclude">Exclude paths (one per line)</label>
          <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
        </div>
      </div>

      <!-- Completed output -->
      <div>
        <h3 class="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">Completed output</h3>
        <Toggle
          bind:checked={form.moveOnComplete}
          label="Move output to a target folder instead of replacing"
          hint="Off: outputs stay in the work directory as “ready to replace”. On: the finished file is moved to the folder below — your originals are never touched. Useful for testing without re-copying source files."
        />
        {#if form.moveOnComplete}
          <div class="mt-3 max-w-xl">
            <label class="label" for="lib-target">Target folder</label>
            <div class="flex gap-2">
              <input id="lib-target" class="input" readonly placeholder="Choose a folder…" value={form.targetFolder ?? ''} />
              <button type="button" class="btn flex-shrink-0" onclick={() => (targetPickerOpen = true)}>Browse</button>
            </div>
          </div>
        {/if}
      </div>
    </div>
  {/if}
  <div class="mt-5 flex gap-2">
    <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path}>
      <Icon name="check" class="h-4 w-4" />
      Save
    </button>
    <button class="btn" onclick={cancelEdit}>
      <Icon name="x" class="h-4 w-4" />
      Cancel
    </button>
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
              <span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300">{library.ruleProfile}</span>
              {#if library.priority !== 0}
                <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">priority {library.priority}</span>
              {/if}
              {#if !library.enabled}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">disabled</span>
              {/if}
              {#if library.autoEnqueueEnabled}
                <span class="badge bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-300" title="Scanned and enqueued automatically">
                  auto {library.autoEnqueueWindowStart === library.autoEnqueueWindowEnd ? 'daily' : `${library.autoEnqueueWindowStart}–${library.autoEnqueueWindowEnd}`}
                </span>
              {/if}
            </div>
            <div class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400">{library.path}</div>
            <div class="mt-1 text-xs text-slate-400">
              {library.fileCount.toLocaleString()} files discovered
              {#if library.autoEnqueueEnabled && library.lastAutoEnqueueAt}
                · last auto-run {new Date(library.lastAutoEnqueueAt).toLocaleString()}
              {/if}
            </div>
          </div>
          <div class="flex flex-shrink-0 gap-2">
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
            {@render configForm()}
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
