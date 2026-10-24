// The shared rules behind every multi-select list. The functions are pure so the Queue table,
// the candidate table, and their tests all apply exactly the same rules.
//
// A selection is a list of row ids in ascending order. The order is not the click order: it is
// the only order two runs can be compared in, and the batch actions do not care which row was
// touched first.

/**
 * The most rows one batch action may carry. It matches the cap the API puts on a batch request,
 * so a selection the interface accepts is a request the server accepts.
 */
export const MAX_SELECTION = 200

export function isSelected(selected: readonly number[], id: number): boolean {
  return selected.includes(id)
}

export function toggleSelection(selected: readonly number[], id: number): number[] {
  return isSelected(selected, id)
    ? selected.filter((value) => value !== id)
    : addToSelection(selected, [id])
}

export function addToSelection(selected: readonly number[], ids: readonly number[]): number[] {
  const merged = new Set(selected)
  for (const id of ids) merged.add(id)
  return [...merged].sort((a, b) => a - b)
}

export function removeFromSelection(selected: readonly number[], ids: readonly number[]): number[] {
  const dropped = new Set(ids)
  return selected.filter((id) => !dropped.has(id))
}

export function clearSelection(): number[] {
  return []
}

/**
 * The rows between the anchor and the target in the order they are shown. Shift-clicking extends
 * from the last row the operator touched, so a range is chosen over the visible order rather than
 * over ids.
 */
function rangeInOrder(order: readonly number[], anchor: number, target: number): number[] {
  const from = order.indexOf(anchor)
  const to = order.indexOf(target)
  if (from < 0 || to < 0) return []
  const start = Math.min(from, to)
  const end = Math.max(from, to)
  return order.slice(start, end + 1)
}

/**
 * The rows between the anchor and the target, added to the selection. The range is taken in the
 * order the rows are shown, so the rows that reach the cap are the ones the operator picked last.
 */
export function rangeSelection(
  order: readonly number[],
  selected: readonly number[],
  anchor: number,
  target: number,
): number[] {
  return capMerge(selected, rangeInOrder(order, anchor, target))
}

/** True when every shown row is selected — the header checkbox's checked state. */
export function isEverySelected(order: readonly number[], selected: readonly number[]): boolean {
  return order.length > 0 && order.every((id) => isSelected(selected, id))
}

/** True when at least one shown row is selected — the header checkbox's indeterminate state. */
export function isSomeSelected(order: readonly number[], selected: readonly number[]): boolean {
  return order.some((id) => isSelected(selected, id))
}

/**
 * Adds rows to a selection without going past the cap. The rows the operator just picked come
 * first, so a selection that reaches the cap still acts on what was picked last; the rows that
 * were already selected follow, up to the cap.
 */
export function capMerge(selected: readonly number[], incoming: readonly number[]): number[] {
  const merged = new Set(selected)
  for (const id of incoming) merged.add(id)
  if (merged.size <= MAX_SELECTION) return [...merged].sort((a, b) => a - b)
  const kept: number[] = []
  for (const id of incoming) {
    if (kept.length >= MAX_SELECTION) break
    if (!kept.includes(id)) kept.push(id)
  }
  for (const id of selected) {
    if (kept.length >= MAX_SELECTION) break
    if (!kept.includes(id)) kept.push(id)
  }
  return kept.sort((a, b) => a - b)
}
