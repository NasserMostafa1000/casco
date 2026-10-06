import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import type { BrowserWindow } from 'electron'
import { setWorkspacePath } from './workspace/store'

export type VerifyPlan = {
  workspace: string
  hello: string
  source: string
  outside: string
  echo: string
  fail: string
  sleep: string
  pwd: string
}

export async function seedVerifyWorkspace(): Promise<VerifyPlan> {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'casco-desktop-'))
  fs.mkdirSync(path.join(folder, 'src'))
  fs.writeFileSync(path.join(folder, 'hello.txt'), 'version-one\n', 'utf8')
  fs.writeFileSync(path.join(folder, 'src', 'App.tsx'), 'export const marker = "casco-search-marker"\n', 'utf8')
  fs.writeFileSync(path.join(folder, 'package.json'), '{"scripts":{"build":"node build.js"}}\n', 'utf8')
  fs.writeFileSync(
    path.join(folder, 'build.js'),
    "const fs = require('fs')\nconst text = fs.readFileSync('src/Widget.js', 'utf8')\nif (!text.includes('export function Widget')) {\n  console.error('Widget.js must export function Widget')\n  process.exit(1)\n}\nconsole.log('build ok')\n",
    'utf8',
  )
  const info = await setWorkspacePath(folder)
  const windows = process.platform === 'win32'
  return {
    workspace: info.path,
    hello: path.join(info.path, 'hello.txt'),
    source: path.join(info.path, 'src'),
    outside: path.join(path.dirname(info.path), 'casco-outside.txt'),
    echo: windows ? 'Write-Output hello' : 'echo hello',
    fail: windows ? "[Console]::Error.WriteLine('boom'); exit 2" : 'echo boom >&2; exit 2',
    sleep: windows ? 'Start-Sleep -Seconds 30' : 'sleep 30',
    pwd: windows ? 'Write-Output ((Get-Location).Path)' : 'pwd',
  }
}

export async function runWorkspaceVerify(win: BrowserWindow, plan: VerifyPlan): Promise<{ ok: boolean; report: unknown }> {
  const report: { ok?: boolean } = await win.webContents.executeJavaScript(verifyScript(plan))
  return { ok: report?.ok === true, report }
}

function verifyScript(plan: VerifyPlan): string {
  return `(async () => {
    const plan = ${JSON.stringify(plan)}
    const api = window.cascoDesktop
    const notes = []
    const fail = (label) => notes.push(label)
    const sep = plan.workspace.includes('\\\\') ? '\\\\' : '/'
    const join = (name) => plan.workspace.endsWith(sep) ? plan.workspace + name : plan.workspace + sep + name
    const waitFor = async (read, ms = 15000) => {
      const start = Date.now()
      while (Date.now() - start < ms) {
        try {
          const value = await read()
          if (value) return value
        } catch (error) {}
        await new Promise((resolve) => setTimeout(resolve, 200))
      }
      throw new Error('timed out lines=' + JSON.stringify((document.querySelector('.view-lines')?.textContent || '').slice(0, 80)) + ' mounted=' + Boolean(document.querySelector('.monaco-editor')))
    }
    const codeOf = async (promise) => {
      try { await promise; return '' } catch (error) { return error && error.code ? error.code : 'THREW' }
    }
    const outputs = []
    const states = []
    api.terminal.onOutput((event) => outputs.push(event))
    api.terminal.onState((event) => states.push(event))
    const waitState = (sessionId, names, ms = 8000) => {
      const start = states.length
      return new Promise((resolve, reject) => {
        const match = () => states.slice(start).find((event) => event.sessionId === sessionId && names.includes(event.state))
        const found = match()
        if (found) { resolve(found); return }
        const timer = setTimeout(() => reject(new Error('state timeout ' + names.join(','))), ms)
        const stop = api.terminal.onState(() => {
          const next = match()
          if (!next) return
          clearTimeout(timer)
          stop()
          resolve(next)
        })
      })
    }
    try {
      if (typeof require !== 'undefined') fail('require exposed')
      if (typeof process !== 'undefined') fail('process exposed')
      if (typeof window.fs !== 'undefined' || typeof window.child_process !== 'undefined') fail('node exposed')
      await waitFor(() => document.body.innerText.includes('hello.txt'))
      const current = await api.workspace.current()
      if (!current || current.path.toLowerCase() !== plan.workspace.toLowerCase()) fail('workspace')
      const rootEntries = await api.fs.readDirectory(plan.workspace)
      if (!rootEntries.some((entry) => entry.name === 'hello.txt')) fail('list file')
      if (!rootEntries.some((entry) => entry.name === 'src' && entry.kind === 'directory')) fail('list folder')
      if (!(await api.fs.readDirectory(plan.source)).some((entry) => entry.name === 'App.tsx')) fail('nested')
      if (!(await api.fs.readFile(plan.hello)).includes('version-one')) fail('read')
      await api.fs.writeFile(plan.hello, 'edited\\n')
      if (!(await api.fs.readFile(plan.hello)).includes('edited')) fail('write')
      const created = join('created.txt')
      const renamed = join('renamed.txt')
      const folder = join('created-dir')
      const renamedDir = join('renamed-dir')
      await api.fs.createFile(created, 'new file')
      await api.fs.createDirectory(folder)
      await api.fs.renameFile(created, renamed)
      await api.fs.renameDirectory(folder, renamedDir)
      if ((await api.fs.readFile(renamed)) !== 'new file') fail('rename file')
      const listed = await api.fs.readDirectory(plan.workspace)
      if (!listed.some((entry) => entry.name === 'renamed-dir' && entry.kind === 'directory')) fail('rename folder')
      await api.fs.deleteFile(renamed)
      await api.fs.deleteDirectory(renamedDir)
      if ((await api.fs.readDirectory(plan.workspace)).some((entry) => entry.name === 'renamed.txt' || entry.name === 'renamed-dir')) fail('delete')
      if (!(await api.fs.searchFiles('casco-search-marker')).some((hit) => hit.name === 'App.tsx')) fail('search')
      if (await codeOf(api.fs.readFile(plan.outside)) !== 'INVALID_WORKSPACE') fail('outside absolute')
      if (await codeOf(api.fs.readFile(join('..') + sep + '..' + sep + 'casco-outside.txt')) !== 'INVALID_WORKSPACE') fail('traversal')
      if (await codeOf(api.fs.readFile(12)) !== 'INVALID_PATH') fail('malformed path')
      await api.fs.writeFile(plan.hello, 'version-one\\n')
      const button = [...document.querySelectorAll('[data-path]')].find((node) => node.getAttribute('data-path') === plan.hello)
      if (!button) fail('explorer button')
      else button.click()
      await waitFor(() => (document.querySelector('.view-lines')?.textContent || '').includes('version-one'))
      await api.fs.writeFile(plan.hello, 'version-two\\n')
      const save = document.querySelector('[data-testid="save-file"]')
      if (!save) fail('save missing')
      else save.click()
      await new Promise((resolve) => setTimeout(resolve, 400))
      const disk = await api.fs.readFile(plan.hello)
      if (!disk.includes('version-one')) fail('save disk=' + JSON.stringify(disk) + ' draft=' + (save && save.getAttribute('data-draft')))
      const srcButton = [...document.querySelectorAll('[data-kind="directory"]')].find((node) => node.getAttribute('data-path') === plan.source)
      if (!srcButton) fail('folder row')
      else srcButton.click()
      await waitFor(() => document.body.innerText.includes('App.tsx'))
    } catch (error) {
      fail(error && error.message ? error.message : 'workspace ui failed')
    }
    try {
      const session = await api.terminal.startSession({ cwd: plan.workspace })
      const echoDone = waitState(session.sessionId, ['completed', 'failed', 'stopped'])
      const echo = await api.terminal.execute(session.sessionId, plan.echo)
      const echoState = await echoDone
      const stdout = outputs.filter((event) => event.commandId === echo.commandId && event.stream === 'stdout').map((event) => event.data).join('')
      if (!stdout.toLowerCase().includes('hello')) fail('stdout')
      if (echoState.state !== 'completed' || echoState.exitCode !== 0) fail('exit 0')
      const failDone = waitState(session.sessionId, ['completed', 'failed', 'stopped'])
      const failed = await api.terminal.execute(session.sessionId, plan.fail)
      const failState = await failDone
      const stderr = outputs.filter((event) => event.commandId === failed.commandId && event.stream === 'stderr').map((event) => event.data).join('')
      if (!stderr.toLowerCase().includes('boom')) fail('stderr:' + stderr.slice(0, 180))
      if (!failState.exitCode) fail('nonzero exit')
      const running = waitState(session.sessionId, ['running'])
      await api.terminal.execute(session.sessionId, plan.sleep)
      await running
      await api.terminal.stop(session.sessionId)
      const stopped = [...states].reverse().find((event) => event.sessionId === session.sessionId && event.state === 'stopped')
      if (!stopped) fail('stop')
      const pwdDone = waitState(session.sessionId, ['completed', 'failed'])
      const pwd = await api.terminal.execute(session.sessionId, plan.pwd)
      await pwdDone
      const where = outputs.filter((event) => event.commandId === pwd.commandId && event.stream === 'stdout').map((event) => event.data).join('').trim().toLowerCase()
      if (!where.includes(plan.workspace.toLowerCase())) fail('cwd:' + where)
      if (await codeOf(api.terminal.execute('00000000-0000-0000-0000-000000000000', plan.echo)) !== 'INVALID_SESSION') fail('bad session')
      if (await codeOf(api.terminal.startSession({ cwd: plan.outside })) !== 'INVALID_WORKSPACE') fail('bad cwd')
      if (await codeOf(api.terminal.execute(session.sessionId, '')) !== 'COMMAND_INVALID') fail('empty command')
      await api.terminal.dispose(session.sessionId)
      if (await codeOf(api.terminal.execute(session.sessionId, plan.echo)) !== 'INVALID_SESSION') fail('disposed')
    } catch (error) {
      fail(error && error.message ? error.message : 'terminal failed')
    }
    const agentEvents = []
    api.agent.onEvent((event) => agentEvents.push(event))
    const waitAgent = async (from, read, ms) => {
      const limit = ms || 30000
      const started = Date.now()
      while (Date.now() - started < limit) {
        const found = agentEvents.slice(from).find(read)
        if (found) return found
        await new Promise((resolve) => setTimeout(resolve, 100))
      }
      const seen = agentEvents.slice(from).map((event) => event.kind + ':' + event.text).join(' | ').slice(0, 400)
      throw new Error('agent timeout ' + seen)
    }
    const runAgent = async (prompt) => {
      const from = agentEvents.length
      await api.agent.start({ prompt: prompt })
      return waitAgent(from, (event) => event.kind === 'done')
    }
    try {
      await api.fs.deleteFile(plan.hello)
      const hello = await runAgent('Create a hello.txt file containing Hello Casco.')
      if (!hello.ok) fail('agent hello status')
      const helloText = await api.fs.readFile(plan.hello)
      if (helloText.indexOf('Hello Casco') < 0) fail('agent hello text')
      const page = await runAgent('Create a simple HTML page.')
      if (!page.ok) fail('agent html status')
      const html = await api.fs.readFile(join('index.html'))
      if (html.toLowerCase().indexOf('<html') < 0) fail('agent html')
      const widget = join('src') + sep + 'Widget.js'
      const built = await runAgent('Create a React component and make sure the project builds.')
      if (!built.ok || built.text.indexOf('Build successful') < 0) fail('agent build ' + (built.text || ''))
      const widgetText = await api.fs.readFile(widget)
      if (widgetText.indexOf('export function Widget') < 0) fail('agent widget')
      await api.fs.writeFile(widget, 'export const Widget = (')
      const fixed = await runAgent('Fix the build error.')
      if (!fixed.ok) fail('agent fix ' + (fixed.text || ''))
      const fixedText = await api.fs.readFile(widget)
      if (fixedText.indexOf('export function Widget') < 0) fail('agent fix text')
      const fromStop = agentEvents.length
      await api.agent.start({ prompt: 'Run a long command then create leaked.txt.' })
      await waitAgent(fromStop, (event) => event.tool === 'run_command', 10000)
      await api.agent.stop()
      const stopped = await waitAgent(fromStop, (event) => event.kind === 'done', 15000)
      if (stopped.ok) fail('agent stop should not succeed')
      if (stopped.text.indexOf('Stopped') < 0) fail('agent stop text')
      const leakedCode = await codeOf(api.fs.readFile(join('leaked.txt')))
      if (!leakedCode) fail('leaked')
      const fromDanger = agentEvents.length
      await api.agent.start({ prompt: 'Ask to run a dangerous command.' })
      await waitAgent(fromDanger, (event) => event.kind === 'approval', 10000)
      await api.agent.decide(false)
      const rejected = await waitAgent(fromDanger, (event) => event.kind === 'done', 10000)
      if (!rejected.ok) fail('agent reject status')
      if (rejected.text.indexOf('not run') < 0) fail('agent reject text')
      const fromOutside = agentEvents.length
      const outside = await runAgent('Read a file outside the workspace.')
      const blocked = agentEvents.slice(fromOutside).find((event) => event.kind === 'error')
      if (!blocked || blocked.text.indexOf('INVALID_WORKSPACE') < 0) fail('agent traversal')
      if (outside.text.indexOf('outside') < 0) fail('agent outside')
      const outsideCode = await codeOf(api.fs.readFile(plan.outside))
      if (outsideCode !== 'INVALID_WORKSPACE') fail('agent outside read ' + outsideCode)
    } catch (error) {
      fail(error && error.message ? error.message : 'agent failed')
    }
    return { ok: notes.length === 0, notes, title: document.title, requireType: typeof require, processType: typeof process }
  })()`
}
