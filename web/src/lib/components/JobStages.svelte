<script lang="ts">
  import type { Job, QueueStatus } from '../api'
  import { isJobStageActive, jobStep } from '../job-presentation'
  import { i18n } from '../i18n/i18n.svelte'
  import OkMark from './OkMark.svelte'
  // `class` replaces the default gap above the tracker, for a view that lays it out beside the progress.
  let { job, queue = null, class: className = 'mt-5' }: { job: Job; queue?: QueueStatus | null; class?: string } = $props()
  let step = $derived(jobStep(job))
  let active = $derived(isJobStageActive(job, queue))
  let labels = $derived([i18n.m.queue.step_probe, i18n.m.queue.step_encode, i18n.m.queue.step_verify, i18n.m.queue.step_replace])
</script>

{#if step !== null}
  <ol class="job-stages {className}" aria-label={i18n.m.queue.job_stages}>
    {#each labels as label, index}
      <li class:stage-done={index < step} class:stage-current={index === step} aria-current={index === step ? 'step' : undefined}>
        {#if index < step}<OkMark />{:else if index === step}<span class="stage-live" class:stage-live-still={!active} aria-hidden="true"></span>{:else}<span class="stage-pending" aria-hidden="true"></span>{/if}<span class="min-w-0">{label}{#if index < step}<span class="sr-only"> ({i18n.m.queue.step_done})</span>{/if}</span>
      </li>
    {/each}
  </ol>
{/if}
