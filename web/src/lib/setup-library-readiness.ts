type LibraryIdentity = { id: number }
type AccessResult = { ok: boolean }
type AccessByLibrary = Readonly<Record<number, AccessResult | null | undefined>>

export function libraryPathsReady(
  libraries: readonly LibraryIdentity[],
  access: AccessByLibrary,
): boolean {
  return libraries.length > 0 && libraries.every((library) => access[library.id]?.ok === true)
}

export function firstUnavailableLibrary<T extends LibraryIdentity>(
  libraries: readonly T[],
  access: AccessByLibrary,
): T | undefined {
  return libraries.find((library) => access[library.id]?.ok !== true)
}
