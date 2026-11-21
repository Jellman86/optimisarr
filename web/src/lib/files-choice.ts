// "What happens to my files" is one choice of three, stored in two columns the server already
// has: `autoReplace`, and `moveOnComplete` with its folder. The dispatcher lets the folder win
// when both are on, so that is how a library saved with both is read here.
export type FilesChoice = 'ask' | 'auto' | 'elsewhere'

type FilesFlags = { autoReplace: boolean; moveOnComplete: boolean }

export function filesChoice(flags: FilesFlags): FilesChoice {
  if (flags.moveOnComplete) return 'elsewhere'
  return flags.autoReplace ? 'auto' : 'ask'
}

export function flagsFor(choice: FilesChoice): FilesFlags {
  return { autoReplace: choice === 'auto', moveOnComplete: choice === 'elsewhere' }
}

/** Both switches on: replace automatically was saved but never did anything. */
export function hasContradictoryFlags(flags: FilesFlags): boolean {
  return flags.autoReplace && flags.moveOnComplete
}

/** Where one file ends up under the chosen folder: the library's sub-folders are kept. */
export function savedElsewherePath(targetFolder: string, relativePath: string): string {
  return `${targetFolder.replace(/[\\/]+$/, '')}/${relativePath.replace(/\\/g, '/').replace(/^\/+/, '')}`
}
