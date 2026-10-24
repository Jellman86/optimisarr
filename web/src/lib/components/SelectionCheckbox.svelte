<script lang="ts">
  // The checkbox of a selectable row, and of the header that selects the whole list. Shift-click
  // extends the selection from the last row the operator touched, and the header says so when only
  // part of the list is selected — an operator must be able to see that their choice is narrower
  // than the list they are looking at.
  let {
    checked,
    indeterminate = false,
    label,
    onchange,
  }: {
    checked: boolean
    indeterminate?: boolean
    label: string
    onchange: (extend: boolean) => void
  } = $props()

  let box: HTMLInputElement | undefined = $state()
  $effect(() => {
    if (box) box.indeterminate = indeterminate
  })
</script>

<input
  type="checkbox"
  class="checkbox"
  bind:this={box}
  {checked}
  aria-label={label}
  onclick={(event) => onchange(event.shiftKey)}
/>
