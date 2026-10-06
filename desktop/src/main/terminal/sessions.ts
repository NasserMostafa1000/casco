import { spawn, type ChildProcess } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import fs from 'node:fs'
import { channels } from '../../shared/channels'
import type { TerminalOutputEvent, TerminalSessionInfo, TerminalStateEvent } from '../../shared/types'
import { DesktopFailure } from '../errors'
import { resolveInsideWorkspace } from '../filesystem/paths'
import { validateCommand } from './policy'
import { platformShell, terminalEnv, type ShellSpec } from './shell'

const STORED_LIMIT = 500_000

type LiveCommand = {
  commandId: string
  command: string
  cwd: string
  startedAt: number
  stdout: string
  stderr: string
  stopRequested: boolean
  child: ChildProcess | null
  exitCode: number | null
  status: TerminalStateEvent['state'] | null
  finishedAt: number | null
}

export type CommandResult = {
  commandId: string
  command: string
  cwd: string
  stdout: string
  stderr: string
  exitCode: number | null
  status: TerminalStateEvent['state']
  duration: number
}

type Session = {
  id: string
  cwd: string
  shell: ShellSpec
  command: LiveCommand | null
}

export type TerminalEmit = (channel: string, payload: TerminalOutputEvent | TerminalStateEvent) => void

export class TerminalSessionManager {
  private sessions = new Map<string, Session>()
  private waiters = new Map<string, (result: CommandResult) => void>()

  constructor(private emit: TerminalEmit) {}

  async start(cwdInput: string | undefined, workspace: string): Promise<TerminalSessionInfo> {
    const cwd = await resolveInsideWorkspace(workspace, cwdInput ?? workspace)
    const stat = await fs.promises.stat(cwd)
    if (!stat.isDirectory()) throw new DesktopFailure('INVALID_WORKSPACE', 'The terminal folder is not valid.')
    const shell = platformShell()
    const session: Session = { id: randomUUID(), cwd, shell, command: null }
    this.sessions.set(session.id, session)
    return { sessionId: session.id, cwd, shell: shell.label }
  }

  async execute(
    sessionId: string,
    command: string,
    workspace: string,
    source: 'user' | 'ai' = 'user',
    userApproved = false,
  ): Promise<{ commandId: string }> {
    const session = this.require(sessionId)
    if (session.command?.child) throw new DesktopFailure('PROCESS_START_FAILED', 'A command is already running.')
    const cwd = await resolveInsideWorkspace(workspace, session.cwd)
    const decision = validateCommand({ command, cwd, source })
    if (decision === 'DENY' || (decision === 'ASK_USER' && !userApproved)) {
      throw new DesktopFailure(
        decision === 'DENY' ? 'COMMAND_INVALID' : 'APPROVAL_REQUIRED',
        decision === 'DENY' ? 'The command is not allowed.' : 'The command needs approval.',
      )
    }
    const commandId = randomUUID()
    const live: LiveCommand = {
      commandId,
      command,
      cwd,
      startedAt: Date.now(),
      stdout: '',
      stderr: '',
      stopRequested: false,
      child: null,
      exitCode: null,
      status: null,
      finishedAt: null,
    }
    session.command = live
    this.state(session, { state: 'starting', commandId, exitCode: null, signal: null })

    const child = spawn(session.shell.file, session.shell.args(command), {
      cwd,
      env: terminalEnv(),
      windowsHide: true,
      detached: process.platform !== 'win32',
      stdio: ['pipe', 'pipe', 'pipe'],
    })
    live.child = child
    child.stdout?.setEncoding('utf8')
    child.stderr?.setEncoding('utf8')
    child.stdout?.on('data', (chunk: string) => {
      live.stdout = clip(live.stdout + chunk)
      this.output(session, { commandId, stream: 'stdout', data: chunk })
    })
    child.stderr?.on('data', (chunk: string) => {
      live.stderr = clip(live.stderr + chunk)
      this.output(session, { commandId, stream: 'stderr', data: chunk })
    })
    child.once('close', (code, signal) => {
      live.child = null
      live.exitCode = code
      live.status = live.stopRequested ? 'stopped' : code === 0 ? 'completed' : 'failed'
      live.finishedAt = Date.now()
      this.state(session, {
        state: live.status,
        commandId,
        exitCode: code,
        signal: signal ?? null,
      })
      const waiter = this.waiters.get(commandId)
      if (waiter) {
        this.waiters.delete(commandId)
        waiter(this.snapshot(live))
      }
    })

    await new Promise<void>((resolve, reject) => {
      const fail = () => {
        live.child = null
        live.status = 'failed'
        live.finishedAt = Date.now()
        this.state(session, { state: 'failed', commandId, exitCode: null, signal: null })
        const waiter = this.waiters.get(commandId)
        if (waiter) {
          this.waiters.delete(commandId)
          waiter(this.snapshot(live))
        }
        reject(new DesktopFailure('PROCESS_START_FAILED', 'The command could not start.'))
      }
      child.once('error', fail)
      child.once('spawn', () => {
        child.off('error', fail)
        this.state(session, { state: 'running', commandId, exitCode: null, signal: null })
        resolve()
      })
    })
    return { commandId }
  }

  wait(sessionId: string, commandId: string): Promise<CommandResult> {
    const live = this.require(sessionId).command
    if (!live || live.commandId !== commandId) throw new DesktopFailure('PROCESS_NOT_FOUND', 'There is no running command.')
    if (live.finishedAt) return Promise.resolve(this.snapshot(live))
    return new Promise((resolve) => {
      this.waiters.set(commandId, resolve)
      if (live.finishedAt) {
        this.waiters.delete(commandId)
        resolve(this.snapshot(live))
      }
    })
  }

  latest(sessionId: string): CommandResult | null {
    const live = this.require(sessionId).command
    if (!live) return null
    return this.snapshot(live)
  }

  private snapshot(live: LiveCommand): CommandResult {
    return {
      commandId: live.commandId,
      command: live.command,
      cwd: live.cwd,
      stdout: live.stdout,
      stderr: live.stderr,
      exitCode: live.exitCode,
      status: live.status ?? (live.child ? 'running' : 'completed'),
      duration: (live.finishedAt ?? Date.now()) - live.startedAt,
    }
  }

  write(sessionId: string, input: string): void {
    const command = this.require(sessionId).command
    if (!command?.child?.stdin || command.child.stdin.destroyed) {
      throw new DesktopFailure('PROCESS_NOT_FOUND', 'There is no running command.')
    }
    command.child.stdin.write(input)
  }

  async stop(sessionId: string): Promise<void> {
    const command = this.require(sessionId).command
    const child = command?.child
    if (!command || !child?.pid) throw new DesktopFailure('PROCESS_NOT_FOUND', 'There is no running command.')
    command.stopRequested = true
    await killChild(child)
  }

  async dispose(sessionId: string): Promise<void> {
    const session = this.sessions.get(sessionId)
    if (!session) throw new DesktopFailure('INVALID_SESSION', 'That terminal session is not valid.')
    this.sessions.delete(sessionId)
    if (session.command?.child) {
      session.command.stopRequested = true
      await killChild(session.command.child).catch(() => undefined)
    }
  }

  async disposeAll(): Promise<void> {
    for (const id of [...this.sessions.keys()]) {
      await this.dispose(id).catch(() => undefined)
    }
  }

  private require(sessionId: string): Session {
    const session = this.sessions.get(sessionId)
    if (!session) throw new DesktopFailure('INVALID_SESSION', 'That terminal session is not valid.')
    return session
  }

  private output(session: Session, event: Omit<TerminalOutputEvent, 'sessionId' | 'timestamp'>): void {
    this.emit(channels.terminalOutput, { ...event, sessionId: session.id, timestamp: Date.now() })
  }

  private state(session: Session, event: Omit<TerminalStateEvent, 'sessionId' | 'timestamp'>): void {
    this.emit(channels.terminalState, { ...event, sessionId: session.id, timestamp: Date.now() })
  }
}

function clip(value: string): string {
  return value.length > STORED_LIMIT ? value.slice(value.length - STORED_LIMIT) : value
}

function killChild(child: ChildProcess): Promise<void> {
  const pid = child.pid
  if (!pid || child.exitCode !== null || child.signalCode !== null) return Promise.resolve()
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new DesktopFailure('PROCESS_STOP_FAILED', 'The command did not stop.')), 5000)
    child.once('close', () => {
      clearTimeout(timer)
      resolve()
    })
    if (process.platform === 'win32') {
      const killer = spawn('taskkill', ['/pid', String(pid), '/t', '/f'], { windowsHide: true })
      killer.once('error', () => undefined)
      return
    }
    try {
      process.kill(-pid, 'SIGTERM')
    } catch {
      try {
        child.kill('SIGTERM')
      } catch {
        clearTimeout(timer)
        resolve()
      }
    }
  })
}
