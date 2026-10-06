import fs from 'node:fs'
import type { FSWatcher } from 'node:fs'

let watcher: FSWatcher | null = null
let timer: NodeJS.Timeout | null = null

export function watchWorkspace(root: string, onChange: () => void): void {
  stopWatching()
  try {
    watcher = fs.watch(root, { recursive: true }, () => {
      if (timer) clearTimeout(timer)
      timer = setTimeout(onChange, 200)
    })
    watcher.on('error', () => undefined)
  } catch {
    watcher = null
  }
}

export function stopWatching(): void {
  if (timer) clearTimeout(timer)
  timer = null
  watcher?.close()
  watcher = null
}
