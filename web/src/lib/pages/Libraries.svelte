<script lang="ts">
  import { api, type Library, type LibraryOptions, type SaveLibrary } from '../api'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Toggle from '../components/Toggle.svelte'

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

  function toggleCustomQuality(on: boolean) {
    form.qualityCrf = on ? (form.qualityCrf ?? DEFAULT_CRF) : null
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
  // Edited in MB for friendliness; converted to bytes on save.
  let minSizeMb = $state<number | ''>('')

  const BYTES_PER_MB = 1024 * 1024

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
      moveOnComplete: library.moveOnComplete,
      targetFolder: library.targetFolder,
      minVmafHarmonicMean: library.minVmafHarmonicMean,
      minVmafMin: library.minVmafMin,
      autoEnqueueEnabled: library.autoEnqueueEnabled,
      autoEnqueueWindowStart: library.autoEnqueueWindowStart,
      autoEnqueueWindowEnd: library.autoEnqueueWindowEnd,
    }
    minSizeMb = library.minFileSizeBytes != null ? Math.round(library.minFileSizeBytes / BYTES_PER_MB) : ''
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
      One library per content type. Expand a card to define how it's optimised — target format, HDR handling, size and resolution limits, and priority.
    </p>
  </div>
  {#if editingId !== 0}
    <button class="btn btn-primary" onclick={startAdd}>Add library</button>
  {/if}
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{:else if message}
  <div class="card mb-4 border-emerald-300 p-3 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{message}</div>
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

  <h3 class="mb-3 mt-6 text-xs font-semibold uppercase tracking-wide text-slate-400">
    Target output <span class="font-normal normal-case">— "Profile default" uses the preset above</span>
  </h3>
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
    <div class="sm:col-span-2">
      <div class="mb-1 flex items-center justify-between">
        <label class="label mb-0" for="lib-crf">Quality (CRF)</label>
        <label class="flex cursor-pointer items-center gap-2 text-xs font-normal text-slate-500 dark:text-slate-400">
          <input type="checkbox" class="checkbox" checked={form.qualityCrf != null} onchange={(e) => toggleCustomQuality(e.currentTarget.checked)} />
          Set custom quality
        </label>
      </div>
      {#if form.qualityCrf != null}
        <div class="flex items-center gap-3">
          <input id="lib-crf" class="flex-1 accent-cyan-600" type="range" min="14" max="40" step="1" bind:value={form.qualityCrf} />
          <span class="badge w-10 justify-center bg-cyan-100 text-cyan-700 dark:bg-cyan-900/40 dark:text-cyan-400">{form.qualityCrf}</span>
        </div>
        <p class="mt-1 text-xs text-slate-400">Lower = higher quality and larger files. 18–24 is a good range.</p>
      {:else}
        <p class="text-xs text-slate-400">Using the encoder's default quality.</p>
      {/if}
    </div>
    <div>
      <label class="label" for="lib-vmaf-harmonic">VMAF gate override — harmonic mean</label>
      <input id="lib-vmaf-harmonic" class="input" type="number" min="0" max="100" step="0.5" placeholder="Global default" bind:value={form.minVmafHarmonicMean} />
    </div>
    <div>
      <label class="label" for="lib-vmaf-min">VMAF gate override — worst frame</label>
      <input id="lib-vmaf-min" class="input" type="number" min="0" max="100" step="0.5" placeholder="Global default" bind:value={form.minVmafMin} />
    </div>
    <p class="text-xs text-slate-400 sm:col-span-2">
      Only used when the perceptual-quality gate is enabled in Settings. Leave blank to use the global thresholds —
      raise these for an archive library that should demand near-lossless quality.
    </p>
  </div>

  <h3 class="mb-3 mt-6 text-xs font-semibold uppercase tracking-wide text-slate-400">Eligibility &amp; queue</h3>
  <div class="grid gap-4 sm:grid-cols-2">
    <div>
      <label class="label" for="lib-priority">Queue priority</label>
      <select id="lib-priority" class="input" bind:value={form.priority}>
        {#each priorityLevels as level}<option value={level.value}>{level.label}</option>{/each}
      </select>
    </div>
    <div>
      <label class="label" for="lib-maxheight">Max resolution (skip above)</label>
      <select id="lib-maxheight" class="input" bind:value={form.maxHeight}>
        {#each resolutionLimits as limit}<option value={limit.value}>{limit.label}</option>{/each}
      </select>
    </div>
    <div>
      <label class="label" for="lib-minsize">Minimum file size (MB)</label>
      <input id="lib-minsize" class="input" type="number" min="0" placeholder="profile default" bind:value={minSizeMb} />
    </div>
    <div class="sm:col-span-2">
      <label class="label" for="lib-exclude">Exclude paths (one per line)</label>
      <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
    </div>
  </div>

  <h3 class="mb-3 mt-6 text-xs font-semibold uppercase tracking-wide text-slate-400">Completed output</h3>
  <Toggle
    bind:checked={form.moveOnComplete}
    label="Move output to a target folder when complete"
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

  <h3 class="mb-3 mt-6 text-xs font-semibold uppercase tracking-wide text-slate-400">Automatic optimisation</h3>
  <Toggle
    bind:checked={form.autoEnqueueEnabled}
    label="Scan and enqueue automatically on a schedule"
    hint="When on, this library is scanned and its eligible files are queued once per day, within the window below. Jobs still only run inside the global processing window (Settings), and the global concurrency limit always applies — auto-enqueue never starts jobs itself, it only fills the queue."
  />
  {#if form.autoEnqueueEnabled}
    <div class="mt-3 flex flex-wrap items-end gap-4">
      <div>
        <label class="label" for="lib-auto-start">Window start</label>
        <input id="lib-auto-start" class="input w-32" type="time" bind:value={form.autoEnqueueWindowStart} />
      </div>
      <div>
        <label class="label" for="lib-auto-end">Window end</label>
        <input id="lib-auto-end" class="input w-32" type="time" bind:value={form.autoEnqueueWindowEnd} />
      </div>
      <p class="max-w-md text-xs text-slate-500 dark:text-slate-400">
        Equal start and end means all day (enqueue once each day). A window like 01:00–06:00 runs a single nightly scan-and-enqueue when it opens.
      </p>
    </div>
  {/if}

  <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-700">
    <Toggle bind:checked={form.enabled} label="Enabled" hint="Included in scans and eligible for the queue." />
  </div>
  <div class="mt-5 flex gap-2">
    <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path}>Save</button>
    <button class="btn" onclick={cancelEdit}>Cancel</button>
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
              {busyId === library.id ? 'Working' : 'Scan'}
            </button>
            <button class="btn" onclick={() => enqueue(library)} disabled={busyId === library.id || !library.enabled} title="Queue this library's eligible files">
              Enqueue
            </button>
            <button class="btn" onclick={() => (editingId === library.id ? cancelEdit() : startEdit(library))} disabled={busyId === library.id}>
              {editingId === library.id ? 'Close' : 'Configure'}
            </button>
            <button class="btn btn-danger" onclick={() => remove(library)} disabled={busyId === library.id}>Delete</button>
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
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No libraries yet. Add one to start discovering media.
  </div>
{/if}
