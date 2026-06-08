<script lang="ts">
  import { api, type Settings } from '../api'
  import { formatSize } from '../format'

  let settings = $state<Settings>({
    maxConcurrentJobs: 1,
    scheduleEnabled: false,
    scheduleWindowStart: '00:00',
    scheduleWindowEnd: '00:00',
    minFreeDiskBytes: 10 * 1024 * 1024 * 1024,
    cpuThreadLimit: 0,
    encoderMode: 'Auto',
    verificationDurationTolerancePercent: 1,
    verificationRequireAudioRetained: true,
    verificationRequireSubtitlesRetained: false,
    verificationRequireSizeReduction: true,
    replacementAllowCrossFilesystem: false,
    replacementQuarantineRetentionDays: 0,
  })
  let minFreeDiskGiB = $state('10')
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
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
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
      settings = await api.saveSettings({
        ...settings,
        maxConcurrentJobs: Number(settings.maxConcurrentJobs) || 1,
        cpuThreadLimit: Math.max(0, Number(settings.cpuThreadLimit) || 0),
        verificationDurationTolerancePercent: Math.max(0, Number(settings.verificationDurationTolerancePercent) || 0),
        minFreeDiskBytes: gibToBytes(minFreeDiskGiB),
      })
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
      message = 'Settings saved.'
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to save settings'
    } finally {
      saving = false
    }
  }

  function gibToBytes(value: string) {
    const parsed = Number(value)
    if (!Number.isFinite(parsed) || parsed < 0) return 0
    return Math.round(parsed * 1024 * 1024 * 1024)
  }

  function bytesToGiB(value: number) {
    return (value / 1024 / 1024 / 1024).toString()
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
  <div class="card max-w-2xl p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Queue</h2>
    <div class="grid gap-5 sm:grid-cols-2">
      <div>
      <label class="label" for="max-jobs">Max concurrent jobs</label>
      <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        How many transcodes may run at once across all libraries. Start at 1 and raise it only if your CPU/GPU and disk can keep up.
      </p>
      </div>

      <div>
        <label class="label" for="encoder-mode">Encoder mode</label>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          Auto prefers hardware encoders when available, then falls back to CPU.
        </p>
      </div>

      <div>
        <label class="label" for="cpu-threads">CPU thread limit</label>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          Passed to FFmpeg as <code>-threads</code> for each job. Set 0 to let FFmpeg decide.
        </p>
      </div>

      <div>
        <label class="label" for="free-disk">Minimum free work disk</label>
        <div class="flex items-center gap-2">
          <input id="free-disk" class="input" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="text-sm text-slate-500 dark:text-slate-400">GiB</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          New jobs pause below this free space threshold. Set 0 to disable. Current setting: {formatSize(gibToBytes(minFreeDiskGiB))}.
        </p>
      </div>
    </div>

    <div class="mt-6 border-t border-slate-200 pt-5 dark:border-slate-800">
      <label class="mb-3 flex items-center gap-2 text-sm font-medium text-slate-700 dark:text-slate-200">
        <input type="checkbox" bind:checked={settings.scheduleEnabled} />
        Restrict new jobs to a processing window
      </label>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label" for="window-start">Window start</label>
          <input id="window-start" class="input" type="time" bind:value={settings.scheduleWindowStart} disabled={!settings.scheduleEnabled} />
        </div>
        <div>
          <label class="label" for="window-end">Window end</label>
          <input id="window-end" class="input" type="time" bind:value={settings.scheduleWindowEnd} disabled={!settings.scheduleEnabled} />
        </div>
      </div>
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        A start and end of 00:00 means all day. Overnight windows such as 22:00 to 06:00 are supported.
      </p>
    </div>

    <div class="mt-5">
      <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? 'Saving' : 'Save'}</button>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Verification gates</h2>
    <div class="max-w-xs">
      <label class="label" for="duration-tolerance">Duration tolerance</label>
      <div class="flex items-center gap-2">
        <input
          id="duration-tolerance"
          class="input"
          type="number"
          min="0"
          step="0.1"
          bind:value={settings.verificationDurationTolerancePercent}
        />
        <span class="text-sm text-slate-500 dark:text-slate-400">%</span>
      </div>
    </div>

    <div class="mt-5 grid gap-3 text-sm text-slate-700 dark:text-slate-200">
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={settings.verificationRequireAudioRetained} />
        Require all audio tracks to be retained
      </label>
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={settings.verificationRequireSubtitlesRetained} />
        Require all subtitle tracks to be retained
      </label>
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={settings.verificationRequireSizeReduction} />
        Require output to be smaller than the original
      </label>
    </div>

    <p class="mt-3 text-xs text-slate-500 dark:text-slate-400">
      Decode health, output readability, and a video stream are always required.
    </p>

    <h3 class="mt-6 text-sm font-semibold text-slate-900 dark:text-slate-100">Replacement</h3>
    <div class="mt-3 grid gap-3 text-sm text-slate-700 dark:text-slate-200">
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={settings.replacementAllowCrossFilesystem} />
        Allow cross-filesystem replacement (copy-plus-delete instead of atomic move)
      </label>
      <div class="flex items-center gap-2">
        <span>Quarantine retention</span>
        <input
          class="input w-24"
          type="number"
          min="0"
          step="1"
          bind:value={settings.replacementQuarantineRetentionDays}
        />
        <span class="text-sm text-slate-500 dark:text-slate-400">days (0 = keep indefinitely)</span>
      </div>
    </div>

    <div class="mt-5">
      <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? 'Saving' : 'Save'}</button>
    </div>
  </div>
{/if}
