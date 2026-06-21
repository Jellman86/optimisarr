<script lang="ts">
  // A compact filled sparkline for a 0–100% usage history. Width-responsive via a fixed
  // viewBox with non-scaling stroke so the line stays crisp at any size.
  type Props = {
    label: string
    data: number[]
    current: number | null
    color: string
    // When set, the metric is unavailable (e.g. GPU stats not exposable) and the graph is
    // replaced by this short message.
    unavailable?: string | null
    detail?: string | null
  }
  let { label, data, current, color, unavailable = null, detail = null }: Props = $props()

  const W = 120
  const H = 32

  let line = $derived.by(() => {
    if (data.length < 2) return ''
    const n = data.length
    return data
      .map((v, i) => {
        const x = (i / (n - 1)) * W
        const y = H - (Math.min(100, Math.max(0, v)) / 100) * H
        return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)} ${y.toFixed(1)}`
      })
      .join(' ')
  })
  let area = $derived(line ? `${line} L${W} ${H} L0 ${H} Z` : '')
</script>

<div class="rounded-lg border border-slate-200 p-3 dark:border-slate-700">
  <div class="flex items-baseline justify-between gap-2">
    <span class="text-xs font-medium uppercase tracking-wide text-slate-400">{label}</span>
    {#if !unavailable}
      <span class="text-sm font-semibold tabular-nums" style="color: {color}">
        {current != null ? Math.round(current) : '–'}%{#if detail}<span class="ml-1 text-xs font-normal text-slate-400">{detail}</span>{/if}
      </span>
    {/if}
  </div>
  {#if unavailable}
    <div class="mt-2 flex h-10 items-center justify-center text-center text-xs text-slate-400">{unavailable}</div>
  {:else}
    <svg viewBox="0 0 {W} {H}" preserveAspectRatio="none" class="mt-2 h-10 w-full overflow-visible">
      {#if area}<path d={area} fill={color} fill-opacity="0.12" />{/if}
      {#if line}<path d={line} fill="none" stroke={color} stroke-width="1.5" vector-effect="non-scaling-stroke" />{/if}
    </svg>
  {/if}
</div>
