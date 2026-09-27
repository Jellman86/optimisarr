<script lang="ts">
  import { activity } from '../stores/activity.svelte'
  import { brand } from '../stores/brand.svelte'
  import { theme } from '../stores/ui.svelte'
  import BrandCanvas from './BrandCanvas.svelte'

  let { class: className = 'h-9 w-9' }: { class?: string } = $props()
</script>

<BrandCanvas style={brand.style} working={activity.brandWorking} dark={theme.isDark} class={className} />
