<script lang="ts">
  import type { SoundtrackSearchSummary } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import MeasureReport from './MeasureReport.svelte'
  let { report, active = false }: { report: SoundtrackSearchSummary | null; active?: boolean } = $props()
</script>

{#if report}
  <MeasureReport title={i18n.m.soundtrack_quality.search_title}
    hintLabel={t(i18n.m.common.about_information, { label: i18n.m.soundtrack_quality.search_title })}
    hint={i18n.m.soundtrack_quality.search_hint}
    badge={active ? { text: i18n.m.soundtrack_quality.search_running, tone: 'muted' } : null}>
    {#if report.unavailable}
      <p class="mt-3 break-words text-body text-ink-3">{t(i18n.m.soundtrack_quality.search_library, { bitrate: report.baselineKbps })}</p>
      <p class="mt-1 break-words text-caption text-ink-3">{report.unavailable}</p>
    {/if}
    {#each report.tracks ?? [] as item}
      {@const decision = item.result.decision}
      <div class="hairline-t mt-3 flex min-w-0 flex-wrap items-baseline justify-between gap-x-3 gap-y-1 pt-3">
        <h4 class="min-w-0 break-words text-body font-semibold text-ink">
          {t(i18n.m.soundtrack_quality.track, { track: item.track.outputIndex + 1, language: item.track.language ?? i18n.m.common.unknown })}
          {#if item.track.title}<span class="font-normal text-ink-3"> · {item.track.title}</span>{/if}
        </h4>
        <span class="text-body font-semibold tabular-nums text-ink">{t(i18n.m.soundtrack_quality.search_rate, { bitrate: decision.selectedValue })}</span>
        <p class="w-full break-words text-caption text-ink-3">
          {decision.fellBack ? t(i18n.m.soundtrack_quality.search_fallback, { bitrate: report.baselineKbps }) : t(i18n.m.soundtrack_quality.search_chosen, { count: item.result.probes.length })}
          · {decision.reason}
        </p>
      </div>
    {/each}
  </MeasureReport>
{/if}
