import { useEffect, useRef, useState } from 'react'
import Editor, { type OnMount } from '@monaco-editor/react'
import type { editor } from 'monaco-editor'

type Props = {
  filePath: string | null
  theme: 'light' | 'dark'
  onSaved: () => void
}

export function EditorPane({ filePath, theme, onSaved }: Props) {
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null)
  const saveRef = useRef<() => void>(() => undefined)
  const [original, setOriginal] = useState('')
  const [draft, setDraft] = useState('')
  const [message, setMessage] = useState('')

  useEffect(() => {
    let cancelled = false
    if (!filePath) return
    setMessage('')
    window.cascoDesktop.fs.readFile(filePath).then((text) => {
      if (cancelled) return
      setOriginal(text)
      setDraft(text)
      editorRef.current?.setValue(text)
    }).catch((error: Error) => {
      if (!cancelled) setMessage(error.message)
    })
    return () => {
      cancelled = true
    }
  }, [filePath])

  async function save() {
    if (!filePath) return
    const value = draft
    try {
      await window.cascoDesktop.fs.writeFile(filePath, value)
      setOriginal(value)
      setMessage('')
      onSaved()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Save failed.')
    }
  }

  saveRef.current = () => void save()
  const dirty = Boolean(filePath) && draft !== original
  const name = filePath ? filePath.split(/[/\\]/).pop() : 'No file'

  const onMount: OnMount = (instance, monaco) => {
    editorRef.current = instance
    instance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current())
  }

  return (
    <section className="editor-pane">
      <div className="panel-head">
        <strong data-editor-file={filePath ?? ''}>{name}{dirty ? ' •' : ''}</strong>
        <button type="button" data-testid="save-file" data-draft={draft} disabled={!filePath} onClick={() => void save()}>Save</button>
      </div>
      {message && <p className="hint">{message}</p>}
      {filePath ? (
        <div className="editor-surface">
          <Editor
            key={filePath}
            height="100%"
            language={languageFor(filePath)}
            theme={theme === 'light' ? 'vs' : 'vs-dark'}
            value={draft}
            onChange={(value) => setDraft(value ?? '')}
            onMount={onMount}
            options={{
              fontSize: 13,
              minimap: { enabled: false },
              wordWrap: 'on',
              automaticLayout: true,
              scrollBeyondLastLine: false,
            }}
          />
        </div>
      ) : (
        <div className="empty">Open a file from the explorer.</div>
      )}
    </section>
  )
}

function languageFor(filePath: string): string {
  const ext = filePath.split('.').pop()?.toLowerCase()
  switch (ext) {
    case 'ts':
    case 'tsx':
      return 'typescript'
    case 'js':
    case 'jsx':
      return 'javascript'
    case 'json':
      return 'json'
    case 'css':
      return 'css'
    case 'html':
      return 'html'
    case 'md':
      return 'markdown'
    case 'py':
      return 'python'
    case 'yml':
    case 'yaml':
      return 'yaml'
    case 'xml':
    case 'svg':
      return 'xml'
    case 'sh':
      return 'shell'
    case 'ps1':
      return 'powershell'
    default:
      return 'plaintext'
  }
}
