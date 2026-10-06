import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import http from 'node:http'
import net from 'node:net'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { app, BrowserWindow, dialog, net as electronNet, protocol, session, type WebFrameMain } from 'electron'
import { registerIpc } from './main/ipc/register'
import type { TerminalSessionManager } from './main/terminal/sessions'
import { seedVerifyWorkspace, runWorkspaceVerify } from './main/verify'
import { stopWatching } from './main/workspace/watch'

const DEV_PORT = 5174
const DEV_ORIGINS = [`http://127.0.0.1:${DEV_PORT}`, `http://localhost:${DEV_PORT}`]
const APP_SCHEME = 'casco'
const APP_HOST = 'app'

protocol.registerSchemesAsPrivileged([
  {
    scheme: APP_SCHEME,
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
])

const windowOptions = {
  preload: path.join(__dirname, 'preload.js'),
  contextIsolation: true,
  nodeIntegration: false,
  sandbox: true,
  webSecurity: true,
  allowRunningInsecureContent: false,
} as const

app.enableSandbox()

let mainWindow: BrowserWindow | null = null
let devServer: ChildProcess | null = null
let devServerOwned = false
let terminals: TerminalSessionManager | null = null
let quitting = false

function desktopRoot(): string {
  return path.resolve(__dirname, '..')
}

function rendererDistDir(): string {
  if (app.isPackaged) return path.join(app.getAppPath(), 'dist-renderer')
  return path.join(desktopRoot(), 'dist-renderer')
}

function isDev(): boolean {
  return !app.isPackaged
}

function isVerify(): boolean {
  return process.env.CASCO_DESKTOP_VERIFY === '1'
}

function isAllowedPageUrl(raw: string): boolean {
  let url: URL
  try {
    url = new URL(raw)
  } catch {
    return false
  }
  if (url.protocol === 'casco:' && url.hostname === APP_HOST) return true
  return DEV_ORIGINS.includes(`${url.protocol}//${url.host}`)
}

function isAllowedSender(frame: WebFrameMain | null): boolean {
  return Boolean(frame && isAllowedPageUrl(frame.url))
}

function safeDistFile(requestPath: string): string | null {
  const root = path.resolve(rendererDistDir())
  let decoded = requestPath
  try {
    decoded = decodeURIComponent(requestPath)
  } catch {
    return null
  }
  if (decoded.includes('\0')) return null
  const full = path.resolve(root, decoded.replace(/^[/\\]+/, ''))
  if (full !== root && !full.startsWith(root + path.sep)) return null
  return full
}

function registerAppProtocol(): void {
  protocol.handle(APP_SCHEME, (request) => {
    const url = new URL(request.url)
    if (url.hostname !== APP_HOST) return new Response('Not found', { status: 404 })
    let filePath = safeDistFile(url.pathname)
    if (!filePath) return new Response('Bad path', { status: 400 })
    if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      if (path.extname(filePath)) return new Response('Not found', { status: 404 })
      filePath = path.join(path.resolve(rendererDistDir()), 'index.html')
    }
    return electronNet.fetch(pathToFileURL(filePath).toString())
  })
}

function portOpen(port: number, host: string): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = net.connect({ port, host })
    const done = (open: boolean) => {
      socket.removeAllListeners()
      socket.destroy()
      resolve(open)
    }
    socket.once('connect', () => done(true))
    socket.once('error', () => done(false))
  })
}

function systemNode(): string {
  if (process.platform !== 'win32') return 'node'
  const output = execFileSync('where.exe', ['node'], { encoding: 'utf8' })
  const found = output.split(/\r?\n/).map((line) => line.trim()).find((line) => line.length > 0)
  if (!found) throw new Error('Node.js was not found.')
  return found
}

function httpReady(url: string): Promise<boolean> {
  return new Promise((resolve) => {
    const req = http.get(url, (res) => {
      res.resume()
      resolve((res.statusCode ?? 500) < 500)
    })
    req.on('error', () => resolve(false))
    req.setTimeout(2000, () => {
      req.destroy()
      resolve(false)
    })
  })
}

async function waitForDevServer(): Promise<void> {
  const deadline = Date.now() + 60_000
  while (Date.now() < deadline) {
    if (await httpReady(`http://127.0.0.1:${DEV_PORT}/`)) return
    await new Promise((resolve) => setTimeout(resolve, 300))
  }
  throw new Error('The desktop interface did not become ready.')
}

function startDevServer(): void {
  const viteBin = path.join(desktopRoot(), 'node_modules', 'vite', 'bin', 'vite.js')
  devServer = spawn(systemNode(), [viteBin, '--host', '127.0.0.1', '--port', String(DEV_PORT), '--strictPort'], {
    cwd: desktopRoot(),
    stdio: 'inherit',
    env: process.env,
    windowsHide: true,
  })
  devServerOwned = true
}

function stopDevServer(): void {
  const child = devServer
  devServer = null
  if (!devServerOwned || !child?.pid) return
  devServerOwned = false
  if (process.platform === 'win32') {
    spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true })
    return
  }
  child.kill('SIGTERM')
}

function installDevContentSecurityPolicy(): void {
  const policy = [
    "default-src 'self'",
    "script-src 'self' 'unsafe-inline' blob:",
    "style-src 'self' 'unsafe-inline'",
    "font-src 'self' data:",
    "img-src 'self' data: blob:",
    "connect-src 'self' ws://127.0.0.1:5174 ws://localhost:5174 http://127.0.0.1:5174 http://localhost:5174",
    "worker-src 'self' blob:",
  ].join('; ')
  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    if (!isAllowedPageUrl(details.url)) {
      callback({ responseHeaders: details.responseHeaders })
      return
    }
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        'Content-Security-Policy': [policy],
      },
    })
  })
}

function createWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1100,
    minHeight: 700,
    title: 'Casco Studio',
    icon: path.join(__dirname, '../src/renderer/logo-mark.png'),
    show: false,
    backgroundColor: '#181818',
    autoHideMenuBar: true,
    webPreferences: { ...windowOptions },
  })
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  win.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedPageUrl(url)) event.preventDefault()
  })
  win.webContents.on('will-attach-webview', (event) => event.preventDefault())
  win.once('ready-to-show', () => win.show())
  win.on('closed', () => {
    mainWindow = null
  })
  return win
}

async function loadRenderer(win: BrowserWindow): Promise<void> {
  if (isDev()) {
    if (!(await portOpen(DEV_PORT, '127.0.0.1'))) startDevServer()
    await waitForDevServer()
    await win.loadURL(`http://127.0.0.1:${DEV_PORT}/`)
    return
  }
  const indexFile = path.join(rendererDistDir(), 'index.html')
  if (!fs.existsSync(indexFile)) throw new Error('The desktop interface has not been built.')
  await win.loadURL(`${APP_SCHEME}://${APP_HOST}/index.html`)
}

async function boot(): Promise<void> {
  registerAppProtocol()
  terminals = registerIpc({
    isAllowedSender,
    getWindow: () => mainWindow,
    emit: (channel, payload) => {
      if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send(channel, payload)
    },
  })
  if (isDev()) installDevContentSecurityPolicy()
  app.setName('Casco Studio')
  if (process.platform === 'win32') app.setAppUserModelId('studio.casco.desktop')
  const plan = isVerify() ? await seedVerifyWorkspace() : null
  const win = createWindow()
  mainWindow = win
  await loadRenderer(win)
  if (!plan) return
  const result = await runWorkspaceVerify(win, plan)
  console.log(`CASCO_VERIFY ${JSON.stringify(result)}`)
  await terminals.disposeAll()
  stopDevServer()
  stopWatching()
  fs.rmSync(plan.workspace, { recursive: true, force: true })
  app.exit(result.ok ? 0 : 1)
}

app.on('web-contents-created', (_event, contents) => {
  contents.setWindowOpenHandler(() => ({ action: 'deny' }))
  contents.on('will-attach-webview', (event) => event.preventDefault())
})

app.on('before-quit', (event) => {
  if (quitting) return
  event.preventDefault()
  quitting = true
  void Promise.resolve(terminals?.disposeAll()).finally(() => {
    stopDevServer()
    stopWatching()
    app.quit()
  })
})

app.on('window-all-closed', () => app.quit())

app.whenReady().then(boot).catch((error: unknown) => {
  const message = error instanceof Error ? error.message : 'Casco Studio could not start.'
  console.error(message)
  stopDevServer()
  if (isVerify()) app.exit(1)
  else dialog.showErrorBox('Casco Studio', message)
})
