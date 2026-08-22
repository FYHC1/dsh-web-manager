// dsh-web-manager tray install (explicit, not a pnpm lifecycle hook): on Windows
// install + start the tray companion (dist/Install.ps1); on WSL/Linux the bridge
// bundle is already loaded by dsh web and the tray manager materializes its WSL
// helper scripts on demand.
//
//   node node_modules/dsh-web-manager/scripts/install-tray.mjs     (from the profile)
//   node scripts/install-tray.mjs                                   (from a checkout)
//
// Never fails the caller on tray setup errors — the bridge is the part that must
// be installed; the tray is a convenience (re-runnable via dist/Install.ps1).
import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(dirname(fileURLToPath(import.meta.url)))
const installer = join(root, 'dist', 'Install.ps1')

if (process.platform !== 'win32') {
  process.exit(0)
}

if (!existsSync(installer)) {
  console.warn('[dsh-web-manager] dist/Install.ps1 not found — skipping tray install (run scripts/Build.ps1 first)')
  process.exit(0)
}

try {
  execFileSync('powershell.exe', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', installer,
  ], { stdio: 'inherit', shell: false })
} catch (err) {
  console.warn('[dsh-web-manager] tray install failed:', err && err.message)
  console.warn(`[dsh-web-manager] install manually: powershell -ExecutionPolicy Bypass -File "${installer}"`)
  process.exit(0)
}
