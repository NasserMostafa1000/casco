import type { TerminalSessionManager } from '../terminal/sessions'
import { requireWorkspacePath } from '../workspace/store'
import { createProvider, type ChatTurn } from './provider'
import { requestMessage, systemPrompt } from './prompt'
import { MAX_VERIFICATION_RETRIES, runTool, type ToolChange, type ToolOutcome } from './tools'

export const MAX_STEPS = 16

export type AgentUpdate = {
  kind: 'status' | 'tool' | 'change' | 'approval' | 'done' | 'error'
  text: string
  tool?: string
  command?: string
  exitCode?: number | null
  ok?: boolean
  changes?: ToolChange[]
}

type LoopInput = {
  taskId: string
  prompt: string
  openFile?: string
  terminals: TerminalSessionManager
  sessionId: string
  signal: AbortSignal
  approve: (command: string) => Promise<boolean>
  emit: (update: AgentUpdate) => void
}

type AgentAction = {
  status?: string
  summary?: string
  tool?: string
  input?: unknown
  done?: boolean
}

export async function runAgentLoop(input: LoopInput): Promise<void> {
  const workspace = requireWorkspacePath()
  const provider = createProvider(input.prompt)
  const messages: ChatTurn[] = [
    { role: 'system', content: systemPrompt() },
    {
      role: 'user',
      content: requestMessage({
        workspace,
        platform: process.platform,
        request: input.prompt,
        openFile: input.openFile,
      }),
    },
  ]
  const changes: ToolChange[] = []
  let retries = 0
  let lastFailure = ''
  const attempted: string[] = []

  const finish = (ok: boolean, text: string) => {
    input.emit({ kind: 'done', text, ok, changes: [...changes] })
  }

  try {
    for (let step = 0; step < MAX_STEPS; step += 1) {
      if (input.signal.aborted) {
        finish(false, 'Stopped.')
        return
      }
      const raw = await provider.complete(messages, input.signal)
      const action = parseAction(raw)
      if (!action) {
        messages.push({ role: 'assistant', content: clip(raw, 2000) })
        messages.push({ role: 'user', content: 'Reply with only the JSON object.' })
        continue
      }
      if (action.status) input.emit({ kind: 'status', text: action.status })
      if (action.done) {
        if (lastFailure && retries < MAX_VERIFICATION_RETRIES) {
          messages.push({ role: 'assistant', content: clip(raw, 2000) })
          messages.push({ role: 'user', content: `The last check failed. Fix it before finishing.\n${lastFailure}` })
          continue
        }
        finish(!lastFailure, action.summary?.trim() || action.status || 'Finished.')
        return
      }
      if (!action.tool) {
        messages.push({ role: 'assistant', content: clip(raw, 2000) })
        messages.push({ role: 'user', content: 'Choose one tool, or set done to true after verification.' })
        continue
      }
      input.emit({
        kind: 'tool',
        text: action.status || action.tool,
        tool: action.tool,
        command: action.tool === 'run_command' ? commandOf(action.input) : undefined,
      })
      const outcome = await runTool(action.tool, action.input, {
        terminals: input.terminals,
        sessionId: input.sessionId,
        approve: input.approve,
        signal: input.signal,
      })
      if (input.signal.aborted) {
        finish(false, 'Stopped.')
        return
      }
      if (outcome.change) {
        changes.push(outcome.change)
        input.emit({ kind: 'change', text: outcome.text, tool: action.tool })
      } else if (!outcome.ok) {
        input.emit({ kind: 'error', text: outcome.text, tool: action.tool })
      }
      noteFailure(outcome, attempted)
      if (outcome.commandFailed) {
        retries += 1
        lastFailure = outcome.text
        if (retries >= MAX_VERIFICATION_RETRIES) {
        finish(false, `Stopped after ${MAX_VERIFICATION_RETRIES} failed checks.\n${failureReport(attempted, lastFailure)}`)
          return
        }
      } else if (outcome.command && outcome.ok) {
        lastFailure = ''
      }
      messages.push({ role: 'assistant', content: clip(raw, 2000) })
      messages.push({ role: 'user', content: `Tool ${action.tool} result:\n${clip(outcome.text, 6000)}` })
    }
    finish(false, failureReport(attempted, lastFailure || 'The step limit was reached.'))
  } catch (error) {
    if (input.signal.aborted) {
      finish(false, 'Stopped.')
      return
    }
    const code = error instanceof Error ? error.message : ''
    if (code === 'MODEL_NOT_CONFIGURED') {
      input.emit({
        kind: 'error',
        text: 'No model endpoint is configured. Set CASCO_MODEL_BASE_URL, CASCO_MODEL_API_KEY, and CASCO_MODEL_NAME. Casco server keys are not stored in this app.',
      })
      finish(false, 'No model is configured.')
      return
    }
    input.emit({ kind: 'error', text: 'The model request failed.' })
    finish(false, 'The model request failed.')
  }
}

function noteFailure(outcome: ToolOutcome, attempted: string[]): void {
  if (outcome.command) attempted.push(outcome.command)
}

function failureReport(attempted: string[], error: string): string {
  const tried = attempted.length ? `Attempted: ${attempted.join(' | ')}` : 'No command was attempted.'
  return `${tried}\n${clip(error, 1200)}`
}

function commandOf(input: unknown): string | undefined {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return undefined
  const command = (input as { command?: unknown }).command
  return typeof command === 'string' ? command : undefined
}

export function parseAction(raw: string): AgentAction | null {
  let text = raw.trim()
  if (text.startsWith('```')) {
    const newline = text.indexOf('\n')
    text = newline >= 0 ? text.slice(newline + 1) : text
    if (text.endsWith('```')) text = text.slice(0, -3)
  }
  const start = text.indexOf('{')
  const end = text.lastIndexOf('}')
  if (start < 0 || end <= start) return null
  try {
    const value = JSON.parse(text.slice(start, end + 1)) as AgentAction
    if (!value || typeof value !== 'object') return null
    return value
  } catch {
    return null
  }
}

function clip(value: string, max: number): string {
  return value.length > max ? value.slice(0, max) : value
}
