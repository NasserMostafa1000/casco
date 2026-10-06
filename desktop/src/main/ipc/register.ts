import { dialog, ipcMain, type BrowserWindow, type WebFrameMain } from 'electron'
import { channels } from '../../shared/channels'
import type { WorkspaceInfo } from '../../shared/types'
import { AgentController } from '../agent/controller'
import { DesktopFailure, asObject, asString } from '../errors'
import * as files from '../filesystem/service'
import { TerminalSessionManager } from '../terminal/sessions'
import {
  getWorkspacePath,
  onWorkspaceChanged,
  requireWorkspacePath,
  setWorkspacePath,
  workspaceInfo,
} from '../workspace/store'
import { stopWatching, watchWorkspace } from '../workspace/watch'

type Emit = (channel: string, payload: unknown) => void

export function registerIpc(options: {
  isAllowedSender: (frame: WebFrameMain | null) => boolean
  getWindow: () => BrowserWindow | null
  emit: Emit
}): TerminalSessionManager {
  const terminals = new TerminalSessionManager((channel, payload) => options.emit(channel, payload))
  const agent = new AgentController(terminals, options.emit)

  const guard = async <T>(frame: WebFrameMain | null, work: () => Promise<T> | T) => {
    if (!options.isAllowedSender(frame)) {
      return { ok: false as const, error: { code: 'INVALID_WORKSPACE', message: 'The request was rejected.' } }
    }
    try {
      return { ok: true as const, data: await work() }
    } catch (error) {
      if (error instanceof DesktopFailure) return { ok: false as const, error: { code: error.code, message: error.message } }
      console.error(error instanceof Error ? error.message : 'Desktop operation failed.')
      return { ok: false as const, error: { code: 'INTERNAL_ERROR', message: 'The operation failed.' } }
    }
  }

  const publishWorkspace = (info: WorkspaceInfo | null) => {
    options.emit(channels.workspaceChanged, info)
    if (info) watchWorkspace(info.path, () => options.emit(channels.workspaceFilesChanged, { at: Date.now() }))
    else stopWatching()
  }

  onWorkspaceChanged(() => {
    void agent.stop()
    void terminals.disposeAll()
    publishWorkspace(workspaceInfo())
  })

  ipcMain.handle(channels.ping, (event) =>
    guard(event.senderFrame, () => ({ ok: true as const, name: 'Casco Studio' })),
  )

  ipcMain.handle(channels.workspaceCurrent, (event) => guard(event.senderFrame, () => workspaceInfo()))

  ipcMain.handle(channels.workspaceSelect, (event) =>
    guard(event.senderFrame, async () => {
      const win = options.getWindow()
      const dialogOptions = { title: 'Open project folder', properties: ['openDirectory'] as Array<'openDirectory'> }
      const picked = win ? await dialog.showOpenDialog(win, dialogOptions) : await dialog.showOpenDialog(dialogOptions)
      if (picked.canceled || !picked.filePaths[0]) return null
      return setWorkspacePath(picked.filePaths[0])
    }),
  )

  ipcMain.handle(channels.fsReadFile, (event, filePath: unknown) =>
    guard(event.senderFrame, () => files.readFile(asString(filePath, 'INVALID_PATH'))),
  )
  ipcMain.handle(channels.fsWriteFile, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'INVALID_PATH')
      return files.writeFile(asString(body.path, 'INVALID_PATH'), asString(body.content, 'INVALID_PATH', true))
    }),
  )
  ipcMain.handle(channels.fsCreateFile, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'INVALID_PATH')
      return files.createFile(asString(body.path, 'INVALID_PATH'), asString(body.content, 'INVALID_PATH', true))
    }),
  )
  ipcMain.handle(channels.fsCreateDirectory, (event, dirPath: unknown) =>
    guard(event.senderFrame, () => files.createDirectory(asString(dirPath, 'INVALID_PATH'))),
  )
  ipcMain.handle(channels.fsDeleteFile, (event, filePath: unknown) =>
    guard(event.senderFrame, () => files.deleteFile(asString(filePath, 'INVALID_PATH'))),
  )
  ipcMain.handle(channels.fsDeleteDirectory, (event, dirPath: unknown) =>
    guard(event.senderFrame, () => files.deleteDirectory(asString(dirPath, 'INVALID_PATH'))),
  )
  ipcMain.handle(channels.fsRename, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'INVALID_PATH')
      const kind = body.kind === 'directory' ? 'directory' : body.kind === 'file' ? 'file' : null
      if (!kind) throw new DesktopFailure('INVALID_PATH', 'The request was not valid.')
      return files.renamePath(asString(body.oldPath, 'INVALID_PATH'), asString(body.newPath, 'INVALID_PATH'), kind)
    }),
  )
  ipcMain.handle(channels.fsReadDirectory, (event, dirPath: unknown) =>
    guard(event.senderFrame, () => files.readDirectory(asString(dirPath, 'INVALID_PATH'))),
  )
  ipcMain.handle(channels.fsSearch, (event, query: unknown) =>
    guard(event.senderFrame, () => files.searchFiles(asString(query, 'INVALID_PATH'))),
  )

  ipcMain.handle(channels.terminalStart, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const workspace = requireWorkspacePath()
      let cwd: string | undefined
      if (payload !== undefined && payload !== null) {
        const body = asObject(payload, 'INVALID_WORKSPACE')
        if (body.cwd !== undefined) cwd = asString(body.cwd, 'INVALID_WORKSPACE')
      }
      return terminals.start(cwd, workspace)
    }),
  )
  ipcMain.handle(channels.terminalExecute, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'COMMAND_INVALID')
      return terminals.execute(asSessionId(body.sessionId), asCommand(body.command), requireWorkspacePath())
    }),
  )
  ipcMain.handle(channels.terminalWrite, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'COMMAND_INVALID')
      const input = asString(body.input, 'COMMAND_INVALID', true)
      if (input.length > 100_000) throw new DesktopFailure('COMMAND_INVALID', 'That input is too large.')
      terminals.write(asSessionId(body.sessionId), input)
    }),
  )
  ipcMain.handle(channels.terminalStop, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'INVALID_SESSION')
      return terminals.stop(asSessionId(body.sessionId))
    }),
  )
  ipcMain.handle(channels.terminalDispose, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'INVALID_SESSION')
      return terminals.dispose(asSessionId(body.sessionId))
    }),
  )

  ipcMain.handle(channels.agentStart, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'COMMAND_INVALID')
      const prompt = asString(body.prompt, 'COMMAND_INVALID')
      if (prompt.length > 8000) throw new DesktopFailure('COMMAND_INVALID', 'The request is too large.')
      const openFile = body.openFile === undefined ? undefined : asString(body.openFile, 'INVALID_PATH')
      return agent.start(prompt, openFile)
    }),
  )
  ipcMain.handle(channels.agentStop, (event) => guard(event.senderFrame, () => agent.stop()))
  ipcMain.handle(channels.agentDecide, (event, payload: unknown) =>
    guard(event.senderFrame, () => {
      const body = asObject(payload, 'COMMAND_INVALID')
      if (typeof body.approved !== 'boolean') throw new DesktopFailure('COMMAND_INVALID', 'The request was not valid.')
      agent.decide(body.approved)
    }),
  )

  return terminals
}

function asSessionId(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    throw new DesktopFailure('INVALID_SESSION', 'That terminal session is not valid.')
  }
  return value
}

function asCommand(value: unknown): string {
  const command = asString(value, 'COMMAND_INVALID')
  if (command.length > 8000) throw new DesktopFailure('COMMAND_INVALID', 'The command is not valid.')
  return command
}

export function activeWorkspacePath(): string | null {
  return getWorkspacePath()
}
