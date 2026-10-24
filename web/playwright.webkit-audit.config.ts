import { defineConfig, devices } from '@playwright/test'
import base from './playwright.config'

// Optional Safari-engine pass on macOS. The main suite stays on Chromium in CI.
export default defineConfig({
  ...base,
  testMatch: ['layout-audit.spec.ts', 'exact-duplicates.spec.ts', 'durable-exact-duplicates.spec.ts', 'cross-library-copies.spec.ts'],
  projects: [{ name: 'webkit', use: { ...devices['Desktop Safari'] } }],
})
