import fs from 'node:fs'
import path from 'node:path'
import { DesktopFailure } from '../errors'
import { resolveInsideWorkspace } from './paths'
import { requireWorkspacePath } from '../workspace/store'
import type { DirEntry, SearchHit } from '../../shared/types'

const MAX_TEXT = 2_000_000
const SEARCH_MAX_FILES = 4000
const SEARCH_MAX_HITS = 80
const SEARCH_MAX_BYTES = 150_000
const IGNORE = new Set(['node_modules', '.git', 'dist', 'dist-electron', 'dist-renderer', 'release'])

function assertTextSize(content: string): void {
  if (content.length > MAX_TEXT) throw new DesktopFailure('INVALID_PATH', 'That file is too large.')
}

async function assertFile(target: string): Promise<void> {
  const stat = await statOrMissing(target, 'That file was not found.')
  if (!stat.isFile()) throw new DesktopFailure('NOT_A_FILE', 'That is not a file.')
}

async function assertDirectory(target: string): Promise<void> {
  const stat = await statOrMissing(target, 'That folder was not found.')
  if (!stat.isDirectory()) throw new DesktopFailure('NOT_A_DIRECTORY', 'That is not a folder.')
}

async function statOrMissing(target: string, message: string): Promise<fs.Stats> {
  try {
    return await fs.promises.stat(target)
  } catch (error) {
    const code = error && typeof error === 'object' && 'code' in error ? (error as { code?: string }).code : ''
    if (code === 'ENOENT') throw new DesktopFailure('NOT_FOUND', message)
    throw error
  }
}

export async function readFile(filePath: string): Promise<string> {
  const target = await resolveInsideWorkspace(requireWorkspacePath(), filePath)
  await assertFile(target)
  const stat = await fs.promises.stat(target)
  if (stat.size > MAX_TEXT) throw new DesktopFailure('INVALID_PATH', 'That file is too large.')
  return fs.promises.readFile(target, 'utf8')
}

export async function writeFile(filePath: string, content: string): Promise<void> {
  assertTextSize(content)
  const target = await resolveInsideWorkspace(requireWorkspacePath(), filePath)
  if (fs.existsSync(target)) await assertFile(target)
  else {
    const parent = path.dirname(target)
    if (!fs.existsSync(parent)) throw new DesktopFailure('NOT_FOUND', 'That folder was not found.')
  }
  await fs.promises.writeFile(target, content, 'utf8')
}

export async function createFile(filePath: string, content: string): Promise<void> {
  assertTextSize(content)
  const target = await resolveInsideWorkspace(requireWorkspacePath(), filePath)
  if (fs.existsSync(target)) throw new DesktopFailure('ALREADY_EXISTS', 'That file already exists.')
  const parent = path.dirname(target)
  if (!fs.existsSync(parent)) throw new DesktopFailure('NOT_FOUND', 'That folder was not found.')
  await fs.promises.writeFile(target, content, 'utf8')
}

export async function createDirectory(dirPath: string): Promise<void> {
  const target = await resolveInsideWorkspace(requireWorkspacePath(), dirPath)
  if (fs.existsSync(target)) throw new DesktopFailure('ALREADY_EXISTS', 'That folder already exists.')
  await fs.promises.mkdir(target, { recursive: true })
}

export async function deleteFile(filePath: string): Promise<void> {
  const target = await resolveInsideWorkspace(requireWorkspacePath(), filePath)
  await assertFile(target)
  await fs.promises.unlink(target)
}

export async function deleteDirectory(dirPath: string): Promise<void> {
  const root = requireWorkspacePath()
  const target = await resolveInsideWorkspace(root, dirPath)
  if (target === root) throw new DesktopFailure('INVALID_PATH', 'The project folder cannot be deleted.')
  await assertDirectory(target)
  await fs.promises.rm(target, { recursive: true, force: false })
}

export async function renamePath(oldPath: string, newPath: string, kind: 'file' | 'directory'): Promise<void> {
  const root = requireWorkspacePath()
  const from = await resolveInsideWorkspace(root, oldPath)
  const to = await resolveInsideWorkspace(root, newPath)
  if (from === root) throw new DesktopFailure('INVALID_PATH', 'The project folder cannot be renamed from here.')
  if (kind === 'file') await assertFile(from)
  else await assertDirectory(from)
  if (fs.existsSync(to)) throw new DesktopFailure('ALREADY_EXISTS', 'That name is already used.')
  await fs.promises.rename(from, to)
}

export async function readDirectory(dirPath: string): Promise<DirEntry[]> {
  const target = await resolveInsideWorkspace(requireWorkspacePath(), dirPath)
  await assertDirectory(target)
  const entries = await fs.promises.readdir(target, { withFileTypes: true })
  return entries
    .filter((entry) => entry.name !== '.' && entry.name !== '..')
    .map((entry) => ({
      name: entry.name,
      path: path.join(target, entry.name),
      kind: entry.isDirectory() ? ('directory' as const) : ('file' as const),
    }))
    .sort((a, b) => {
      if (a.kind !== b.kind) return a.kind === 'directory' ? -1 : 1
      return a.name.localeCompare(b.name)
    })
}

function looksBinary(buffer: Buffer): boolean {
  return buffer.subarray(0, 8000).includes(0)
}

export async function searchFiles(query: string): Promise<SearchHit[]> {
  const needle = query.trim().toLowerCase()
  if (!needle || needle.length > 200) throw new DesktopFailure('INVALID_PATH', 'The search text is not valid.')
  const root = requireWorkspacePath()
  const hits: SearchHit[] = []
  const queue = [root]
  let seen = 0

  while (queue.length > 0 && hits.length < SEARCH_MAX_HITS && seen < SEARCH_MAX_FILES) {
    const dir = queue.shift()!
    let entries: fs.Dirent[]
    try {
      entries = await fs.promises.readdir(dir, { withFileTypes: true })
    } catch {
      continue
    }
    for (const entry of entries) {
      if (hits.length >= SEARCH_MAX_HITS || seen >= SEARCH_MAX_FILES) break
      if (IGNORE.has(entry.name)) continue
      const full = path.join(dir, entry.name)
      if (entry.isDirectory()) {
        if (entry.name.toLowerCase().includes(needle)) {
          hits.push({ path: full, name: entry.name })
        }
        queue.push(full)
        continue
      }
      if (!entry.isFile()) continue
      seen += 1
      const nameHit = entry.name.toLowerCase().includes(needle)
      if (nameHit) hits.push({ path: full, name: entry.name })
      if (hits.length >= SEARCH_MAX_HITS) break
      let stat: fs.Stats
      try {
        stat = await fs.promises.stat(full)
      } catch {
        continue
      }
      if (!stat.isFile() || stat.size > SEARCH_MAX_BYTES) continue
      const buffer = await fs.promises.readFile(full)
      if (looksBinary(buffer)) continue
      const lines = buffer.toString('utf8').split(/\r?\n/)
      for (let index = 0; index < lines.length; index += 1) {
        if (!lines[index].toLowerCase().includes(needle)) continue
        if (hits.length >= SEARCH_MAX_HITS) break
        hits.push({
          path: full,
          name: entry.name,
          line: index + 1,
          preview: lines[index].trim().slice(0, 180),
        })
        break
      }
    }
  }
  return hits
}
