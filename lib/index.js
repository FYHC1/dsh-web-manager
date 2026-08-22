// dsh-runtime-bridge: authoritative status + graceful shutdown for dsh web.
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

export const name = 'dsh-runtime-bridge'
export const inject = []

const V = 1

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
