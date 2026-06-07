<script lang="ts">
  import { api, type Library, type LibraryOptions, type SaveLibrary } from '../api'
  import FolderPicker from '../components/FolderPicker.svelte'

  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions>({ mediaTypes: [], ruleProfiles: [] })
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)
  let busyId = $state<number | null>(null)
  let pickerOpen = $state(false)

  // null = not editing; 0 = adding new; >0 = editing that id.
  let editingId = $state<number | null>(null)
  let form = $state<SaveLibrary>(blankForm())

  $effect(() => {
    void load()
  })

  function blankForm(): SaveLibrary {
    return { name: '', path: '', mediaType: 'Film', ruleProfile: 'ConservativeHevc', enabled: true }
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
    editingId = 0
    form = blankForm()
    if (options.mediaTypes.length) form.mediaType = options.mediaTypes[0]
    if (options.ruleProfiles.length) form.ruleProfile = options.ruleProfiles[0]
  }

  function startEdit(library: Library) {
    editingId = library.id
    form = {
      name: library.name,
      path: library.path,
      mediaType: library.mediaType,
      ruleProfile: library.ruleProfile,
      enabled: library.enabled,
    }
  }

  function cancelEdit() {
    editingId = null
  }

  async function save() {
    error = null
    message = null
    try {
      if (editingId === 0) {
        await api.createLibrary(form)
        message = `Added library "${form.name}".`
      } else if (editingId) {
        await api.updateLibrary(editingId, form)
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
      Add one library per content type. Each library has its own rule profile, so TV, film, and music can be optimised differently.
    </p>
  </div>
  {#if editingId === null}
    <button class="btn btn-primary" onclick={startAdd}>Add library</button>
  {/if}
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{:else if message}
  <div class="card mb-4 border-emerald-300 p-3 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{message}</div>
{/if}

{#if editingId !== null}
  <div class="card mb-6 p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">{editingId === 0 ? 'Add library' : 'Edit library'}</h2>
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
        <label class="label" for="lib-rule">Rule profile</label>
        <select id="lib-rule" class="input" bind:value={form.ruleProfile}>
          {#each options.ruleProfiles as rule}<option value={rule}>{rule}</option>{/each}
        </select>
      </div>
    </div>
    <label class="mt-4 flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
      <input type="checkbox" bind:checked={form.enabled} /> Enabled (included in scans)
    </label>
    <div class="mt-5 flex gap-2">
      <button class="btn btn-primary" onclick={save} disabled={!form.name || !form.path}>Save</button>
      <button class="btn" onclick={cancelEdit}>Cancel</button>
    </div>
  </div>
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

{#if libraries.length > 0}
  <div class="grid gap-4">
    {#each libraries as library (library.id)}
      <div class="card flex flex-wrap items-center justify-between gap-4 p-4">
        <div class="min-w-0">
          <div class="flex items-center gap-2">
            <span class="font-semibold text-slate-800 dark:text-slate-100">{library.name}</span>
            <span class="badge bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300">{library.mediaType}</span>
            <span class="badge bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300">{library.ruleProfile}</span>
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
          <button class="btn" onclick={() => startEdit(library)} disabled={busyId === library.id}>Edit</button>
          <button class="btn btn-danger" onclick={() => remove(library)} disabled={busyId === library.id}>Delete</button>
        </div>
      </div>
    {/each}
  </div>
{:else if editingId === null}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No libraries yet. Add one to start discovering media.
  </div>
{/if}
