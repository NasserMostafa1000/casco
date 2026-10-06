import { FormEvent, useEffect, useState } from 'react'
import type { AgentChange, AgentEvent } from '../../shared/types'

export function AgentPanel(props: { workspace: string | null; openFile: string | null }) {
  const [prompt, setPrompt] = useState('')
  const [lines, setLines] = useState<AgentEvent[]>([])
  const [running, setRunning] = useState(false)
  const [approval, setApproval] = useState<string | null>(null)
  const [changes, setChanges] = useState<AgentChange[]>([])

  useEffect(() => {
    return window.cascoDesktop.agent.onEvent((event) => {
      setLines((current) => [...current, event].slice(-80))
      if (event.kind === 'approval') setApproval(event.command ?? event.text)
      if (event.kind === 'done') {
        setRunning(false)
        setApproval(null)
        setChanges(event.changes ?? [])
      }
    })
  }, [])

  async function send(event: FormEvent) {
    event.preventDefault()
    const text = prompt.trim()
    if (!text || !props.workspace || running) return
    setPrompt('')
    setLines([])
    setChanges([])
    setApproval(null)
    setRunning(true)
    try {
      await window.cascoDesktop.agent.start({ prompt: text, openFile: props.openFile ?? undefined })
    } catch (error) {
      setRunning(false)
      setLines([{ taskId: '', kind: 'error', text: error instanceof Error ? error.message : 'The agent could not start.' }])
    }
  }

  return (
    <aside className="agent">
      <div className="panel-head">
        <strong>Casco Agent</strong>
        <button type="button" data-testid="agent-stop" disabled={!running} onClick={() => void window.cascoDesktop.agent.stop()}>Stop</button>
      </div>
      <div className="agent-log" data-testid="agent-log">
        {lines.map((line, index) => (
          <p key={`${line.taskId}-${index}`} className={line.kind === 'error' ? 'agent-error' : undefined}>{line.text}</p>
        ))}
        {changes.length > 0 && (
          <div className="changes">
            {changes.map((change) => (
              <div key={`${change.kind}-${change.path}`}>{change.kind}: {change.path}</div>
            ))}
          </div>
        )}
      </div>
      {approval && (
        <div className="agent-approval">
          <p>Allow this command?</p>
          <p>{approval}</p>
          <button type="button" data-testid="agent-allow" onClick={() => void window.cascoDesktop.agent.decide(true)}>Allow</button>
          <button type="button" data-testid="agent-reject" onClick={() => void window.cascoDesktop.agent.decide(false)}>Reject</button>
        </div>
      )}
      <form className="agent-form" onSubmit={(event) => void send(event)}>
        <input value={prompt} placeholder={props.workspace ? 'Ask Casco...' : 'Open a folder first'} disabled={!props.workspace || running} onChange={(event) => setPrompt(event.target.value)} />
        <button type="submit" data-testid="agent-send" disabled={!props.workspace || running || !prompt.trim()}>Send</button>
      </form>
    </aside>
  )
}
