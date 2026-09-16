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
  import Thumbnail from './Thumbnail.svelte'
  import { modal } from '../modal'
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
      if (closed) { discardPreview(); return }
      void poll()
    } catch (err) {
      if (!closed) error = err instanceof Error ? err.message : i18n.m.shared.preview_start_error
    }
  }

  async function poll() {
    if (closed || jobId === null) return
    try {
      const result = await api.getPreview(jobId)
      if (closed) return
      preview = result
      if (!TERMINAL.includes(preview.status)) {
        timer = setTimeout(poll, 1500)
      }
    } catch (err) {
      if (!closed) error = err instanceof Error ? err.message : i18n.m.shared.preview_load_error
    }
  }

  function discardPreview() {
    if (jobId === null) return
    const id = jobId
    jobId = null
    void api.deletePreview(id).catch(() => {})
  }

  function close() {
    closed = true
    if (timer) clearTimeout(timer)
    discardPreview()
    onClose()
  }

  onDestroy(() => {
    closed = true
    if (timer) clearTimeout(timer)
    discardPreview()
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
        : preview.status === 'Probing'
          ? t(i18n.m.shared.adaptive_quality_progress, { percent: Math.max(1, Math.round(preview.progress * 100)) })
        : preview.status === 'Transcoding'
          ? preview.progress > 0
            ? t(i18n.m.shared.status_encoding, { percent: Math.round(preview.progress * 100) })
            : i18n.m.queue.status_transcoding
          : preview.status === 'Verifying'
            ? i18n.m.shared.status_verifying
            : preview.status === 'Failed'
              ? i18n.m.shared.status_failed
              : i18n.m.shared.status_ready,
  )
</script>

{#if minimized}
  <!-- Collapsed: a small floating widget, so the rest of the UI is usable while the preview runs. -->
  <div class="fixed bottom-4 right-4 z-50 w-72 rounded-lg border border-line bg-panel p-3 shadow-lg">
    <div class="flex items-center gap-2">
      <div class="min-w-0 flex-1">
        <div class="truncate text-xs font-semibold text-ink-2" title={title}>{title}</div>
        <div class="text-[11px] text-ink-3">{t(i18n.m.shared.preview_status, { status: statusLabel })}</div>
      </div>
      <button class="btn btn-ghost flex-shrink-0 px-2 py-1" onclick={() => (minimized = false)} title={i18n.m.shared.expand} aria-label={i18n.m.shared.expand}>
        <Icon name="chevron" class="h-4 w-4 rotate-180" />
      </button>
      <button class="btn btn-ghost flex-shrink-0 px-2 py-1 text-bad" onclick={close} title={i18n.m.shared.discard_preview} aria-label={i18n.m.shared.close}>
        <Icon name="x" class="h-4 w-4" />
      </button>
    </div>
    {#if error}
      <p class="mt-2 text-[11px] text-bad">{error}</p>
    {:else if isRunning || !preview}
      <div class="progress-track mt-2"><div class="progress-indeterminate"></div></div>
    {:else if preview.status === 'Completed'}
      <p class="mt-2 text-[11px] text-ok">{i18n.m.shared.ready_expand}</p>
    {/if}
  </div>
{:else}
<dialog class="app-modal preview-dialog" use:modal={() => (minimized = true)} aria-label={`${i18n.m.shared.preview_optimisation}: ${title}`}>
    <div class="preview-heading">
      <div class="preview-poster"><Thumbnail {mediaFileId} size="md" /></div>
      <div class="min-w-0 flex-1">
        <div class="text-[11px] font-semibold uppercase tracking-wide text-accent">{i18n.m.shared.preview_optimisation}</div>
        <h2 class="line-clamp-2 break-words text-lg font-semibold" title={title}>{title}</h2>
        <p class="line-clamp-2 break-all font-mono text-xs text-ink-3" title={relativePath}>{relativePath}</p>
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

    <div class="preview-body">
    {#if error}
      <div class="card tone-bad p-3 text-sm">{error}</div>
    {:else if !preview || isRunning}
      <div class="flex flex-col items-center gap-3 py-10 text-ink-3">
        <div class="progress-track w-64"><div class="progress-indeterminate"></div></div>
        <p class="text-sm">
          {#if !preview || preview.status === 'Queued'}{i18n.m.shared.queuing_preview}
          {:else if preview.status === 'Probing'}{t(i18n.m.shared.adaptive_quality_progress, { percent: Math.max(1, Math.round(preview.progress * 100)) })}
          {:else if preview.status === 'Transcoding'}
            {preview.progress > 0
              ? t(i18n.m.shared.encoding_progress, { percent: Math.round(preview.progress * 100) })
              : `${i18n.m.queue.status_transcoding}…`}
          {:else if preview.status === 'Verifying'}{i18n.m.shared.verifying_sample}
          {:else}{preview.status}…{/if}
        </p>
        <p class="text-xs">{i18n.m.shared.preview_safety}</p>
      </div>
    {:else}
      {#if preview.status === 'Failed'}
        <div class="card mb-4 tone-warn p-3 text-sm">
          {t(i18n.m.shared.preview_failed, { error: preview.errorMessage ?? i18n.m.shared.unknown_error })}
        </div>
      {/if}

      <!-- Side-by-side viewers per media type (only when an encoded output exists) -->
      {#if preview.status !== 'Failed'}
        <div class="mb-5">
          <MediaCompare
            {mediaKind}
            left={{
              label: i18n.m.shared.original,
              url: api.mediaContentUrl(mediaFileId),
              sizeBytes: preview.original?.sizeBytes,
              startSeconds: preview.clipStartSeconds,
            }}
            right={{
              label: preview.clipped ? i18n.m.shared.encoded_sample : i18n.m.shared.encoded,
              url: api.previewContentUrl(preview.jobId),
              sizeBytes: preview.encoded?.sizeBytes,
              startSeconds: 0,
            }}
            comparisonDurationSeconds={preview.clipDurationSeconds}
          />
          {#if preview.clipped}
            <p class="mt-2 text-xs text-ink-3">
              {t(i18n.m.shared.sample_note, { seconds: Math.round(preview.encoded?.durationSeconds ?? 0) })}
            </p>
          {/if}
        </div>
      {/if}

      <!-- Stats comparison -->
      <div class="card mb-4 overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="border-b border-line text-left text-xs uppercase text-ink-3">
            <tr><th class="px-4 py-2"></th><th class="px-4 py-2">{i18n.m.shared.original}</th><th class="px-4 py-2">{i18n.m.shared.encoded}</th></tr>
          </thead>
          <tbody class="divide-y divide-line-soft">
            <tr>
              <td class="px-4 py-2 text-ink-3">{i18n.m.shared.col_size}</td>
              <td class="px-4 py-2">{preview.original?.sizeBytes != null ? formatSize(preview.original.sizeBytes) : '—'}</td>
              <td class="px-4 py-2">
                {preview.encoded?.sizeBytes != null ? formatSize(preview.encoded.sizeBytes) : '—'}
                {#if preview.savingPercent != null}
                  <span class="badge ml-1 {preview.savingPercent >= 0 ? 'tone-ok' : 'tone-bad'}">
                    {preview.clipped ? '≈' : ''}{preview.savingPercent >= 0 ? '−' : '+'}{Math.abs(preview.savingPercent)}%
                  </span>
                {/if}
              </td>
            </tr>
            <tr><td class="px-4 py-2 text-ink-3">{i18n.m.shared.container}</td><td class="px-4 py-2">{preview.original?.container ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.container ?? '—'}</td></tr>
            {#if mediaKind !== 'Audio'}
              <tr><td class="px-4 py-2 text-ink-3">{i18n.m.shared.video_codec}</td><td class="px-4 py-2">{preview.original?.videoCodec ?? '—'}</td><td class="px-4 py-2">{preview.encoded?.videoCodec ?? '—'}</td></tr>
              <tr><td class="px-4 py-2 text-ink-3">{i18n.m.shared.resolution}</td><td class="px-4 py-2">{resolution(preview.original)}</td><td class="px-4 py-2">{resolution(preview.encoded)}</td></tr>
            {/if}
            {#if mediaKind !== 'Image'}
              <tr><td class="px-4 py-2 text-ink-3">{i18n.m.shared.duration}</td><td class="px-4 py-2">{formatDuration(preview.original?.durationSeconds ?? null)}</td><td class="px-4 py-2">{formatDuration(preview.encoded?.durationSeconds ?? null)}</td></tr>
              <tr><td class="px-4 py-2 text-ink-3">{i18n.m.shared.audio}</td><td class="px-4 py-2">{audio(preview.original)}</td><td class="px-4 py-2">{audio(preview.encoded)}</td></tr>
            {/if}
          </tbody>
        </table>
      </div>

      {#if checks}
        <div>
          <div class="mb-2 text-xs font-medium uppercase text-ink-3">
            {preview.verificationPassed ? i18n.m.shared.verification_passed : i18n.m.shared.verification_failed}{preview.clipped ? ` · ${i18n.m.shared.segment_only}` : ''}
          </div>
          <VerificationChecks {checks} />
        </div>
      {/if}
    {/if}
    </div>
</dialog>
{/if}


<style>
  .preview-dialog { width: 60rem; grid-template-rows: auto minmax(0, 1fr); }
  .preview-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 1rem; padding: 1.25rem 1.5rem; background: var(--raised); }
  .preview-body { overflow-y: auto; overscroll-behavior: contain; padding: 1.5rem; min-height: 0; }
  @media(max-width: 639px) { .preview-heading { padding: 1rem; gap: .625rem; }.preview-body { padding: 1rem; }.preview-poster { display: none; } }
  @media(max-height: 540px) { .preview-poster { display: none; }.preview-heading { padding: .75rem 1rem; } }
</style>
