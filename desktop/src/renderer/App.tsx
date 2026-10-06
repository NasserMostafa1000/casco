import { useEffect, useState } from 'react'
import type { WorkspaceInfo } from '../shared/types'
import { EditorPane } from './editor/EditorPane'
import { Explorer } from './explorer/Explorer'
import { AgentPanel } from './agent/AgentPanel'
import { PreviewPane } from './preview/PreviewPane'
import { TerminalPanel } from './terminal/TerminalPanel'
import logo from './logo-mark.png'

type Theme = 'dark' | 'light'

export function App() {
  const [theme, setTheme] = useState<Theme>(() => (localStorage.getItem('casco_desktop_theme') === 'light' ? 'light' : 'dark'))
  const [workspace, setWorkspace] = useState<WorkspaceInfo | null>(null)
  const [filePath, setFilePath] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const [mainTab, setMainTab] = useState<'editor' | 'preview'>('editor')

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    localStorage.setItem('casco_desktop_theme', theme)
  }, [theme])

  useEffect(() => {
    void window.cascoDesktop.workspace.current().then(setWorkspace)
    const offWorkspace = window.cascoDesktop.workspace.onChanged((next) => {
      setWorkspace(next)
      setFilePath(null)
      setRefreshKey((value) => value + 1)
    })
    const offFiles = window.cascoDesktop.workspace.onFilesChanged(() => setRefreshKey((value) => value + 1))
    return () => {
      offWorkspace()
      offFiles()
    }
  }, [])

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand"><img src={logo} alt="" />Casco Studio</div>
        <div className="project">{workspace ? workspace.name : 'No project'}</div>
        <div className="icon-row">
          <button type="button" onClick={() => void window.cascoDesktop.workspace.select()}>Open folder</button>
          <button type="button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>{theme === 'dark' ? 'Light' : 'Dark'}</button>
        </div>
      </header>
      <div className="workspace">
        {workspace ? (
          <Explorer root={workspace.path} refreshKey={refreshKey} selected={filePath} onOpen={(path) => { setFilePath(path); setMainTab('editor') }} />
        ) : (
          <aside className="explorer">
            <div className="empty">
              <p>Open a project folder on this computer.</p>
              <button type="button" onClick={() => void window.cascoDesktop.workspace.select()}>Open folder</button>
            </div>
          </aside>
        )}
        <main className="main">
          <div className="tabs">
            <button type="button" className={mainTab === 'editor' ? 'active' : ''} onClick={() => setMainTab('editor')}>Editor</button>
            <button type="button" className={mainTab === 'preview' ? 'active' : ''} onClick={() => setMainTab('preview')}>Preview</button>
          </div>
          {mainTab === 'editor' ? (
            <EditorPane filePath={filePath} theme={theme} onSaved={() => setRefreshKey((value) => value + 1)} />
          ) : (
            <PreviewPane />
          )}
        </main>
        <AgentPanel workspace={workspace?.path ?? null} openFile={filePath} />
      </div>
      <TerminalPanel workspace={workspace?.path ?? null} />
    </div>
  )
}
