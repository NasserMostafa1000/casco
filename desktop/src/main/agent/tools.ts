import { DesktopFailure } from '../errors'
import * as files from '../filesystem/service'
import type { CommandResult, TerminalSessionManager } from '../terminal/sessions'
import { requireWorkspacePath } from '../workspace/store'

export const MAX_VERIFICATION_RETRIES = 5

export type ToolChange = {
  path: string
  kind: 'created' | 'modified' | 'deleted' | 'renamed'
}

export type ToolSpec = {
  name: string
  description: string
  input: Record<string, string>
}

export const toolSpecs: ToolSpec[] = [
  { name: 'read_file', description: 'Read a text file in the workspace.', input: { path: 'string' } },
  { name: 'create_file', description: 'Create a new file. Fails if it already exists.', input: { path: 'string', content: 'string' } },
  { name: 'edit_file', description: 'Replace one exact snippet in an existing file.', input: { path: 'string', find: 'string', replace: 'string' } },
  { name: 'delete_file', description: 'Delete one file.', input: { path: 'string' } },
  { name: 'create_directory', description: 'Create a folder.', input: { path: 'string' } },
  { name: 'delete_directory', description: 'Delete a folder. The user must approve this.', input: { path: 'string' } },
  { name: 'rename_file', description: 'Rename a file inside the workspace.', input: { path: 'string', to: 'string' } },
  { name: 'rename_directory', description: 'Rename a folder inside the workspace.', input: { path: 'string', to: 'string' } },
  { name: 'list_directory', description: 'List files and folders. Path defaults to the workspace root.', input: { path: 'string' } },
  { name: 'search_files', description: 'Search file names and text in the workspace.', input: { query: 'string' } },
  { name: 'run_command', description: 'Run one command in the workspace. Dangerous commands are denied or need approval.', input: { command: 'string' } },
  { name: 'read_terminal', description: 'Read the latest command output.', input: {} },
  { name: 'stop_process', description: 'Stop the command started by the agent.', input: {} },
]

export type ToolOutcome = {
  ok: boolean
  text: string
  change?: ToolChange
  commandFailed?: boolean
  command?: string
  exitCode?: number | null
}

type ToolContext = {
  terminals: TerminalSessionManager
  sessionId: string
  approve: (command: string) => Promise<boolean>
  signal: AbortSignal
}

export async function runTool(name: string, input: unknown, ctx: ToolContext): Promise<ToolOutcome> {
  const body = asRecord(input)
  try {
    switch (name) {
      case 'read_file':
        return { ok: true, text: await files.readFile(text(body.path, 'path')) }
      case 'create_file': {
        const filePath = text(body.path, 'path')
        await files.createFile(filePath, text(body.content, 'content', true))
        return { ok: true, text: `Created ${filePath}`, change: { path: filePath, kind: 'created' } }
      }
      case 'edit_file': {
        const filePath = text(body.path, 'path')
        const current = await files.readFile(filePath)
        const next = replaceOnce(current, text(body.find, 'find'), text(body.replace, 'replace', true))
        if (next === null) return { ok: false, text: `edit ${filePath}: the find text was not found.` }
        await files.writeFile(filePath, next)
        return { ok: true, text: `Updated ${filePath}`, change: { path: filePath, kind: 'modified' } }
      }
      case 'delete_file': {
        const filePath = text(body.path, 'path')
        await files.deleteFile(filePath)
        return { ok: true, text: `Deleted ${filePath}`, change: { path: filePath, kind: 'deleted' } }
      }
      case 'create_directory': {
        const dirPath = text(body.path, 'path')
        await files.createDirectory(dirPath)
        return { ok: true, text: `Created folder ${dirPath}`, change: { path: dirPath, kind: 'created' } }
      }
      case 'delete_directory': {
        const dirPath = text(body.path, 'path')
        const allowed = await ctx.approve(`Delete folder ${dirPath}`)
        if (!allowed || ctx.signal.aborted) return { ok: false, text: `Delete folder ${dirPath} was rejected.` }
        await files.deleteDirectory(dirPath)
        return { ok: true, text: `Deleted folder ${dirPath}`, change: { path: dirPath, kind: 'deleted' } }
      }
      case 'rename_file':
      case 'rename_directory': {
        const from = text(body.path, 'path')
        const to = text(body.to, 'to')
        await files.renamePath(from, to, name === 'rename_directory' ? 'directory' : 'file')
        return { ok: true, text: `Renamed ${from} to ${to}`, change: { path: to, kind: 'renamed' } }
      }
      case 'list_directory': {
        const dirPath = body.path === undefined ? requireWorkspacePath() : text(body.path, 'path')
        const entries = await files.readDirectory(dirPath)
        const lines = entries.map((entry) => `${entry.kind} ${entry.name}`)
        return { ok: true, text: lines.length ? lines.join('\n') : '(empty)' }
      }
      case 'search_files': {
        const hits = await files.searchFiles(text(body.query, 'query'))
        const lines = hits.map((hit) => `${hit.path}${hit.line ? `:${hit.line}` : ''}${hit.preview ? ` ${hit.preview}` : ''}`)
        return { ok: true, text: lines.length ? lines.join('\n') : 'No matches.' }
      }
      case 'run_command':
        return runCommand(text(body.command, 'command'), ctx)
      case 'read_terminal':
        return { ok: true, text: formatCommand(ctx.terminals.latest(ctx.sessionId)) }
      case 'stop_process':
        await ctx.terminals.stop(ctx.sessionId).catch((error: unknown) => {
          if (!(error instanceof DesktopFailure) || error.code !== 'PROCESS_NOT_FOUND') throw error
        })
        return { ok: true, text: 'Stop requested.' }
      default:
        return { ok: false, text: `Unknown tool "${name}".` }
    }
  } catch (error) {
    if (error instanceof DesktopFailure) return { ok: false, text: `${error.code}: ${error.message}` }
    return { ok: false, text: 'The tool failed.' }
  }
}

async function runCommand(command: string, ctx: ToolContext): Promise<ToolOutcome> {
  if (command.length > 8000) return { ok: false, text: 'COMMAND_INVALID: The command is not valid.' }
  const stopIfAborted = () => {
    if (!ctx.signal.aborted) return Promise.resolve()
    return ctx.terminals.stop(ctx.sessionId).catch(() => undefined)
  }
  const onAbort = () => {
    void ctx.terminals.stop(ctx.sessionId).catch(() => undefined)
  }
  ctx.signal.addEventListener('abort', onAbort)
  try {
    let started: { commandId: string }
    try {
      started = await ctx.terminals.execute(ctx.sessionId, command, requireWorkspacePath(), 'ai', false)
    } catch (error) {
      if (error instanceof DesktopFailure && error.code === 'APPROVAL_REQUIRED') {
        const allowed = await ctx.approve(command)
        if (!allowed || ctx.signal.aborted) return { ok: false, text: `The command was not run: ${command}` }
        started = await ctx.terminals.execute(ctx.sessionId, command, requireWorkspacePath(), 'ai', true)
      } else if (error instanceof DesktopFailure) {
        return { ok: false, text: `${error.code}: ${error.message}` }
      } else {
        return { ok: false, text: 'The command could not start.' }
      }
    }
    await stopIfAborted()
    const result = await ctx.terminals.wait(ctx.sessionId, started.commandId)
    const failed = result.status !== 'completed' || result.exitCode !== 0
    return {
      ok: !failed,
      text: formatCommand(result),
      commandFailed: failed && result.status !== 'stopped',
      command,
      exitCode: result.exitCode,
    }
  } finally {
    ctx.signal.removeEventListener('abort', onAbort)
  }
}

function formatCommand(result: CommandResult | null): string {
  if (!result) return 'No command has run.'
  return [
    `command: ${result.command}`,
    `cwd: ${result.cwd}`,
    `status: ${result.status}`,
    `exitCode: ${result.exitCode ?? ''}`,
    `durationMs: ${result.duration}`,
    `stdout:\n${clip(result.stdout)}`,
    `stderr:\n${clip(result.stderr)}`,
  ].join('\n')
}

function clip(value: string): string {
  return value.length > 4000 ? value.slice(-4000) : value
}

function asRecord(input: unknown): Record<string, unknown> {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return {}
  return input as Record<string, unknown>
}

function text(value: unknown, label: string, allowEmpty = false): string {
  if (typeof value !== 'string' || value.includes('\0') || (!allowEmpty && !value.trim())) {
    throw new DesktopFailure('INVALID_PATH', `${label} is not valid.`)
  }
  return value
}

/** Same matching order as the web editor: exact, trimmed, then whitespace-tolerant. */
export function replaceOnce(content: string, find: string, replace: string): string | null {
  const exact = content.indexOf(find)
  if (exact >= 0) return content.slice(0, exact) + replace + content.slice(exact + find.length)
  const trimmed = find.trim()
  if (trimmed && trimmed !== find) {
    const at = content.indexOf(trimmed)
    if (at >= 0) return content.slice(0, at) + replace.trim() + content.slice(at + trimmed.length)
  }
  const tokens = trimmed.split(/\s+/).filter(Boolean).map(escapeRegex)
  if (tokens.length === 0) return null
  const match = new RegExp(tokens.join('\\s*')).exec(content)
  if (!match || match.index === undefined) return null
  return content.slice(0, match.index) + replace + content.slice(match.index + match[0].length)
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
