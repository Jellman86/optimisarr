<script lang="ts">
  import { api, type Library, type LibraryOptions, type SaveLibrary } from '../api'
  import FolderPicker from '../components/FolderPicker.svelte'

  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions>({ mediaTypes: [], ruleProfiles: [], hdrHandlings: [] })
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)
  let busyId = $state<number | null>(null)
  let pickerOpen = $state(false)

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
    }
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
    Handling overrides <span class="font-normal normal-case">— leave blank to use the profile default</span>
  </h3>
  <div class="grid gap-4 sm:grid-cols-2">
    <div>
      <label class="label" for="lib-codec">Target video codec</label>
      <input id="lib-codec" class="input" placeholder="profile default (e.g. hevc)" bind:value={form.targetVideoCodec} />
    </div>
    <div>
      <label class="label" for="lib-container">Target container</label>
      <input id="lib-container" class="input" placeholder="profile default (e.g. mkv)" bind:value={form.targetContainer} />
    </div>
    <div>
      <label class="label" for="lib-hdr">HDR / Dolby Vision</label>
      <select id="lib-hdr" class="input" bind:value={form.hdrHandling}>
        <option value={null}>Profile default</option>
        {#each options.hdrHandlings as hdr}<option value={hdr}>{hdr}</option>{/each}
      </select>
    </div>
    <div>
      <label class="label" for="lib-priority">Queue priority</label>
      <input id="lib-priority" class="input" type="number" placeholder="0" bind:value={form.priority} />
    </div>
    <div>
      <label class="label" for="lib-minsize">Minimum file size (MB)</label>
      <input id="lib-minsize" class="input" type="number" min="0" placeholder="profile default" bind:value={minSizeMb} />
    </div>
    <div>
      <label class="label" for="lib-maxheight">Max resolution height (px)</label>
      <input id="lib-maxheight" class="input" type="number" min="1" placeholder="no limit" bind:value={form.maxHeight} />
    </div>
    <div class="sm:col-span-2">
      <label class="label" for="lib-exclude">Exclude paths (one per line)</label>
      <textarea id="lib-exclude" class="input h-20 font-mono text-xs" placeholder="Extras&#10;Featurettes&#10;Samples" bind:value={form.excludePaths}></textarea>
    </div>
  </div>

  <label class="mt-4 flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
    <input type="checkbox" bind:checked={form.enabled} /> Enabled (included in scans)
  </label>
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
            </div>
            <div class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400">{library.path}</div>
            <div class="mt-1 text-xs text-slate-400">{library.fileCount.toLocaleString()} files discovered</div>
          </div>
          <div class="flex flex-shrink-0 gap-2">
            <button class="btn btn-primary" onclick={() => scan(library)} disabled={busyId === library.id || !library.enabled}>
              {busyId === library.id ? 'Working' : 'Scan'}
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
