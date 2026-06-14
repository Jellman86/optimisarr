<script lang="ts">
  // Settings preview: runs a throwaway transcode of one file with its library's resolved
  // settings and shows the original next to the encoded result — viewers per media type plus a
  // size/quality stats table and the verification report. Nothing here ever replaces an original;
  // the preview is deleted when this panel closes.
  import { onDestroy } from 'svelte'
  import { api, type PreviewComparison, type MediaSideStats, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize, formatDuration } from '../format'
  import VerificationChecks from './VerificationChecks.svelte'
  import MediaCompare from './MediaCompare.svelte'

  let { mediaFileId, mediaKind, relativePath, onClose }: {
    mediaFileId: number
    mediaKind: string
    relativePath: string
    onClose: () => void
  } = $props()

  let jobId = $state<number | null>(null)
  let preview = $state<PreviewComparison | null>(null)
  let error = $state<string | null>(null)
  let closed = false
  let timer: ReturnType<typeof setTimeout> | null = null

  const TERMINAL = ['Completed', 'Failed']

  start()

  async function start() {
    try {
      const { jobId: id } = await api.createPreview(mediaFileId)
      jobId = id
      void poll()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not start preview'
    }
  }

  async function poll() {
    if (closed || jobId === null) return
    try {
      preview = await api.getPreview(jobId)
      if (!TERMINAL.includes(preview.status)) {
        timer = setTimeout(poll, 1500)
      }
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load preview'
    }
  }

  function close() {
    closed = true
    if (timer) clearTimeout(timer)
    if (jobId !== null) void api.deletePreview(jobId).catch(() => {})
    onClose()
  }

  onDestroy(() => {
    closed = true
    if (timer) clearTimeout(timer)
    if (jobId !== null) void api.deletePreview(jobId).catch(() => {})
  })

  let checks = $derived(parseChecks(preview?.verificationReportJson ?? null))

  function parseChecks(json: string | null): VerificationCheck[] | null {
    if (!json) return null
    try {
      return (JSON.parse(json) as VerificationReport).checks
    } catch {
      return null
    }
  }

  function resolution(s: MediaSideStats | null): string {
    return s?.width && s?.height ? `${s.width}×${s.height}` : '—'
  }

  function audio(s: MediaSideStats | null): string {
    if (!s?.audioCodec) return '—'
    const parts = [s.audioCodec]
    if (s.audioChannels) parts.push(`${s.audioChannels}ch`)
    if (s.audioBitrateKbps) parts.push(`${s.audioBitrateKbps} kbps`)
    return parts.join(' · ')
  }

  let isRunning = $derived(preview !== null && !TERMINAL.includes(preview.status))
</script>

<div
  class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
  role="button"
  tabindex="0"
  onclick={close}
  onkeydown={(e) => e.key === 'Escape' && close()}
>
  <div
    class="card max-h-[90vh] w-full max-w-4xl overflow-y-auto p-5"
    role="dialog"
    tabindex="-1"
    onclick={(e) => e.stopPropagation()}
    onkeydown={(e) => e.stopPropagation()}
  >
    <div class="mb-4 flex items-start justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold">Preview optimisation</h2>
        <p class="truncate font-mono text-xs text-slate-500 dark:text-slate-400" title={relativePath}>{relativePath}</p>
      </div>
      <button class="btn flex-shrink-0" onclick={close}>Close</button>
    </div>

    {#if error}
      <div class="card border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-300">{error}</div>
    {:else if !preview || isRunning}
      <div class="flex flex-col items-center gap-3 py-10 text-slate-500 dark:text-slate-400">
        <div class="progress-track w-64"><div class="progress-indeterminate"></div></div>
        <p class="text-sm">
          {#if !preview || preview.status === 'Queued'}Queuing preview…
          {:else if preview.status === 'Transcoding'}Encoding… {Math.round(preview.progress * 100)}%
          {:else}{preview.status}…{/if}
        </p>
        <p class="text-xs">This runs a real transcode of this one file; it never touches the original.</p>
      </div>
    {:else}
      {#if preview.status === 'Failed'}
        <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">
          The preview transcode failed: {preview.errorMessage ?? 'unknown error'}. The original is untouched.
        </div>
      {/if}

      <!-- Side-by-side viewers per media type (only when an encoded output exists) -->
      {#if preview.status !== 'Failed'}
        <div class="mb-5">
          <MediaCompare
            {mediaKind}
            left={{ label: 'Original', url: api.mediaContentUrl(mediaFileId), sizeBytes: preview.original?.sizeBytes }}
            right={{ label: preview.clipped ? 'Encoded (sample)' : 'Encoded', url: api.previewContentUrl(preview.jobId), sizeBytes: preview.encoded?.sizeBytes }}
          />
          {#if preview.clipped}
            <p class="mt-2 text-xs text-slate-400">
              The encoded side is a {Math.round(preview.encoded?.durationSeconds ?? 0)}s sample from the middle of the file, so the saving is estimated from its bitrate. A full optimise encodes the whole file.
            </p>
          {/if}
        </div>
      {/if}

      <!-- Stats comparison -->
      <div class="card mb-4 overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
            <tr><th class="px-4 py-2"></th><th class="px-4 py-2">Original</th><th class="px-4 py-2">Encoded</th></tr>
          </thead>
          <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
            <tr>
              <td class="px-4 py-2 text-slate-500">Size</td>
              <td class="px-4 py-2">{preview.original?.sizeBytes != null ? formatSize(preview.original.sizeBytes) : '—'}</td>
              <td class="px-4 py-2">
                {preview.encoded?.sizeBytes != null ? formatSize(preview.encoded.sizeBytes) : '—'}
                {#if preview.savingPercent != null}
                  <span class="badge ml-1 {preview.savingPercent >= 0 ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400' : 'bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-400'}">
                    {preview.clipped ? '≈' : ''}{preview.savingPercent >= 0 ? '−' : '+'}{Math.abs(preview.savingPercent)}%
                  </span>
                {/if}
              </td>
            </tr>
            <tr><td class="px-4 py-2 text-slate-500">Container</td><td class="px-4 py-2">{preview.original?.container ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.container ?? '—'}</td></tr>
            {#if mediaKind !== 'Audio'}
              <tr><td class="px-4 py-2 text-slate-500">Video codec</td><td class="px-4 py-2">{preview.original?.videoCodec ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.videoCodec ?? '—'}</td></tr>
              <tr><td class="px-4 py-2 text-slate-500">Resolution</td><td class="px-4 py-2">{resolution(preview.original)}</td><td class="px-4 py-2">{resolution(preview.encoded)}</td></tr>
            {/if}
            {#if mediaKind !== 'Image'}
              <tr><td class="px-4 py-2 text-slate-500">Duration</td><td class="px-4 py-2">{formatDuration(preview.original?.durationSeconds ?? null)}</td><td class="px-4 py-2">{formatDuration(preview.encoded?.durationSeconds ?? null)}</td></tr>
              <tr><td class="px-4 py-2 text-slate-500">Audio</td><td class="px-4 py-2">{audio(preview.original)}</td><td class="px-4 py-2">{audio(preview.encoded)}</td></tr>
            {/if}
          </tbody>
        </table>
      </div>

      {#if checks}
        <div>
          <div class="mb-2 text-xs font-medium uppercase text-slate-500 dark:text-slate-400">
            Verification {preview.verificationPassed ? '✓ passed' : '✗ failed'}
          </div>
          <VerificationChecks {checks} />
        </div>
      {/if}
    {/if}
  </div>
</div>
