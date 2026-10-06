import { randomUUID } from 'node:crypto'
import { channels } from '../../shared/channels'
import type { AgentEvent } from '../../shared/types'
import { DesktopFailure } from '../errors'
import type { TerminalSessionManager } from '../terminal/sessions'
import { requireWorkspacePath } from '../workspace/store'
import { runAgentLoop, type AgentUpdate } from './loop'

type Emit = (channel: string, payload: unknown) => void

export class AgentController {
  private active: { taskId: string; abort: AbortController; sessionId: string } | null = null
  private approval: ((allowed: boolean) => void) | null = null

  constructor(
    private terminals: TerminalSessionManager,
    private emit: Emit,
  ) {}

  start(prompt: string, openFile?: string): { taskId: string } {
    if (this.active) throw new DesktopFailure('PROCESS_START_FAILED', 'An agent task is already running.')
    const workspace = requireWorkspacePath()
    const taskId = randomUUID()
    const abort = new AbortController()
    const task = { taskId, abort, sessionId: '' }
    this.active = task
    void this.run(task, prompt, openFile, workspace)
    return { taskId }
  }

  async stop(): Promise<void> {
    const task = this.active
    if (!task) return
    task.abort.abort()
    this.approval?.(false)
    this.approval = null
    if (task.sessionId) await this.terminals.stop(task.sessionId).catch(() => undefined)
  }

  decide(approved: boolean): void {
    const pending = this.approval
    this.approval = null
    pending?.(approved)
  }

  private async run(
    task: { taskId: string; abort: AbortController; sessionId: string },
    prompt: string,
    openFile: string | undefined,
    workspace: string,
  ): Promise<void> {
    let sessionId = ''
    try {
      const session = await this.terminals.start(workspace, workspace)
      sessionId = session.sessionId
      task.sessionId = sessionId
      await runAgentLoop({
        taskId: task.taskId,
        prompt,
        openFile,
        terminals: this.terminals,
        sessionId,
        signal: task.abort.signal,
        approve: (command) => this.ask(task.taskId, command, task.abort.signal),
        emit: (update) => this.publish(task.taskId, update),
      })
    } catch (error) {
      const text = error instanceof DesktopFailure ? error.message : 'The agent stopped.'
      this.publish(task.taskId, { kind: 'done', text, ok: false, changes: [] })
    } finally {
      if (sessionId) await this.terminals.dispose(sessionId).catch(() => undefined)
      if (this.active?.taskId === task.taskId) this.active = null
    }
  }

  private ask(taskId: string, command: string, signal: AbortSignal): Promise<boolean> {
    return new Promise((resolve) => {
      let settled = false
      const finish = (allowed: boolean) => {
        if (settled) return
        settled = true
        if (this.approval === finish) this.approval = null
        resolve(allowed)
      }
      this.approval = finish
      this.publish(taskId, { kind: 'approval', text: command, command })
      signal.addEventListener('abort', () => finish(false), { once: true })
    })
  }

  private publish(taskId: string, update: AgentUpdate): void {
    const event: AgentEvent = { taskId, ...update }
    this.emit(channels.agentEvent, event)
  }
}
