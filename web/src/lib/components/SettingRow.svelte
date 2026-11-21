<script lang="ts" module>
  /** A warning tag is never collapsed: it says the setting, as it is set now, changes or removes something. */
  export type SettingTag = { text: string; tone?: 'warn' | 'neutral' | 'accent' }

  /** Where a value came from: its preset, or a change the operator made, which can be undone. */
  export type SettingOrigin = { text: string; changed?: boolean; onreset?: () => void }
</script>

<script lang="ts">
  // One setting, three depths: a sentence that is always there, tags when the current value
  // changes or removes something, and a longer explanation that opens in the page and stays open
  // while the control is used. A control that cannot apply keeps its place and says why.
  import type { Snippet } from 'svelte'
  import { i18n } from '../i18n/i18n.svelte'
  import Icon from './Icon.svelte'

  let {
    label,
    sentence,
    controlId = undefined,
    tags = [],
    more = '',
    origin = null,
    disabledReason = null,
    reasonLink = null,
    stacked = false,
    children,
  }: {
    label: string
    /** What the setting does, in one plain sentence. Required: a setting without one is not done. */
    sentence: string
    /** The id of the one control this row labels. Omit for a group that labels its own parts. */
    controlId?: string
    tags?: SettingTag[]
    /** The longer explanation, shown only when asked for. */
    more?: string
    origin?: SettingOrigin | null
    /** Why the control cannot be used right now. Set it together with the control's own disabled state. */
    disabledReason?: string | null
    /** Where to change the thing that disables this control. */
    reasonLink?: { label: string; onclick: () => void } | null
    /** Put the control under the text at every width, for controls that need the full row. */
    stacked?: boolean
    children: Snippet
  } = $props()

  const id = $props.id()
</script>

<div class="setting-row" data-setting-row={label} data-setting-disabled={disabledReason ? '' : undefined}>
  <div class="setting-grid {stacked ? 'setting-grid-stacked' : ''}">
  <div class="setting-text">
    {#if controlId}
      <label class="setting-label" for={controlId}>{label}</label>
    {:else}
      <span class="setting-label" id={`${id}-label`}>{label}</span>
    {/if}
    <p class="mt-1 text-sm leading-relaxed text-ink-3" id={`${id}-sentence`}>{sentence}</p>
    {#if tags.length > 0}
      <div class="mt-2 flex flex-wrap gap-1.5">
        {#each tags as tag (tag.text)}
          <span class="badge {tag.tone === 'neutral' ? 'tone-neutral' : tag.tone === 'accent' ? 'tone-accent' : 'tone-warn'}" data-setting-tag>{tag.text}</span>
        {/each}
      </div>
    {/if}
    {#if origin}
      <p class="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-ink-3" data-setting-origin={origin.changed ? 'changed' : 'preset'}>
        {#if origin.changed}<span class="badge tone-accent">{i18n.m.settingRow.changed_by_you}</span>{/if}
        <span>{origin.text}</span>
        {#if origin.changed && origin.onreset}
          <button type="button" class="focus-ring min-h-11 rounded px-1 font-medium text-accent underline" onclick={origin.onreset}>{i18n.m.settingRow.reset}</button>
        {/if}
      </p>
    {/if}
    {#if more}
      <details class="setting-more mt-1">
        <summary class="focus-ring inline-flex min-h-11 cursor-pointer items-center gap-1.5 rounded text-xs font-medium text-accent">
          <Icon name="info" class="h-3.5 w-3.5" />
          <span class="when-closed">{i18n.m.settingRow.more}</span>
          <span class="when-open">{i18n.m.settingRow.less}</span>
        </summary>
        <p class="max-w-prose text-xs leading-relaxed text-ink-2">{more}</p>
      </details>
    {/if}
  </div>
  <div class="setting-control">
    {@render children()}
    {#if disabledReason}
      <p class="mt-2 text-xs leading-relaxed text-ink-3" data-setting-reason>
        {disabledReason}
        {#if reasonLink}
          <button type="button" class="focus-ring min-h-11 rounded px-1 font-medium text-accent underline" onclick={reasonLink.onclick}>{reasonLink.label}</button>
        {/if}
      </p>
    {/if}
  </div>
  </div>
</div>

<style>
  .setting-row {
    padding-block: 1rem;
    border-top: 1px solid var(--divide-soft);
  }
  .setting-row:first-child {
    border-top: 0;
    padding-top: 0;
  }
  .setting-row:last-child {
    padding-bottom: 0;
  }
  /* Text and control sit side by side only when the row itself has room for both, not when the
     window does: beside a sidebar a wide screen can still leave a narrow page, and the text must
     never be squeezed into a sliver beside its control. The control keeps a readable width. */
  .setting-grid {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-start;
    gap: 0.75rem 2rem;
  }
  .setting-text {
    flex: 999 1 20rem;
    min-width: 0;
  }
  .setting-control {
    flex: 1 1 18rem;
    max-width: 24rem;
    min-width: 0;
  }
  .setting-grid-stacked {
    flex-direction: column;
    flex-wrap: nowrap;
    align-items: stretch;
  }
  .setting-grid-stacked > .setting-text,
  .setting-grid-stacked > .setting-control {
    flex: none;
    max-width: none;
  }
  .setting-label {
    display: block;
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--ink);
  }
  .setting-more .when-open,
  .setting-more[open] .when-closed {
    display: none;
  }
  .setting-more[open] .when-open {
    display: inline;
  }
  .setting-more > summary {
    list-style: none;
  }
  .setting-more > summary::-webkit-details-marker {
    display: none;
  }
</style>
