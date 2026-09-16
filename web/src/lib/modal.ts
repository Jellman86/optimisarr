// Native dialogs own focus containment and make the rest of the application inert.
export function modal(node: HTMLDialogElement, dismiss: () => void) {
  const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
  node.showModal()
  function cancel(event: Event) {
    event.preventDefault()
    dismiss()
  }
  function backdrop(event: MouseEvent) {
    if (event.target !== node) return
    const box = node.getBoundingClientRect()
    if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) dismiss()
  }
  node.addEventListener('cancel', cancel)
  node.addEventListener('click', backdrop)
  return {
    update(next: () => void) { dismiss = next },
    destroy() {
      node.removeEventListener('cancel', cancel)
      node.removeEventListener('click', backdrop)
      node.close()
      if (opener?.isConnected) opener.focus({ preventScroll: true })
    },
  }
}
