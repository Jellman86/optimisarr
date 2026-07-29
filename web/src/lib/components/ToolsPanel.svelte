<script lang="ts">
  // The hardware & tools panel: FFmpeg/ffprobe availability, hardware acceleration, and
  // detected encoders. Loads its own data so it can be dropped into the Settings "Tools"
  // tab (or anywhere) without the host wiring anything up.
  import { api, type EncoderCapability, type HardwareCapability, type ToolCheck } from '../api'
  import Banner from './Banner.svelte'
  import ConfigSection from './ConfigSection.svelte'
  import { i18n } from '../i18n/i18n.svelte'

  let tools = $state<ToolCheck[]>([])
  let hardware = $state<HardwareCapability | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void load()
  })

  // Initial load uses the cached detection (fast); the Refresh button forces a fresh probe,
  // re-running the per-encoder test encodes (e.g. after adding a GPU or fixing a driver).
  async function load(refresh = false) {
    loading = true
    error = null
    try {
      const [nextTools, nextHardware] = await Promise.all([api.tools(), api.hardware(refresh)])
      tools = nextTools
      hardware = nextHardware
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.shared.tools_load_error
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

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

<div class="min-w-0 space-y-5">
  <ConfigSection
    step={1}
    id="global-tools"
    title={i18n.m.settings.tab_tools}
    description={i18n.m.shared.tools_intro}
  >
    <div class="mb-4 flex justify-end">
      <button class="btn min-h-11 w-full sm:w-auto" onclick={() => load(true)} disabled={loading}>
        {loading ? i18n.m.common.checking : i18n.m.shared.refresh}
      </button>
    </div>

    <div class="grid min-w-0 gap-4 lg:grid-cols-2">
      {#each tools as tool}
        <article class="card min-w-0 p-4" data-tool-card>
          <div class="grid min-w-0 gap-3 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-1">
                <h3 class="font-semibold text-slate-800 dark:text-slate-100">{tool.name}</h3>
                {#if !tool.required}<span class="text-xs text-slate-400">{i18n.m.settings.optional}</span>{/if}
              </div>
              <code class="mt-1 block break-all text-xs leading-relaxed text-slate-400">{tool.command}</code>
            </div>
            <span
              class="badge w-fit max-w-full {tool.available
                ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300'
                : tool.required
                  ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
                  : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}"
            >
              {tool.available ? i18n.m.shared.available : i18n.m.shared.missing}
            </span>
          </div>
          <p
            class="mt-3 break-words text-xs leading-relaxed text-slate-500 [overflow-wrap:anywhere] dark:text-slate-400"
            title={tool.version ?? tool.error ?? ''}
          >
            {tool.version ?? tool.error ?? ''}
          </p>
        </article>
      {/each}
    </div>
  </ConfigSection>

  {#if hardware}
    <ConfigSection
      step={2}
      id="global-hardware"
      title={i18n.m.shared.hardware_acceleration}
      description={i18n.m.shared.hardware_acceleration_desc}
    >
      {#if hardware.error}
        <div class="mb-4 rounded-lg border border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">{hardware.error}</div>
      {/if}
      <div class="grid min-w-0 gap-4 lg:grid-cols-3">
        <article class="card min-w-0 p-4">
          <h3 class="text-sm font-semibold text-slate-800 dark:text-slate-100">{i18n.m.shared.ffmpeg_hwaccels}</h3>
          <div class="mt-3 flex min-w-0 flex-wrap gap-2">
            {#each hardware.hardwareAccelerators as accelerator}
              <span class="badge max-w-full break-all bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{accelerator}</span>
            {:else}
              <span class="text-xs text-slate-400">{i18n.m.shared.none_reported}</span>
            {/each}
          </div>
        </article>

        <article class="card min-w-0 p-4">
          <h3 class="text-sm font-semibold text-slate-800 dark:text-slate-100">{i18n.m.shared.nvidia_runtime}</h3>
          <span class="badge mt-3 max-w-full {hardware.nvidiaRuntimeAvailable ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
            {hardware.nvidiaRuntimeAvailable ? i18n.m.shared.available : i18n.m.shared.not_detected}
          </span>
        </article>

        <article class="card min-w-0 p-4">
          <h3 class="text-sm font-semibold text-slate-800 dark:text-slate-100">{i18n.m.shared.dri_device}</h3>
          <span class="badge mt-3 max-w-full {hardware.driDeviceAvailable ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
            {hardware.driDeviceAvailable ? i18n.m.shared.mapped : i18n.m.shared.not_mapped}
          </span>
        </article>
      </div>
    </ConfigSection>

    <ConfigSection
      step={3}
      id="global-encoders"
      title={i18n.m.shared.encoders}
      description={i18n.m.shared.encoders_desc}
    >
      <div class="grid min-w-0 gap-4 lg:grid-cols-2">
        {#each encoderGroups as [mode, encoders]}
          <article class="card min-w-0 p-4">
            <h3 class="mb-3 font-semibold text-slate-800 dark:text-slate-100">{mode}</h3>
            <div class="grid min-w-0 gap-2 sm:grid-cols-3">
              {#each encoders as encoder}
                <div class="min-w-0 rounded border border-slate-200 p-3 text-xs dark:border-slate-800">
                  <div class="break-all font-mono text-slate-700 dark:text-slate-200">{encoder.name}</div>
                  <div class="mt-2 flex min-w-0 flex-wrap items-center justify-between gap-2">
                    <span class="uppercase text-slate-400">{encoder.codec}</span>
                    <span class={encoder.available ? 'text-emerald-600 dark:text-emerald-400' : 'text-slate-400'}>
                      {encoder.available ? i18n.m.shared.available : i18n.m.shared.missing}
                    </span>
                  </div>
                </div>
              {/each}
            </div>
          </article>
        {/each}
      </div>
    </ConfigSection>
  {/if}
</div>
