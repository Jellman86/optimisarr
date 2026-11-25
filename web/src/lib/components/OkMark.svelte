<script lang="ts">
  // The glyph that says passed, verified, healthy or saved: a solid ok disc with the check
  // knocked out of it. The words beside it stay in ink, because blue text alone means "you can
  // press this". Decorative, so the sentence it sits beside must say the state in words.
  let { class: className = '' }: { class?: string } = $props()
</script>

<svg class="ok-mark {className}" viewBox="0 0 16 16" aria-hidden="true" data-ok-mark>
  <circle cx="8" cy="8" r="8" fill="currentColor" />
  <path d="M4.75 8.25 7 10.5l4.25-4.75" fill="none" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" />
</svg>
