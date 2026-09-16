<script lang="ts">
  // The card at the foot of the rail that says what the server is working on right now, in the
  // manner of a player's now-playing strip: artwork, the title, the encoder, how far along it is
  // and how long is left. The mark above already says *that* work is running; this says what.
  // It is a link to the Queue, where the rest of the story is.
  import { activity } from '../stores/activity.svelte'
  import { counts } from '../stores/counts.svelte'
  import { layout } from '../stores/ui.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { formatDuration } from '../format'
  import { jobPercent } from '../job-presentation'
  import Thumbnail from './Thumbnail.svelte'

  let { collapsed = false }: { collapsed?: boolean } = $props()

  let job = $derived(activity.leadJob)
  let percent = $derived(job ? jobPercent(job) : null)
  let eta = $derived(activity.leadProgress?.etaSeconds ?? null)
  let speed = $derived(activity.leadProgress?.speed ?? null)
  let finishing = $derived(activity.leadProgress?.finishing === true)

  // "Firefly – S01E09 – War Stories", not the whole library path: the folder is where it lives,
  // the file name is what it is.
  let title = $derived.by(() => {
    const path = job?.relativePath
    if (!path) return i18n.m.dashboard.unnamed_file
    const name = path.split('/').pop() || path
    return name.replace(/\.[a-z0-9]{2,4}$/i, '')
  })
  let where = $derived(job?.workerName ?? i18n.m.dashboard.this_server)
  let queued = $derived(counts.queued ?? 0)
</script>

{#if job}
  {#if collapsed}
    <!-- The icon rail: the artwork alone, with the bar beneath it, and the whole story in the title. -->
    <a
      href="#/queue"
      class="mx-auto mb-2 flex w-10 flex-col gap-1.5 rounded-lg focus-ring"
      title={`${i18n.m.app.now_encoding} · ${title}${percent === null ? '' : ` · ${percent}%`}`}
      onclick={() => layout.closeMobile()}
    >
      <Thumbnail mediaFileId={job.mediaFileId} size="sm" />
      <span class="progress-track block h-1 w-full flex-none" role="progressbar" aria-valuenow={percent ?? undefined} aria-valuemin="0" aria-valuemax="100">
        {#if percent !== null}<span class="progress-fill block" style="width: {percent}%"></span>
        {:else}<span class="progress-indeterminate block"></span>{/if}
      </span>
    </a>
  {:else}
    <a
      href="#/queue"
      class="surface-sunken mx-2.5 flex flex-col gap-2.5 p-3 no-underline transition-colors hover:text-ink focus-ring"
      aria-label={`${i18n.m.app.now_encoding}: ${title}`}
      onclick={() => layout.closeMobile()}
    >
      <span class="flex items-center gap-2">
        <span class="h-1.5 w-1.5 flex-none animate-pulse rounded-full bg-accent shadow-[0_0_8px_var(--accent)]" aria-hidden="true"></span>
        <span class="label mb-0">{i18n.m.app.now_encoding}</span>
        {#if finishing}
          <span class="ml-auto font-mono text-[10.5px] text-ink-3">{i18n.m.app.finishing}</span>
        {:else if eta != null}
          <span class="ml-auto font-mono text-[10.5px] text-ink-3">{t(i18n.m.app.time_left, { time: formatDuration(eta) })}</span>
        {/if}
      </span>

      <span class="flex items-center gap-2.5">
        <Thumbnail mediaFileId={job.mediaFileId} size="md" />
        <span class="flex min-w-0 flex-col gap-1">
          <span class="line-clamp-2 text-[13px] font-semibold leading-snug text-ink">{title}</span>
          <span class="truncate font-mono text-[10.5px] text-ink-3">{where}</span>
          {#if job.videoEncoder}
            <span class="flex">
              <span class="badge font-mono {activity.hardwareActive ? 'tone-ok' : 'tone-neutral'}">{job.videoEncoder}</span>
            </span>
          {/if}
        </span>
      </span>

      <span class="progress-track block h-1 w-full flex-none" role="progressbar" aria-valuenow={percent ?? undefined} aria-valuemin="0" aria-valuemax="100">
        {#if percent !== null}
          <span class="progress-fill block" style="width: {percent}%"></span>
        {:else}
          <span class="progress-indeterminate block"></span>
        {/if}
      </span>

      <span class="flex items-center gap-3 font-mono text-[10.5px] text-ink-3">
        {#if percent !== null}<span class="font-semibold text-ink">{percent}%</span>{/if}
        {#if speed != null}<span>{speed.toFixed(1)}×</span>{/if}
        {#if queued > 0}<span class="ml-auto">{t(i18n.m.app.queued_count, { count: queued.toLocaleString() })}</span>{/if}
      </span>
    </a>
  {/if}
{/if}
