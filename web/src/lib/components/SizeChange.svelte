<script lang="ts">
  // The size change of one optimised file, shared by the Recent results table, the idle
  // in-flight panel, the Queue job detail and the Quarantine review list so that a saving
  // reads the same way — and in the same colour — wherever it is shown.
  import { formatSizeChangePercent, sizeChangePercent, sizeChangeStatus } from '../format'

  let {
    sourceSizeBytes,
    outputSizeBytes,
    label = null,
    class: className = '',
  }: {
    sourceSizeBytes: number
    outputSizeBytes: number
    /** A localised sentence in plain words, read beside the figure. */
    label?: string | null
    class?: string
  } = $props()

  const percent = $derived(sizeChangePercent(sourceSizeBytes, outputSizeBytes))
  const status = $derived(sizeChangeStatus(percent))
  // A saving is the app's success tone, a file that grew is a warning, and a file that did not
  // change — or a change that cannot be judged — is a plain fact in neutral ink.
  const tone = $derived(status === 'saved' ? 'text-ok' : status === 'grew' ? 'text-warn' : 'text-ink-2')
  const classes = $derived(className ? `${tone} ${className}` : tone)
</script>

<span class={classes}>{formatSizeChangePercent(percent)}</span>{#if label}<span class="ml-1 text-sm font-normal text-ink-3">({label})</span>{/if}
