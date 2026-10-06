export type ShellSpec = {
  file: string
  label: string
  args: (command: string) => string[]
}

export function platformShell(): ShellSpec {
  if (process.platform === 'win32') {
    return {
      file: 'powershell.exe',
      label: 'powershell',
      args: (command) => ['-NoLogo', '-NoProfile', '-Command', command],
    }
  }
  if (process.platform === 'darwin') {
    return {
      file: '/bin/zsh',
      label: 'zsh',
      args: (command) => ['-c', command],
    }
  }
  return {
    file: '/bin/bash',
    label: 'bash',
    args: (command) => ['-c', command],
  }
}

const blockedEnv = [/^DATABASE_URL$/i, /^POSTGRES/i, /^ZIINA/i, /^CASCO_.*SECRET/i, /^CASCO_.*PASSWORD/i]

/** The user's environment, without Casco server secrets if they were inherited. */
export function terminalEnv(): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...process.env }
  for (const key of Object.keys(env)) {
    if (blockedEnv.some((pattern) => pattern.test(key))) delete env[key]
  }
  return env
}
