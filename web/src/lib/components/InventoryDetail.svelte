<script lang="ts">
  import { api, type InventoryRow } from '../api'
  import { containerLabel, formatDuration, formatSize, interlacedFieldOrder, languageList } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import Chip from './Chip.svelte'
  import DetailDialog from './DetailDialog.svelte'
  import InfoTip from './InfoTip.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import Icon from './Icon.svelte'

  let { row, libraryName, probing, error, onclose, onprobe, onpreview, onretried }: {
    row: InventoryRow; libraryName: string; probing: boolean; error: string | null
    onclose: () => void; onprobe: () => void; onpreview: () => void; onretried: () => void
  } = $props()
  let retrying = $state(false)
  let retried = $state(false)
  let retryError = $state<string | null>(null)

  // A file held back by its last failure is offered that job again here, where the user found it.
  async function tryAgain(jobId: number) {
    if (retrying) return
    retrying = true
    retryError = null
    try {
      await api.retryJob(jobId)
      retried = true
      onretried()
    } catch (err) {
      retryError = err instanceof Error ? err.message : i18n.m.inventory.error_queue
    } finally {
      retrying = false
    }
  }
  let file = $derived(row.file)
  let title = $derived((file.relativePath.split(/[\\/]/).pop() ?? file.relativePath).replace(/\.[^.]+$/, '').replace(/[._]+/g, ' '))
  let artworkFailed = $state(false)
  let artworkLoaded = $state(false)
  let container = $derived(containerLabel(file.container, file.relativePath))
  let fieldShownFirst = $derived(interlacedFieldOrder(file.fieldOrder))
  let verdict = $derived(row.eligible === null ? i18n.m.inventory.badge_unprobed : row.eligible ? i18n.m.onePlan.yes : i18n.m.onePlan.no)
</script>

<DetailDialog
  {title}
  titleId="inventory-detail-title"
  eyebrow={`${libraryName}${file.mediaKind && file.mediaKind !== 'Unknown' ? ` · ${file.mediaKind}` : ''}`}
  closeLabel={i18n.m.shared.close_detail}
  {onclose}
>
  {#snippet backdrop()}
    {#if !artworkFailed}
      <img class="detail-backdrop" class:artwork-loaded={artworkLoaded} src="/api/media/{file.id}/thumbnail" alt="" loading="lazy" onload={() => (artworkLoaded = true)} onerror={() => (artworkFailed = true)} />
    {/if}
    <div class="detail-wash"></div>
  {/snippet}
  {#snippet leading()}
    <Thumbnail mediaFileId={file.id} size="poster" shape={file.mediaKind === 'Audio' || file.mediaKind === 'Image' ? 'square' : 'portrait'} />
  {/snippet}
  {#snippet meta()}
    <span>{formatSize(file.sizeBytes)}</span>{#if container}<span>{container}</span>{/if}{#if file.durationSeconds !== null}<span>{formatDuration(file.durationSeconds)}</span>{/if}
    {#if fieldShownFirst}
      <span class="inline-flex items-center gap-1.5">
        <Chip><span data-interlaced-tag>{i18n.m.inventory.tag_interlaced}</span></Chip>
        <InfoTip label={i18n.m.inventory.field_order_label} text={t(i18n.m.inventory.interlaced_tip, { order: fieldShownFirst === 'top' ? i18n.m.inventory.field_order_top : i18n.m.inventory.field_order_bottom, code: file.fieldOrder ?? '' })} />
      </span>
    {/if}
  {/snippet}
  {#snippet actions()}
    <button class="btn" onclick={onprobe} disabled={probing}><Icon name="search" />{probing ? i18n.m.inventory.probing : file.status === 'Discovered' ? i18n.m.inventory.probe : i18n.m.inventory.reprobe}</button>
    {#if row.eligible}<button class="btn" onclick={onpreview} disabled={probing}><Icon name="play" />{i18n.m.inventory.preview}</button>{/if}
    <!-- A file the rules skip keeps the button, off, and the reason is the verdict above it. -->
    {#if row.eligible}
      <a class="btn btn-primary" href={`#/inventory/optimise/${file.id}`}>{i18n.m.onePlan.open}</a>
    {:else if row.retryJobId != null && !retried}
      {@const retryJobId = row.retryJobId}
      <button class="btn btn-primary" onclick={() => void tryAgain(retryJobId)} disabled={retrying} aria-describedby="inventory-detail-verdict" data-inventory-try-again>
        <Icon name="retry" />{retrying ? i18n.m.inventory.queueing : i18n.m.onePlan.try_again}
      </button>
    {:else}
      <button class="btn btn-primary" disabled aria-describedby="inventory-detail-verdict">{i18n.m.onePlan.open}</button>
    {/if}
  {/snippet}

  <section class="detail-verdict" aria-label={i18n.m.onePlan.can_it}>
    <div class="flex flex-wrap items-center justify-between gap-2"><h3>{i18n.m.onePlan.can_it}</h3><span class="badge {row.eligible ? 'tone-ok' : 'tone-muted'}">{verdict}</span></div>
    <p id="inventory-detail-verdict">{row.reason ?? i18n.m.inventory.verdict_probe_hint}</p>
    {#if row.eligible === false && file.libraryId && row.retryJobId == null}
      <p>{t(i18n.m.onePlan.skipped_where, { library: libraryName })} <a class="focus-ring font-medium text-accent underline" href={`#/libraries/${file.libraryId}/configure/files`}>{libraryName} → {i18n.m.librarySummary.topic_files}</a></p>
    {/if}
  </section>
  {#if error}<p class="callout tone-bad mb-4" role="alert">{error}</p>{/if}
  {#if retryError}<p class="callout tone-bad mb-4" role="alert">{retryError}</p>{/if}
  {#if retried}
    <p class="callout tone-ok mb-4" role="status" data-inventory-retried>
      {i18n.m.inventory.queue_added}
      <a class="focus-ring font-medium underline" href="#/queue">{i18n.m.onePlan.open_queue}</a>
    </p>
  {/if}
  {#if file.probeError}<p class="callout tone-bad mb-4">{t(i18n.m.inventory.probe_failed, { error: file.probeError })}</p>{/if}
  <dl class="detail-specs">
    <div><dt>{i18n.m.inventory.detail_status}</dt><dd>{file.status}</dd></div>
    <div><dt>{i18n.m.inventory.detail_size}</dt><dd>{formatSize(file.sizeBytes)}</dd></div>
    <div><dt>{i18n.m.inventory.detail_container}</dt><dd>{container ?? '—'}</dd></div>
    {#if file.mediaKind !== 'Audio'}<div><dt>{i18n.m.inventory.detail_video}</dt><dd>{file.videoCodec ?? '—'}{#if file.width && file.height}<span>{file.width} × {file.height}</span>{/if}</dd></div>{/if}
    {#if file.mediaKind !== 'Image'}
      <div><dt>{i18n.m.inventory.detail_audio}</dt><dd>{file.audioCodecs ?? '—'}{#if file.audioTrackCount !== null}{plural(file.audioTrackCount, i18n.m.inventory.audio_tracks_one, i18n.m.inventory.audio_tracks, file.audioTrackCount.toLocaleString())}{/if}{#if file.audioLanguages}<span>{languageList(file.audioLanguages, i18n.m.inventory.unknown_language)}</span>{/if}</dd></div>
      <div><dt>{i18n.m.inventory.detail_subtitles}</dt><dd>{file.subtitleTrackCount ?? '—'}</dd></div>
      <div><dt>{i18n.m.inventory.detail_duration}</dt><dd>{formatDuration(file.durationSeconds)}</dd></div>
    {/if}
  </dl>
  <div class="detail-path"><h3>{i18n.m.inventory.col_file}</h3><p>{file.relativePath}</p></div>
</DetailDialog>

<style>
  .detail-backdrop { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: 50% 35%; filter: blur(10px); transform: scale(1.1); opacity: 0; }
  .detail-backdrop.artwork-loaded { opacity: .3; }
  .detail-wash { position: absolute; inset: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--panel) 15%, transparent), color-mix(in srgb, var(--panel) 88%, transparent) 65%), linear-gradient(0deg, var(--panel), transparent 70%); }
  .detail-verdict { background: var(--sunken); border-radius: .75rem; padding: 1rem 1.125rem; margin-bottom: 1.125rem; }
  h3 { font-size: .75rem; font-weight: 600; color: var(--ink-2); }
  .detail-verdict p { margin-top: .625rem; color: var(--ink-2); font-size: .8125rem; line-height: 1.65; }
  .detail-specs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); column-gap: 2rem; }
  .detail-specs > div { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; padding: .875rem 0; border-bottom: 1px solid var(--divide-soft); font-size: .8125rem; }
  dt { color: var(--ink-3); } dd { text-align: right; color: var(--ink-2); overflow-wrap: anywhere; min-width: 0; } dd span { display: block; color: var(--ink-3); font-size: .75rem; margin-top: .25rem; }
  .detail-path { margin-top: 1.5rem; }.detail-path p { margin-top: .5rem; color: var(--ink-3); font: .6875rem/1.8 var(--font-mono); overflow-wrap: anywhere; }
  @media (max-width: 639px) { .detail-specs { grid-template-columns: 1fr; } }
</style>
