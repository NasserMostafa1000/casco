import type { CascoDesktopApi, Result } from '../shared/types'

function unwrap<T>(result: Result<T>): T {
  if (!result.ok) {
    const error = new Error(result.error.message) as Error & { code: string }
    error.code = result.error.code
    throw error
  }
  return result.data
}

async function call<T>(value: Promise<T>): Promise<T> {
  return unwrap((await value) as unknown as Result<T>)
}

export function installDesktopApi(): void {
  const raw = window.cascoRaw
  window.cascoDesktop = {
    ping: () => call(raw.ping()),
    workspace: {
      select: () => call(raw.workspace.select()),
      current: () => call(raw.workspace.current()),
      onChanged: (callback) => raw.workspace.onChanged(callback),
      onFilesChanged: (callback) => raw.workspace.onFilesChanged(callback),
    },
    fs: {
      readFile: (filePath) => call(raw.fs.readFile(filePath)),
      writeFile: (filePath, content) => call(raw.fs.writeFile(filePath, content)),
      createFile: (filePath, content) => call(raw.fs.createFile(filePath, content)),
      createDirectory: (dirPath) => call(raw.fs.createDirectory(dirPath)),
      deleteFile: (filePath) => call(raw.fs.deleteFile(filePath)),
      deleteDirectory: (dirPath) => call(raw.fs.deleteDirectory(dirPath)),
      renameFile: (oldPath, newPath) => call(raw.fs.renameFile(oldPath, newPath)),
      renameDirectory: (oldPath, newPath) => call(raw.fs.renameDirectory(oldPath, newPath)),
      readDirectory: (dirPath) => call(raw.fs.readDirectory(dirPath)),
      searchFiles: (query) => call(raw.fs.searchFiles(query)),
    },
    terminal: {
      startSession: (options) => call(raw.terminal.startSession(options)),
      execute: (sessionId, command) => call(raw.terminal.execute(sessionId, command)),
      write: (sessionId, input) => call(raw.terminal.write(sessionId, input)),
      stop: (sessionId) => call(raw.terminal.stop(sessionId)),
      dispose: (sessionId) => call(raw.terminal.dispose(sessionId)),
      onOutput: (callback) => raw.terminal.onOutput(callback),
      onState: (callback) => raw.terminal.onState(callback),
    },
    agent: {
      start: (options) => call(raw.agent.start(options)),
      stop: () => call(raw.agent.stop()),
      decide: (approved) => call(raw.agent.decide(approved)),
      onEvent: (callback) => raw.agent.onEvent(callback),
    },
  }
}

declare global {
  interface Window {
    cascoRaw: CascoDesktopApi
  }
}
