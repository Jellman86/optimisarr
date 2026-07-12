// English is the source of truth for every user-facing string. Its shape defines the
// `Messages` type in `i18n.svelte.ts`, so every other locale must provide exactly these
// keys — a missing or misspelled key fails `npm run check` (the CI completeness gate).
// Use `{token}` placeholders for interpolation; resolve them with `t(...)`.
export const en = {
  common: {
    loading: 'Loading Optimisarr...',
    checking: 'Checking...',
    continue: 'Continue',
    cancel: 'Cancel',
    save: 'Save',
    close: 'Close',
  },
  auth: {
    token_required: 'Admin token required',
    token_label: 'Admin token',
  },
  nav: {
    dashboard: 'Dashboard',
    libraries: 'Libraries',
    inventory: 'Inventory',
    queue: 'Queue',
    quarantine: 'Quarantine',
    schedule: 'Schedule',
    settings: 'Settings',
    open_menu: 'Open menu',
    close_menu: 'Close menu',
    toggle_theme: 'Toggle theme',
    collapse_sidebar: 'Collapse sidebar',
    coming_soon: '{label} — coming soon',
    soon: 'soon',
  },
  app: {
    tagline: 'Safe library optimiser',
    encoding_on_gpu: 'Encoding on GPU',
    encoding_on_cpu: 'Encoding on CPU',
    version_build: 'Optimisarr {version} · build {hash}',
    build: 'build {hash}',
  },
  language: {
    label: 'Language',
  },
}

// Every locale must match this exact shape (string leaves), so a locale cannot omit,
// add, or misname a key without failing `npm run check`.
export type Messages = typeof en
