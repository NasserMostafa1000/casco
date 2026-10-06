import fs from 'node:fs'
import path from 'node:path'
import { DesktopFailure } from '../errors'

function isInside(root: string, target: string): boolean {
  const relative = path.relative(root, target)
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))
}

async function nearestExisting(start: string): Promise<{ existing: string; rest: string[] }> {
  const rest: string[] = []
  let current = path.resolve(start)
  while (!fs.existsSync(current)) {
    const base = path.basename(current)
    const parent = path.dirname(current)
    if (!base || parent === current) throw new DesktopFailure('NOT_FOUND', 'That folder was not found.')
    rest.unshift(base)
    current = parent
  }
  return { existing: await fs.promises.realpath(current), rest }
}

/** Resolve a renderer-supplied path and prove it stays inside the workspace. */
export async function resolveInsideWorkspace(root: string, input: string): Promise<string> {
  if (typeof root !== 'string' || !root.trim()) {
    throw new DesktopFailure('WORKSPACE_NOT_SELECTED', 'Open a project folder first.')
  }
  if (typeof input !== 'string' || !input.trim() || input.includes('\0') || input.length > 1024) {
    throw new DesktopFailure('INVALID_PATH', 'That path is not valid.')
  }

  const base = await fs.promises.realpath(path.resolve(root))
  const absolute = path.isAbsolute(input) ? path.resolve(input) : path.resolve(base, input)
  if (fs.existsSync(absolute)) {
    const real = await fs.promises.realpath(absolute)
    if (!isInside(base, real)) throw new DesktopFailure('INVALID_WORKSPACE', 'That path is outside the project.')
    return real
  }

  const parent = path.dirname(absolute)
  const { existing, rest } = await nearestExisting(parent)
  if (!isInside(base, existing)) throw new DesktopFailure('INVALID_WORKSPACE', 'That path is outside the project.')
  const full = path.join(existing, ...rest, path.basename(absolute))
  if (!isInside(base, full) || path.relative(base, full).includes(':')) {
    throw new DesktopFailure('INVALID_WORKSPACE', 'That path is outside the project.')
  }
  return full
}
