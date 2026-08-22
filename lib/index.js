// dsh-runtime-bridge: authoritative status + graceful shutdown for dsh web,
// plus idempotent desktop-shortcut creation that launches the shared tray manager
// (dsh-web-manager.exe) with "open windows" / "open wsl".
//
// Listens on 127.0.0.1:<DSH_BRIDGE_PORT> (inside WSL; reachable from Windows
// through localhost forwarding) and speaks a versioned line-delimited JSON
// protocol. The manager (dsh web manager) queries it instead of guessing from
// ports alone, and asks it to shut dsh down gracefully before any kill fallback.
//
// Protocol (one JSON request per line, one JSON response per line):
//   request : { v: 1, method: 'ping'|'getStatus'|'getRuntimeInfo'|'shutdown',
//               token: '<DSH_BRIDGE_TOKEN>' }
//   response: { v: 1, ok: true, ... }  |  { v: 1, error: '<message>' }
//
// Environment (set by the launcher before exec dsh):
//   DSH_BRIDGE_PORT  - listen port (0/absent disables the bridge)
//   DSH_BRIDGE_TOKEN - shared secret checked on every request
//   DSH_PROFILE      - profile name (reported in getStatus)
//   DSH_WEB_PORT     - the web port this dsh serves (reported in getStatus)

import net from 'node:net'
import os from 'node:os'
import { execSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-runtime-bridge'
export const inject = []

const __dirname = dirname(fileURLToPath(import.meta.url))
const PKG_DIR = resolve(__dirname, '..')

const V = 1

// Windows PowerShell reachable from inside WSL (not on the WSL PATH, so absolute).
const WSL_POWERSHELL = '/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'

function currentPlatform() {
  if (process.platform === 'win32') return 'win'
  if (existsSync(WSL_POWERSHELL)) return 'wsl'
  return 'linux'
}

// Idempotent: ensure the shared tray manager is installed and the platform
// desktop shortcut exists. Runs on every dsh boot but is a no-op when the
// shortcut is already present (both scripts exit 0 without changes).
function ensureShortcut() {
  const platform = currentPlatform()
  try {
    if (platform === 'win') {
      const script = join(PKG_DIR, 'scripts', 'ensure-shortcut.ps1')
      execSync(`powershell -NoProfile -ExecutionPolicy Bypass -File "${script}" -Backend windows`, {
        encoding: 'utf8', timeout: 120000, stdio: 'pipe',
      })
      console.log('[dsh-web-manager] Windows shortcut ensured')
    } else if (platform === 'wsl') {
      const script = join(PKG_DIR, 'scripts', 'ensure-shortcut.sh')
      execSync(`bash "${script}"`, { encoding: 'utf8', timeout: 120000, stdio: 'pipe' })
      console.log('[dsh-web-manager] WSL shortcut ensured')
    }
    // linux: no Windows desktop to create a shortcut on.
  } catch (e) {
    // Never break dsh web over a shortcut; the bridge is the must-have part.
    console.warn('[dsh-web-manager] shortcut install failed:', (e && e.message) || e)
  }
}

function reply(socket, obj) {
  try {
    socket.write(JSON.stringify(obj) + '\n')
  } catch (_) {
    /* socket gone */
  }
  // One request, one response: close so the client's read completes.
  try {
    socket.end()
  } catch (_) {
    /* socket gone */
  }
}

function getDshVersion() {
  try {
    const out = execSync('dsh --version', { timeout: 3000, stdio: ['ignore', 'pipe', 'ignore'] })
      .toString()
      .trim()
      .split('\n')[0]
    return out || ''
  } catch {
    return ''
  }
}

export function apply(ctx) {
  // Shortcut + shared tray manager install (independent of the bridge; idempotent).
  ensureShortcut()

  const port = Number(process.env.DSH_BRIDGE_PORT || 0)
  const token = process.env.DSH_BRIDGE_TOKEN || ''
  if (!port) {
    console.warn('[dsh-runtime-bridge] DSH_BRIDGE_PORT not set; bridge disabled')
    return
  }

  const startedAt = Date.now()
  let shuttingDown = false

  const server = net.createServer((socket) => {
    let buf = ''
    socket.on('data', (chunk) => {
      buf += chunk.toString()
      let idx
      while ((idx = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, idx)
        buf = buf.slice(idx + 1)
        let req
        try {
          req = JSON.parse(line)
        } catch (e) {
          reply(socket, { v: V, error: 'bad json: ' + e.message })
          continue
        }
        handle(req, socket)
      }
    })
    socket.on('error', () => {})
    socket.setNoDelay(true)
  })

  function handle(req, socket) {
    try {
      if (!req || req.v !== V) return reply(socket, { v: V, error: 'unsupported version' })
      if (req.token !== token) return reply(socket, { v: V, error: 'bad token' })
      switch (req.method) {
        case 'ping':
          return reply(socket, { v: V, ok: true, pong: true, ts: Date.now() })
        case 'getStatus':
          return reply(socket, {
            v: V,
            ok: true,
            status: {
              running: true,
              shuttingDown,
              pid: process.pid,
              startedAt,
              uptimeMs: Date.now() - startedAt,
              profile: process.env.DSH_PROFILE || '',
              webPort: Number(process.env.DSH_WEB_PORT || 0),
              host: process.env.DSH_WEB_HOST || '',
            },
          })
        case 'getRuntimeInfo':
          return reply(socket, {
            v: V,
            ok: true,
            info: {
              node: process.version,
              platform: process.platform,
              arch: process.arch,
              dshVersion: getDshVersion(),
              hostname: os.hostname(),
              cwd: process.cwd(),
              profileDir: process.env.DSH_PROFILE_DIR || '',
            },
          })
        case 'shutdown': {
          if (shuttingDown) return reply(socket, { v: V, ok: true, shuttingDown: true })
          shuttingDown = true
          reply(socket, { v: V, ok: true, shuttingDown: true })
          // Give the response a moment to flush, then terminate gracefully.
          setTimeout(() => {
            try {
              process.kill(process.pid, 'SIGTERM')
            } catch (_) {
              process.exit(0)
            }
          }, 200)
          return
        }
        default:
          return reply(socket, { v: V, error: 'unknown method: ' + req.method })
      }
    } catch (e) {
      reply(socket, { v: V, error: e.message })
    }
  }

  server.listen(port, '127.0.0.1', () => {
    console.log(`[dsh-runtime-bridge] listening on 127.0.0.1:${port} (profile=${process.env.DSH_PROFILE || '?'})`)
  })
  server.on('error', (e) => {
    console.error('[dsh-runtime-bridge] listen error:', e.message)
  })

  ctx.on('dispose', () => {
    try {
      server.close()
    } catch (_) {
      /* already closed */
    }
  })
}
