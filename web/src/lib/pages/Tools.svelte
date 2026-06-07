<script lang="ts">
  import { api, type ToolCheck } from '../api'

  let tools = $state<ToolCheck[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    error = null
    try {
      tools = await api.tools()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load tools'
    } finally {
      loading = false
    }
  }
</script>

<header class="mb-6 flex items-start justify-between">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Tools</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">FFmpeg and ffprobe must be available before probing and transcoding can run.</p>
  </div>
  <button class="btn" onclick={load} disabled={loading}>{loading ? 'Checking' : 'Refresh'}</button>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

<div class="grid gap-4 sm:grid-cols-2">
  {#each tools as tool}
    <div class="card p-4">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-2">
          <span class="font-semibold text-slate-800 dark:text-slate-100">{tool.name}</span>
          <code class="text-xs text-slate-400">{tool.command}</code>
        </div>
        <span
          class="badge {tool.available
            ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300'
            : 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'}"
        >
          {tool.available ? 'Available' : 'Missing'}
        </span>
      </div>
      <div class="mt-2 truncate text-xs text-slate-500 dark:text-slate-400" title={tool.version ?? tool.error ?? ''}>
        {tool.version ?? tool.error ?? ''}
      </div>
    </div>
  {/each}
</div>
