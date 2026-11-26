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

<!-- The label is the checkbox's touch target on a phone: a thumb-sized square around the box, which
     itself stays the size of a checkbox. Negative margins let the square overhang its neighbours
     instead of moving them, so a row lays out as before. Elsewhere it is as if it were not there. -->
<label class="contents max-sm:flex max-sm:min-h-(--control-lg) max-sm:min-w-(--control-lg) max-sm:m-[calc((1rem_-_var(--control-lg))/2)] max-sm:cursor-pointer max-sm:items-center max-sm:justify-center">
  <input
    type="checkbox"
    class="checkbox"
    bind:this={box}
    {checked}
    aria-label={label}
    onclick={(event) => onchange(event.shiftKey)}
  />
</label>
