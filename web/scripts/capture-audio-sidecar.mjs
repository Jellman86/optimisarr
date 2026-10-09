// Keep the original audio-only documentation command available.
import { captureLinuxSidecar } from './capture-linux-sidecar.mjs'
await captureLinuxSidecar(process.argv[2], ['audio'])
