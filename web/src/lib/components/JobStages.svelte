<script lang="ts">
  import type { Job } from '../api'
  import { jobStep } from '../job-presentation'
  import { i18n } from '../i18n/i18n.svelte'
  let { job }: { job: Job } = $props()
  let step = $derived(jobStep(job))
  let labels = $derived([i18n.m.queue.step_probe, i18n.m.queue.step_encode, i18n.m.queue.step_verify, i18n.m.queue.step_replace])
</script>

{#if step !== null}
  <ol class="job-stages" aria-label={i18n.m.queue.job_stages}>
    {#each labels as label, index}
      <li class:stage-done={index < step} class:stage-current={index === step} aria-current={index === step ? 'step' : undefined}>
        {#if index < step}<span aria-hidden="true">✓ </span>{/if}{label}
      </li>
    {/each}
  </ol>
{/if}

<style>
  .job-stages { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .5rem; margin-top: 1.25rem; }
  li { padding-top: .625rem; border-top: 2px solid var(--edge); font-size: .6875rem; color: var(--ink-3); overflow-wrap: anywhere; }
  .stage-done { color: var(--ok); border-color: var(--ok); }.stage-current { color: var(--accent); border-color: var(--accent); }
  .stage-done span { margin-right: .25rem; }
  @media(max-width: 420px) { .job-stages { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .75rem; } }
</style>
