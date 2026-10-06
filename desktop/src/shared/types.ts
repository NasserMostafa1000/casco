export type DesktopError = {
  code: string
  message: string
}

export type Result<T> = { ok: true; data: T } | { ok: false; error: DesktopError }

export type WorkspaceInfo = {
  path: string
  name: string
}

export type DirEntry = {
  name: string
  path: string
  kind: 'file' | 'directory'
}

export type SearchHit = {
  path: string
  name: string
  line?: number
  preview?: string
}

export type TerminalOutputEvent = {
  sessionId: string
  commandId?: string
  stream: 'stdout' | 'stderr'
  data: string
  timestamp: number
}

export type TerminalState = 'starting' | 'running' | 'completed' | 'failed' | 'stopped'

export type TerminalStateEvent = {
  sessionId: string
  commandId?: string
  state: TerminalState
  exitCode?: number | null
  signal?: string | null
  timestamp: number
}

export type AgentChange = {
  path: string
  kind: 'created' | 'modified' | 'deleted' | 'renamed'
}

export type AgentEvent = {
  taskId: string
  kind: 'status' | 'tool' | 'change' | 'approval' | 'done' | 'error'
  text: string
  tool?: string
  command?: string
  exitCode?: number | null
  ok?: boolean
  changes?: AgentChange[]
}

export type TerminalSessionInfo = {
  sessionId: string
  cwd: string
  shell: string
}

export type CascoDesktopApi = {
  ping: () => Promise<{ ok: true; name: string }>
  workspace: {
    select: () => Promise<WorkspaceInfo | null>
    current: () => Promise<WorkspaceInfo | null>
    onChanged: (callback: (workspace: WorkspaceInfo | null) => void) => () => void
    onFilesChanged: (callback: () => void) => () => void
  }
  fs: {
    readFile: (filePath: string) => Promise<string>
    writeFile: (filePath: string, content: string) => Promise<void>
    createFile: (filePath: string, content: string) => Promise<void>
    createDirectory: (dirPath: string) => Promise<void>
    deleteFile: (filePath: string) => Promise<void>
    deleteDirectory: (dirPath: string) => Promise<void>
    renameFile: (oldPath: string, newPath: string) => Promise<void>
    renameDirectory: (oldPath: string, newPath: string) => Promise<void>
    readDirectory: (dirPath: string) => Promise<DirEntry[]>
    searchFiles: (query: string) => Promise<SearchHit[]>
  }
  terminal: {
    startSession: (options?: { cwd?: string }) => Promise<TerminalSessionInfo>
    execute: (sessionId: string, command: string) => Promise<{ commandId: string }>
    write: (sessionId: string, input: string) => Promise<void>
    stop: (sessionId: string) => Promise<void>
    dispose: (sessionId: string) => Promise<void>
    onOutput: (callback: (event: TerminalOutputEvent) => void) => () => void
    onState: (callback: (event: TerminalStateEvent) => void) => () => void
  }
  agent: {
    start: (options: { prompt: string; openFile?: string }) => Promise<{ taskId: string }>
    stop: () => Promise<void>
    decide: (approved: boolean) => Promise<void>
    onEvent: (callback: (event: AgentEvent) => void) => () => void
  }
}
