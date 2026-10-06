import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron'
import { channels } from './shared/channels'
import type {
  AgentEvent,
  CascoDesktopApi,
  DirEntry,
  Result,
  SearchHit,
  TerminalOutputEvent,
  TerminalSessionInfo,
  TerminalStateEvent,
  WorkspaceInfo,
} from './shared/types'

async function invoke<T>(channel: string, payload?: unknown): Promise<Result<T>> {
  return (await ipcRenderer.invoke(channel, payload)) as Result<T>
}

function subscribe<T>(channel: string, callback: (payload: T) => void): () => void {
  const listener = (_event: IpcRendererEvent, payload: T) => callback(payload)
  ipcRenderer.on(channel, listener)
  return () => ipcRenderer.removeListener(channel, listener)
}

const desktop: CascoDesktopApi = {
  ping: () => invoke(channels.ping),
  workspace: {
    select: () => invoke<WorkspaceInfo | null>(channels.workspaceSelect),
    current: () => invoke<WorkspaceInfo | null>(channels.workspaceCurrent),
    onChanged: (callback) => subscribe<WorkspaceInfo | null>(channels.workspaceChanged, callback),
    onFilesChanged: (callback) => subscribe(channels.workspaceFilesChanged, () => callback()),
  },
  fs: {
    readFile: (filePath) => invoke<string>(channels.fsReadFile, filePath),
    writeFile: (filePath, content) => invoke<void>(channels.fsWriteFile, { path: filePath, content }),
    createFile: (filePath, content) => invoke<void>(channels.fsCreateFile, { path: filePath, content }),
    createDirectory: (dirPath) => invoke<void>(channels.fsCreateDirectory, dirPath),
    deleteFile: (filePath) => invoke<void>(channels.fsDeleteFile, filePath),
    deleteDirectory: (dirPath) => invoke<void>(channels.fsDeleteDirectory, dirPath),
    renameFile: (oldPath, newPath) => invoke<void>(channels.fsRename, { oldPath, newPath, kind: 'file' }),
    renameDirectory: (oldPath, newPath) => invoke<void>(channels.fsRename, { oldPath, newPath, kind: 'directory' }),
    readDirectory: (dirPath) => invoke<DirEntry[]>(channels.fsReadDirectory, dirPath),
    searchFiles: (query) => invoke<SearchHit[]>(channels.fsSearch, query),
  },
  terminal: {
    startSession: (options) => invoke<TerminalSessionInfo>(channels.terminalStart, options ?? {}),
    execute: (sessionId, command) => invoke(channels.terminalExecute, { sessionId, command }),
    write: (sessionId, input) => invoke(channels.terminalWrite, { sessionId, input }),
    stop: (sessionId) => invoke(channels.terminalStop, { sessionId }),
    dispose: (sessionId) => invoke(channels.terminalDispose, { sessionId }),
    onOutput: (callback) => subscribe<TerminalOutputEvent>(channels.terminalOutput, callback),
    onState: (callback) => subscribe<TerminalStateEvent>(channels.terminalState, callback),
  },
  agent: {
    start: (options) => invoke(channels.agentStart, options),
    stop: () => invoke(channels.agentStop),
    decide: (approved) => invoke(channels.agentDecide, { approved }),
    onEvent: (callback) => subscribe<AgentEvent>(channels.agentEvent, callback),
  },
}

contextBridge.exposeInMainWorld('cascoRaw', desktop)
