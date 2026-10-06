import fs from 'node:fs'
import path from 'node:path'
import { DesktopFailure } from '../errors'
import type { WorkspaceInfo } from '../../shared/types'

let root: string | null = null
const listeners = new Set<() => void>()

export function getWorkspacePath(): string | null {
  return root
}

export function requireWorkspacePath(): string {
  if (!root) throw new DesktopFailure('WORKSPACE_NOT_SELECTED', 'Open a project folder first.')
  return root
}

export function workspaceInfo(): WorkspaceInfo | null {
  if (!root) return null
  return { path: root, name: path.basename(root) }
}

export function onWorkspaceChanged(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export async function setWorkspacePath(dir: string): Promise<WorkspaceInfo> {
  if (typeof dir !== 'string' || !dir.trim() || dir.includes('\0')) {
    throw new DesktopFailure('INVALID_WORKSPACE', 'That folder is not valid.')
  }
  const resolved = path.resolve(dir)
  if (!fs.existsSync(resolved)) throw new DesktopFailure('INVALID_WORKSPACE', 'That folder was not found.')
  const real = await fs.promises.realpath(resolved)
  const stat = await fs.promises.stat(real)
  if (!stat.isDirectory()) throw new DesktopFailure('INVALID_WORKSPACE', 'Choose a folder, not a file.')
  root = real
  for (const listener of listeners) listener()
  return workspaceInfo()!
}
