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
  import Icon from './Icon.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'

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
  // Minimised collapses the panel to a small floating widget while the transcode keeps running, so
  // the rest of the UI stays usable. The component stays mounted (and keeps polling) either way;
  // only an explicit Close discards the preview.
  let minimized = $state(false)

  const TERMINAL = ['Completed', 'Failed']

  start()

  async function start() {
    try {
      const { jobId: id } = await api.createPreview(mediaFileId)
      jobId = id
      void poll()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.shared.preview_start_error
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
      error = err instanceof Error ? err.message : i18n.m.shared.preview_load_error
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

  // The file name is the heading; the full path is a subheader. Scene separators become spaces.
  let title = $derived(
    ((relativePath.replace(/\\/g, '/').split('/').pop() ?? relativePath)
      .replace(/\.[^.]+$/, '')
      .replace(/[._]+/g, ' ')
      .trim()) || relativePath,
  )

  // Short status line for the minimised widget.
  let statusLabel = $derived(
    error
      ? i18n.m.shared.status_error
      : !preview || preview.status === 'Queued'
        ? i18n.m.shared.status_queuing
        : preview.status === 'Transcoding'
          ? t(i18n.m.shared.status_encoding, { percent: Math.round(preview.progress * 100) })
          : preview.status === 'Verifying'
            ? i18n.m.shared.status_verifying
            : preview.status === 'Failed'
              ? i18n.m.shared.status_failed
              : i18n.m.shared.status_ready,
  )
</script>

{#if minimized}
  <!-- Collapsed: a small floating widget, so the rest of the UI is usable while the preview runs. -->
  <div class="fixed bottom-4 right-4 z-50 w-72 rounded-lg border border-slate-200 bg-white p-3 shadow-lg dark:border-slate-700 dark:bg-slate-900">
    <div class="flex items-center gap-2">
      <div class="min-w-0 flex-1">
        <div class="truncate text-xs font-semibold text-slate-700 dark:text-slate-200" title={title}>{title}</div>
        <div class="text-[11px] text-slate-400">{t(i18n.m.shared.preview_status, { status: statusLabel })}</div>
      </div>
      <button class="btn btn-ghost flex-shrink-0 px-2 py-1" onclick={() => (minimized = false)} title={i18n.m.shared.expand} aria-label={i18n.m.shared.expand}>
        <Icon name="chevron" class="h-4 w-4 rotate-180" />
      </button>
      <button class="btn btn-ghost flex-shrink-0 px-2 py-1 text-red-600 dark:text-red-400" onclick={close} title={i18n.m.shared.discard_preview} aria-label={i18n.m.shared.close}>
        <Icon name="x" class="h-4 w-4" />
      </button>
    </div>
    {#if error}
      <p class="mt-2 text-[11px] text-red-600 dark:text-red-400">{error}</p>
    {:else if isRunning || !preview}
      <div class="progress-track mt-2"><div class="progress-indeterminate"></div></div>
    {:else if preview.status === 'Completed'}
      <p class="mt-2 text-[11px] text-emerald-600 dark:text-emerald-400">{i18n.m.shared.ready_expand}</p>
    {/if}
  </div>
{:else}
<div
  class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
  role="button"
  tabindex="0"
  onclick={() => (minimized = true)}
  onkeydown={(e) => e.key === 'Escape' && (minimized = true)}
>
  <div
    class="card max-h-[90vh] w-full max-w-4xl overflow-y-auto p-5"
    role="dialog"
    tabindex="-1"
    onclick={(e) => e.stopPropagation()}
    onkeydown={(e) => e.stopPropagation()}
  >
    <div class="mb-4 flex items-start justify-between gap-3">
      <div class="min-w-0">
        <div class="text-[11px] font-semibold uppercase tracking-wide text-cyan-600 dark:text-cyan-400">{i18n.m.shared.preview_optimisation}</div>
        <h2 class="truncate text-lg font-semibold" title={title}>{title}</h2>
        <p class="truncate font-mono text-xs text-slate-500 dark:text-slate-400" title={relativePath}>{relativePath}</p>
      </div>
      <div class="flex flex-shrink-0 items-center gap-1">
        <button class="btn btn-ghost px-2" onclick={() => (minimized = true)} title={i18n.m.shared.minimise_preview} aria-label={i18n.m.shared.minimise}>
          <Icon name="minus" class="h-4 w-4" />
        </button>
        <button class="btn btn-ghost px-2" onclick={close} title={i18n.m.shared.discard_preview} aria-label={i18n.m.shared.close}>
          <Icon name="x" class="h-4 w-4" />
        </button>
      </div>
    </div>

    {#if error}
      <div class="card border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-300">{error}</div>
    {:else if !preview || isRunning}
      <div class="flex flex-col items-center gap-3 py-10 text-slate-500 dark:text-slate-400">
        <div class="progress-track w-64"><div class="progress-indeterminate"></div></div>
        <p class="text-sm">
          {#if !preview || preview.status === 'Queued'}{i18n.m.shared.queuing_preview}
          {:else if preview.status === 'Transcoding'}{t(i18n.m.shared.encoding_progress, { percent: Math.round(preview.progress * 100) })}
          {:else if preview.status === 'Verifying'}{i18n.m.shared.verifying_sample}
          {:else}{preview.status}…{/if}
        </p>
        <p class="text-xs">{i18n.m.shared.preview_safety}</p>
      </div>
    {:else}
      {#if preview.status === 'Failed'}
        <div class="card mb-4 border-amber-300 p-3 text-sm text-amber-800 dark:border-amber-800 dark:text-amber-300">
          {t(i18n.m.shared.preview_failed, { error: preview.errorMessage ?? i18n.m.shared.unknown_error })}
        </div>
      {/if}

      <!-- Side-by-side viewers per media type (only when an encoded output exists) -->
      {#if preview.status !== 'Failed'}
        <div class="mb-5">
          <MediaCompare
            {mediaKind}
            left={{ label: i18n.m.shared.original, url: api.mediaContentUrl(mediaFileId), sizeBytes: preview.original?.sizeBytes }}
            right={{ label: preview.clipped ? i18n.m.shared.encoded_sample : i18n.m.shared.encoded, url: api.previewContentUrl(preview.jobId), sizeBytes: preview.encoded?.sizeBytes }}
          />
          {#if preview.clipped}
            <p class="mt-2 text-xs text-slate-400">
              {t(i18n.m.shared.sample_note, { seconds: Math.round(preview.encoded?.durationSeconds ?? 0) })}
            </p>
          {/if}
        </div>
      {/if}

      <!-- Stats comparison -->
      <div class="card mb-4 overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
            <tr><th class="px-4 py-2"></th><th class="px-4 py-2">{i18n.m.shared.original}</th><th class="px-4 py-2">{i18n.m.shared.encoded}</th></tr>
          </thead>
          <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
            <tr>
              <td class="px-4 py-2 text-slate-500">{i18n.m.shared.col_size}</td>
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
            <tr><td class="px-4 py-2 text-slate-500">{i18n.m.shared.container}</td><td class="px-4 py-2">{preview.original?.container ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.container ?? '—'}</td></tr>
            {#if mediaKind !== 'Audio'}
              <tr><td class="px-4 py-2 text-slate-500">{i18n.m.shared.video_codec}</td><td class="px-4 py-2">{preview.original?.videoCodec ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.videoCodec ?? '—'}</td></tr>
              <tr><td class="px-4 py-2 text-slate-500">{i18n.m.shared.resolution}</td><td class="px-4 py-2">{resolution(preview.original)}</td><td class="px-4 py-2">{resolution(preview.encoded)}</td></tr>
            {/if}
            {#if mediaKind !== 'Image'}
              <tr><td class="px-4 py-2 text-slate-500">{i18n.m.shared.duration}</td><td class="px-4 py-2">{formatDuration(preview.original?.durationSeconds ?? null)}</td><td class="px-4 py-2">{formatDuration(preview.encoded?.durationSeconds ?? null)}</td></tr>
              <tr><td class="px-4 py-2 text-slate-500">{i18n.m.shared.audio}</td><td class="px-4 py-2">{audio(preview.original)}</td><td class="px-4 py-2">{audio(preview.encoded)}</td></tr>
            {/if}
          </tbody>
        </table>
      </div>

      {#if checks}
        <div>
          <div class="mb-2 text-xs font-medium uppercase text-slate-500 dark:text-slate-400">
            {preview.verificationPassed ? i18n.m.shared.verification_passed : i18n.m.shared.verification_failed}{preview.clipped ? ` · ${i18n.m.shared.segment_only}` : ''}
          </div>
          <VerificationChecks {checks} />
        </div>
      {/if}
    {/if}
  </div>
</div>
{/if}
