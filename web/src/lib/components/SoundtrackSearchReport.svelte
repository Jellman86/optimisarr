<script lang="ts">
  import type { SoundtrackSearchSummary } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import InfoTip from './InfoTip.svelte'
  let { report, active = false }: { report: SoundtrackSearchSummary | null; active?: boolean } = $props()
</script>

{#if report}
  <section class="mt-5 min-w-0 rounded-lg border border-line bg-panel p-4" aria-label={i18n.m.soundtrack_quality.search_title}>
    <div class="flex min-w-0 flex-wrap items-center justify-between gap-2">
      <div class="flex min-w-0 items-center gap-2">
        <h3 class="text-sm font-semibold text-ink-2">{i18n.m.soundtrack_quality.search_title}</h3>
        <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.soundtrack_quality.search_title })}
          text={i18n.m.soundtrack_quality.search_hint} />
      </div>
      {#if active}<span class="badge tone-muted">{i18n.m.soundtrack_quality.search_running}</span>{/if}
    </div>
    {#if report.unavailable}
      <p class="mt-3 break-words text-sm text-ink-3">{t(i18n.m.soundtrack_quality.search_library, { bitrate: report.baselineKbps })}</p>
      <p class="mt-1 break-words text-xs text-ink-3">{report.unavailable}</p>
    {/if}
    {#each report.tracks ?? [] as item}
      {@const decision = item.result.decision}
      <div class="mt-3 flex min-w-0 flex-wrap items-baseline justify-between gap-x-3 gap-y-1 border-t border-line-soft pt-3">
        <h4 class="min-w-0 break-words text-sm font-semibold text-ink-2">
          {t(i18n.m.soundtrack_quality.track, { track: item.track.outputIndex + 1, language: item.track.language ?? i18n.m.common.unknown })}
          {#if item.track.title}<span class="font-normal text-ink-3"> · {item.track.title}</span>{/if}
        </h4>
        <span class="font-mono text-sm tabular-nums text-ink">{t(i18n.m.soundtrack_quality.search_rate, { bitrate: decision.selectedValue })}</span>
        <p class="w-full break-words text-xs text-ink-3">
          {decision.fellBack ? t(i18n.m.soundtrack_quality.search_fallback, { bitrate: report.baselineKbps }) : t(i18n.m.soundtrack_quality.search_chosen, { count: item.result.probes.length })}
          · {decision.reason}
        </p>
      </div>
    {/each}
  </section>
{/if}
