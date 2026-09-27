import { defineConfig } from 'vite'
import shared from './vite.config'

export default defineConfig({
  ...shared,
  build: {
    outDir: '../sidecars/linux/src/Optimisarr.Sidecar.Linux/wwwroot',
    emptyOutDir: true,
    rollupOptions: { input: 'sidecar.html' },
  },
})
