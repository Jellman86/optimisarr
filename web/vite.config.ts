import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
import tailwindcss from '@tailwindcss/vite'
import { execSync } from 'node:child_process'
import { createRequire } from 'node:module'

const pkg = createRequire(import.meta.url)('./package.json') as { version: string }

// Short commit the build was produced from. The Docker web stage has no `.git`,
// so it passes GIT_HASH as a build arg; locally we read it from git directly.
function gitHash(): string {
  // CI passes the full 40-char SHA; normalise to a short hash either way.
  if (process.env.GIT_HASH) {
    return process.env.GIT_HASH.trim().slice(0, 7)
  }
  try {
    return execSync('git rev-parse --short HEAD').toString().trim()
  } catch {
    return 'unknown'
  }
}

const apiTarget = process.env.OPTIMISARR_API ?? 'http://localhost:8787'

const hash = gitHash()
const appVersion = `${pkg.version}+${hash}`

// https://vite.dev/config/
export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  define: {
    __GIT_HASH__: JSON.stringify(hash),
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  build: {
    outDir: '../src/Optimisarr.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      // Defaults to a server running alongside this checkout. Point it somewhere else to
      // develop the UI against a populated instance — an empty local database shows every
      // page in its empty state, which is not where the layout problems are:
      //   OPTIMISARR_API=https://optimisarr.example npm run dev
      '/api': { target: apiTarget, changeOrigin: true },
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
})
