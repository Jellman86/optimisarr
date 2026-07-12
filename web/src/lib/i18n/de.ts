import type { Messages } from './en'

// German. Typed as `Messages`, so it must define exactly the English key set —
// omitting or misnaming a key fails `npm run check`.
export const de: Messages = {
  common: {
    loading: 'Optimisarr wird geladen …',
    checking: 'Wird geprüft …',
    continue: 'Weiter',
    cancel: 'Abbrechen',
    save: 'Speichern',
    close: 'Schließen',
  },
  auth: {
    token_required: 'Admin-Token erforderlich',
    token_label: 'Admin-Token',
  },
  nav: {
    dashboard: 'Dashboard',
    libraries: 'Bibliotheken',
    inventory: 'Bestand',
    queue: 'Warteschlange',
    quarantine: 'Quarantäne',
    schedule: 'Zeitplan',
    settings: 'Einstellungen',
    open_menu: 'Menü öffnen',
    close_menu: 'Menü schließen',
    toggle_theme: 'Design umschalten',
    collapse_sidebar: 'Seitenleiste einklappen',
    coming_soon: '{label} — folgt bald',
    soon: 'bald',
  },
  app: {
    tagline: 'Sicherer Bibliotheks-Optimierer',
    encoding_on_gpu: 'Kodierung auf GPU',
    encoding_on_cpu: 'Kodierung auf CPU',
    version_build: 'Optimisarr {version} · Build {hash}',
    build: 'Build {hash}',
  },
  language: {
    label: 'Sprache',
  },
}
