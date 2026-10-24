import {
  capMerge,
  clearSelection,
  isEverySelected,
  isSelected,
  isSomeSelected,
  MAX_SELECTION,
  rangeSelection,
  removeFromSelection,
  toggleSelection,
} from './selection'

/**
 * The multi-select state shared by the Queue table and the candidate table.
 *
 * The state is a set of row ids; the rules that change it live in ./selection so they can be
 * tested without a component. A selection survives a change of filter and a change of page, so
 * an operator can select rows, look at another page, and come back.
 */
export function createSelection() {
  let selected = $state<number[]>([])
  // The last row the operator touched, so shift-clicking extends the range from it.
  let anchor = $state<number | null>(null)

  function toggle(id: number, order: readonly number[], extend = false) {
    if (extend && anchor !== null) {
      // The range is taken in the order the rows are shown, so the rows that reach the cap are the
      // ones the operator picked last, not the ones with the lowest ids.
      selected = rangeSelection(order, selected, anchor, id)
      return
    }
    // At the cap a further row is refused rather than dropped from the batch unnoticed.
    if (!isSelected(selected, id) && selected.length >= MAX_SELECTION) return
    selected = toggleSelection(selected, id)
    anchor = id
  }

  return {
    /** The selected row ids, ascending. */
    get ids() {
      return selected
    },
    get count() {
      return selected.length
    },
    get isEmpty() {
      return selected.length === 0
    },
    /** True once one action cannot carry any more rows. */
    get isFull() {
      return selected.length >= MAX_SELECTION
    },
    isSelected(id: number) {
      return isSelected(selected, id)
    },
    /** True when every shown row is selected. */
    isEverySelected(order: readonly number[]) {
      return isEverySelected(order, selected)
    },
    /** True when at least one shown row is selected. */
    isSomeSelected(order: readonly number[]) {
      return isSomeSelected(order, selected)
    },
    toggle,
    /** Select every shown row, up to the cap. */
    selectAll(order: readonly number[]) {
      selected = capMerge([], order)
      anchor = null
    },
    /** Forget the given rows, e.g. the ones a batch action just handled. */
    remove(ids: readonly number[]) {
      selected = removeFromSelection(selected, ids)
    },
    clear() {
      selected = clearSelection()
      anchor = null
    },
  }
}

export type Selection = ReturnType<typeof createSelection>
