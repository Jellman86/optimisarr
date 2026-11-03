import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  addToSelection,
  capMerge,
  clearSelection,
  isEverySelected,
  isSelected,
  isSomeSelected,
  MAX_SELECTION,
  rangeSelection,
  removeFromSelection,
  toggleSelection,
} from './selection.ts'

test('a row is selected, then unselected, by the same toggle', () => {
  const selected = toggleSelection([], 3)
  assert.deepEqual(selected, [3])
  assert.ok(isSelected(selected, 3))
  assert.deepEqual(toggleSelection(selected, 3), [])
})

test('a selection is kept in ascending order so two runs compare', () => {
  assert.deepEqual(addToSelection([5], [1, 9, 3]), [1, 3, 5, 9])
  assert.deepEqual(addToSelection([1, 3], [3, 1]), [1, 3])
})

test('removing rows leaves the rest of the selection alone', () => {
  assert.deepEqual(removeFromSelection([1, 2, 3, 4], [2, 4]), [1, 3])
  assert.deepEqual(removeFromSelection([1, 2], [9]), [1, 2])
  assert.deepEqual(clearSelection(), [])
})

test('a range runs over the shown order, not over the id values', () => {
  const order = [30, 10, 20]
  assert.deepEqual(rangeSelection(order, [], 30, 20), [10, 20, 30])
  assert.deepEqual(rangeSelection(order, [], 20, 30), [10, 20, 30])
})

test('a range keeps what was already selected', () => {
  const order = [1, 2, 3, 4, 5]
  assert.deepEqual(rangeSelection(order, [1], 3, 4), [1, 3, 4])
})

test('a range from a row that is no longer shown changes nothing', () => {
  assert.deepEqual(rangeSelection([1, 2, 3], [2], 99, 3), [2])
})

test('the header checkbox knows when all and when some rows are selected', () => {
  assert.equal(isEverySelected([1, 2], [1, 2]), true)
  assert.equal(isEverySelected([1, 2], [1]), false)
  assert.equal(isSomeSelected([1, 2], [1]), true)
  assert.equal(isSomeSelected([1, 2], []), false)
  assert.equal(isEverySelected([], []), false)
})

test('an empty list is never reported as fully selected', () => {
  assert.equal(isEverySelected([], []), false)
  assert.equal(isSomeSelected([], []), false)
})

test('a selection is capped rather than silently truncated', () => {
  const many = Array.from({ length: MAX_SELECTION + 25 }, (_, index) => index + 1)
  const capped = capMerge([], many)
  assert.equal(capped.length, MAX_SELECTION)
  assert.deepEqual(capped, many.slice(0, MAX_SELECTION))
})

test('a range that reaches the cap keeps the rows nearest the anchor', () => {
  // The list is shown newest first, so the lowest ids are the last rows of the range.
  const order = Array.from({ length: MAX_SELECTION + 10 }, (_, index) => MAX_SELECTION + 10 - index)
  const range = order.slice(0, MAX_SELECTION + 10)
  const capped = rangeSelection(order, [], order[0], order[order.length - 1])
  assert.equal(capped.length, MAX_SELECTION)
  assert.deepEqual(capped, range.slice(0, MAX_SELECTION).sort((a, b) => a - b))
})

test('a merge that stays under the cap keeps everything', () => {
  assert.deepEqual(capMerge([1, 2], [2, 3]), [1, 2, 3])
})

test('a range at the cap never silently drops an earlier selection', () => {
  const selected = Array.from({ length: MAX_SELECTION }, (_, index) => index + 1)
  assert.deepEqual(capMerge(selected, [999]), selected)
})
