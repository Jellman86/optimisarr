<script lang="ts">
  // The hardware & tools panel: FFmpeg/ffprobe availability, hardware acceleration, and
  // detected encoders. Loads its own data so it can be dropped into the Settings "Tools"
  // tab (or anywhere) without the host wiring anything up.
  import { api, type EncoderCapability, type HardwareCapability, type ToolCheck } from '../api'
  import Banner from './Banner.svelte'

  let tools = $state<ToolCheck[]>([])
  let hardware = $state<HardwareCapability | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    error = null
    try {
      const [nextTools, nextHardware] = await Promise.all([api.tools(), api.hardware()])
      tools = nextTools
      hardware = nextHardware
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load tools'
    } finally {
      loading = false
    }
  }

  let encoderGroups = $derived(groupEncoders(hardware?.encoders ?? []))

  function groupEncoders(encoders: EncoderCapability[]) {
    const groups = new Map<string, EncoderCapability[]>()
    for (const encoder of encoders) {
      groups.set(encoder.mode, [...(groups.get(encoder.mode) ?? []), encoder])
    }
    return [...groups.entries()]
  }
</script>

<div class="mb-4 flex items-start justify-between gap-4">
  <p class="text-sm text-slate-500 dark:text-slate-400">
    FFmpeg and ffprobe must be available before probing and transcoding can run.
  </p>
  <button class="btn flex-shrink-0" onclick={load} disabled={loading}>{loading ? 'Checking' : 'Refresh'}</button>
</div>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
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

{#if hardware}
  <section class="mt-6">
    <h2 class="mb-3 font-semibold text-slate-800 dark:text-slate-100">Hardware acceleration</h2>
    {#if hardware.error}
      <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">{hardware.error}</div>
    {/if}
    <div class="grid gap-4 lg:grid-cols-3">
      <div class="card p-4">
        <div class="text-sm font-semibold text-slate-800 dark:text-slate-100">FFmpeg hwaccels</div>
        <div class="mt-3 flex flex-wrap gap-2">
          {#each hardware.hardwareAccelerators as accelerator}
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{accelerator}</span>
          {:else}
            <span class="text-xs text-slate-400">None reported</span>
          {/each}
        </div>
      </div>

      <div class="card p-4">
        <div class="text-sm font-semibold text-slate-800 dark:text-slate-100">NVIDIA runtime</div>
        <span class="badge mt-3 {hardware.nvidiaRuntimeAvailable ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
          {hardware.nvidiaRuntimeAvailable ? 'Available' : 'Not detected'}
        </span>
      </div>

      <div class="card p-4">
        <div class="text-sm font-semibold text-slate-800 dark:text-slate-100">DRI device</div>
        <span class="badge mt-3 {hardware.driDeviceAvailable ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
          {hardware.driDeviceAvailable ? '/dev/dri mapped' : 'Not mapped'}
        </span>
      </div>
    </div>
  </section>

  <section class="mt-6">
    <h2 class="mb-3 font-semibold text-slate-800 dark:text-slate-100">Encoders</h2>
    <div class="grid gap-4 lg:grid-cols-2">
      {#each encoderGroups as [mode, encoders]}
        <div class="card p-4">
          <div class="mb-3 font-semibold text-slate-800 dark:text-slate-100">{mode}</div>
          <div class="grid gap-2 sm:grid-cols-3">
            {#each encoders as encoder}
              <div class="rounded border border-slate-200 p-2 text-xs dark:border-slate-800">
                <div class="font-mono text-slate-700 dark:text-slate-200">{encoder.name}</div>
                <div class="mt-1 flex items-center justify-between gap-2">
                  <span class="uppercase text-slate-400">{encoder.codec}</span>
                  <span class={encoder.available ? 'text-emerald-600 dark:text-emerald-400' : 'text-slate-400'}>
                    {encoder.available ? 'available' : 'missing'}
                  </span>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/each}
    </div>
  </section>
{/if}
