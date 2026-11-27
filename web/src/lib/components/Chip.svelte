<script lang="ts">
  // A fact about a thing — its encoder, a language, where it runs — drawn as a filled capsule
  // with no outline. Monospace is for machine strings only (encoder and codec IDs, language
  // codes); quantities and words stay in the reading face.
  import type { Snippet } from 'svelte'

  let { tone = 'neutral', mono = false, truncate = false, title, class: className = '', children }: {
    tone?: 'neutral' | 'muted' | 'ok' | 'warn' | 'bad' | 'accent' | 'info'
    mono?: boolean
    /** Cut a long value to the chip's width with an ellipsis; pair with a max width and `title`. */
    truncate?: boolean
    title?: string
    class?: string
    children: Snippet
  } = $props()
</script>

<span class="chip tone-{tone} {className}" class:chip-mono={mono} {title}>{#if truncate}<span class="min-w-0 truncate">{@render children()}</span>{:else}{@render children()}{/if}</span>
