import { useEffect, useState } from 'react'
import type { DirEntry, SearchHit } from '../../shared/types'

type Props = {
  root: string
  refreshKey: number
  selected: string | null
  onOpen: (filePath: string) => void
}

export function Explorer({ root, refreshKey, selected, onOpen }: Props) {
  const [entries, setEntries] = useState<DirEntry[]>([])
  const [expanded, setExpanded] = useState<Record<string, DirEntry[]>>({})
  const [openDirs, setOpenDirs] = useState<Record<string, boolean>>({})
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<SearchHit[] | null>(null)
  const [draft, setDraft] = useState('')
  const [mode, setMode] = useState<'file' | 'dir' | 'rename' | null>(null)
  const [target, setTarget] = useState<DirEntry | null>(null)
  const [message, setMessage] = useState('')

  async function loadRoot() {
    setEntries(await window.cascoDesktop.fs.readDirectory(root))
  }

  useEffect(() => {
    let cancelled = false
    setExpanded({})
    setOpenDirs({})
    window.cascoDesktop.fs.readDirectory(root).then((rows) => {
      if (!cancelled) setEntries(rows)
    }).catch((error: Error) => setMessage(error.message))
    return () => {
      cancelled = true
    }
  }, [root, refreshKey])

  async function toggle(entry: DirEntry) {
    const next = !openDirs[entry.path]
    setOpenDirs((current) => ({ ...current, [entry.path]: next }))
    if (next && !expanded[entry.path]) {
      const rows = await window.cascoDesktop.fs.readDirectory(entry.path)
      setExpanded((current) => ({ ...current, [entry.path]: rows }))
    }
  }

  async function search(value: string) {
    setQuery(value)
    if (!value.trim()) {
      setHits(null)
      return
    }
    setHits(await window.cascoDesktop.fs.searchFiles(value.trim()))
  }

  function parentOf(entry: DirEntry | null): string {
    if (!entry) return root
    const slash = Math.max(entry.path.lastIndexOf('/'), entry.path.lastIndexOf('\\'))
    return entry.path.slice(0, slash) || root
  }

  async function commit() {
    const name = draft.trim()
    if (!name || name.includes('/') || name.includes('\\') || name.includes('\0')) {
      setMessage('Use a file name without a folder slash.')
      return
    }
    try {
      const folder = mode === 'rename' ? parentOf(target) : parentOf(target?.kind === 'directory' ? target : null)
      const base = mode === 'file' || mode === 'dir' ? (target?.kind === 'directory' ? target.path : folder) : folder
      const next = joinPath(mode === 'rename' ? folder : base, name)
      if (mode === 'file') await window.cascoDesktop.fs.createFile(next, '')
      if (mode === 'dir') await window.cascoDesktop.fs.createDirectory(next)
      if (mode === 'rename' && target) {
        if (target.kind === 'directory') await window.cascoDesktop.fs.renameDirectory(target.path, next)
        else await window.cascoDesktop.fs.renameFile(target.path, next)
      }
      setMode(null)
      setDraft('')
      setMessage('')
      await loadRoot()
      setExpanded({})
      setOpenDirs({})
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'The change failed.')
    }
  }

  async function removeSelected() {
    if (!target) return
    if (!window.confirm(`Delete ${target.name}?`)) return
    try {
      if (target.kind === 'directory') await window.cascoDesktop.fs.deleteDirectory(target.path)
      else await window.cascoDesktop.fs.deleteFile(target.path)
      setTarget(null)
      await loadRoot()
      setExpanded({})
      setOpenDirs({})
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'The delete failed.')
    }
  }

  return (
    <aside className="explorer">
      <div className="panel-head">
        <strong>Explorer</strong>
        <div className="icon-row">
          <button type="button" onClick={() => { setMode('file'); setDraft('') }}>File</button>
          <button type="button" onClick={() => { setMode('dir'); setDraft('') }}>Folder</button>
          <button type="button" disabled={!target} onClick={() => { setMode('rename'); setDraft(target?.name ?? '') }}>Rename</button>
          <button type="button" disabled={!target} onClick={() => void removeSelected()}>Delete</button>
        </div>
      </div>
      <input
        className="search"
        value={query}
        placeholder="Search files"
        onChange={(event) => void search(event.target.value)}
      />
      {mode && (
        <form className="inline-form" onSubmit={(event) => { event.preventDefault(); void commit() }}>
          <input value={draft} autoFocus onChange={(event) => setDraft(event.target.value)} placeholder={mode === 'dir' ? 'Folder name' : 'File name'} />
          <button type="submit">OK</button>
        </form>
      )}
      {message && <p className="hint">{message}</p>}
      <div className="tree">
        {hits ? (
          hits.length === 0 ? <p className="hint">No matches.</p> : hits.map((hit) => (
            <button key={`${hit.path}:${hit.line ?? 0}`} type="button" className="tree-row" data-path={hit.path} onClick={() => onOpen(hit.path)}>
              <span>{hit.name}{hit.line ? `:${hit.line}` : ''}</span>
              {hit.preview && <small>{hit.preview}</small>}
            </button>
          ))
        ) : (
          <Branch
            entries={entries}
            expanded={expanded}
            openDirs={openDirs}
            selected={selected}
            target={target?.path ?? null}
            depth={0}
            onToggle={(entry) => void toggle(entry)}
            onSelect={(entry) => {
              setTarget(entry)
              if (entry.kind === 'file') onOpen(entry.path)
            }}
          />
        )}
      </div>
    </aside>
  )
}

function Branch(props: {
  entries: DirEntry[]
  expanded: Record<string, DirEntry[]>
  openDirs: Record<string, boolean>
  selected: string | null
  target: string | null
  depth: number
  onToggle: (entry: DirEntry) => void
  onSelect: (entry: DirEntry) => void
}) {
  return (
    <>
      {props.entries.map((entry) => (
        <div key={entry.path}>
          <button
            type="button"
            className={entry.path === props.selected || entry.path === props.target ? 'tree-row active' : 'tree-row'}
            style={{ paddingLeft: 8 + props.depth * 14 }}
            data-path={entry.path}
            data-kind={entry.kind}
            onClick={() => {
              props.onSelect(entry)
              if (entry.kind === 'directory') props.onToggle(entry)
            }}
          >
            <span>{entry.kind === 'directory' ? (props.openDirs[entry.path] ? '▾' : '▸') : '·'} {entry.name}</span>
          </button>
          {entry.kind === 'directory' && props.openDirs[entry.path] && (
            <Branch {...props} entries={props.expanded[entry.path] ?? []} depth={props.depth + 1} />
          )}
        </div>
      ))}
    </>
  )
}

function joinPath(dir: string, name: string): string {
  if (dir.endsWith('/') || dir.endsWith('\\')) return dir + name
  const slash = dir.includes('\\') ? '\\' : '/'
  return `${dir}${slash}${name}`
}
