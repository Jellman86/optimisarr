<script lang="ts">
  import { api, type Settings } from '../api'

  let settings = $state<Settings>({ maxConcurrentJobs: 1 })
  let loading = $state(true)
  let saving = $state(false)
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    error = null
    try {
      settings = await api.settings()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load settings'
    } finally {
      loading = false
    }
  }

  async function save() {
    saving = true
    error = null
    message = null
    try {
      settings = await api.saveSettings({ maxConcurrentJobs: Number(settings.maxConcurrentJobs) || 1 })
      message = 'Settings saved.'
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to save settings'
    } finally {
      saving = false
    }
  }
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Settings</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">Global options that apply across every library.</p>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{:else if message}
  <div class="card mb-4 border-emerald-300 p-3 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{message}</div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else}
  <div class="card max-w-xl p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Queue</h2>
    <div class="max-w-xs">
      <label class="label" for="max-jobs">Max concurrent jobs</label>
      <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        How many transcodes may run at once across all libraries. Start at 1 and raise it only if your CPU/GPU and disk can keep up.
      </p>
    </div>
    <div class="mt-5">
      <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? 'Saving' : 'Save'}</button>
    </div>
  </div>
{/if}
