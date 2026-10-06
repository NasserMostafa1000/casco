import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { TerminalState } from '../../shared/types'

type Line = { stream: 'stdout' | 'stderr' | 'meta'; text: string }

const OUTPUT_LIMIT = 200_000

export function TerminalPanel({ workspace }: { workspace: string | null }) {
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [shell, setShell] = useState('')
  const [lines, setLines] = useState<Line[]>([])
  const [command, setCommand] = useState('')
  const [state, setState] = useState<TerminalState | 'idle'>('idle')
  const [exitCode, setExitCode] = useState<number | null>(null)
  const [message, setMessage] = useState('')
  const scroller = useRef<HTMLDivElement | null>(null)
  const sessionRef = useRef<string | null>(null)

  function push(line: Line) {
    setLines((current) => {
      const next = [...current, line]
      const text = next.map((item) => item.text).join('')
      if (text.length <= OUTPUT_LIMIT) return next
      return [{ stream: 'meta', text: text.slice(text.length - OUTPUT_LIMIT) }]
    })
  }

  useEffect(() => {
    scroller.current?.scrollTo(0, scroller.current.scrollHeight)
  }, [lines])

  useEffect(() => {
    const offOutput = window.cascoDesktop.terminal.onOutput((event) => {
      if (event.sessionId !== sessionRef.current) return
      push({ stream: event.stream, text: event.data })
    })
    const offState = window.cascoDesktop.terminal.onState((event) => {
      if (event.sessionId !== sessionRef.current) return
      setState(event.state)
      if (event.exitCode !== undefined && event.exitCode !== null && event.state !== 'starting' && event.state !== 'running') {
        setExitCode(event.exitCode)
        push({ stream: 'meta', text: `\nExit code: ${event.exitCode}\n` })
      }
      if (event.state === 'stopped') push({ stream: 'meta', text: 'Stopped.\n' })
    })
    return () => {
      offOutput()
      offState()
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const previous = sessionRef.current
    sessionRef.current = null
    setSessionId(null)
    if (previous) void window.cascoDesktop.terminal.dispose(previous).catch(() => undefined)
    if (!workspace) return
    window.cascoDesktop.terminal.startSession({ cwd: workspace }).then((info) => {
      if (cancelled) {
        void window.cascoDesktop.terminal.dispose(info.sessionId).catch(() => undefined)
        return
      }
      sessionRef.current = info.sessionId
      setSessionId(info.sessionId)
      setShell(info.shell)
      setMessage('')
    }).catch((error: Error) => setMessage(error.message))
    return () => {
      cancelled = true
    }
  }, [workspace])

  async function run(event: FormEvent) {
    event.preventDefault()
    if (!sessionId || !command.trim()) return
    const text = command
    setCommand('')
    try {
      if (state === 'running' || state === 'starting') {
        await window.cascoDesktop.terminal.write(sessionId, `${text}\n`)
        return
      }
      push({ stream: 'meta', text: `\n> ${text}\n` })
      setExitCode(null)
      await window.cascoDesktop.terminal.execute(sessionId, text)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'The command failed.')
    }
  }

  const running = state === 'running' || state === 'starting'

  return (
    <section className="terminal">
      <div className="panel-head">
        <strong>Terminal{shell ? ` · ${shell}` : ''}</strong>
        <div className="icon-row">
          <span className={running ? 'badge on' : 'badge'}>{running ? 'Running' : 'Idle'}</span>
          {exitCode !== null && <span className="badge">Exit {exitCode}</span>}
          <button type="button" onClick={() => setLines([])}>Clear</button>
          <button type="button" data-testid="terminal-stop" disabled={!running || !sessionId} onClick={() => sessionId && void window.cascoDesktop.terminal.stop(sessionId)}>Stop</button>
        </div>
      </div>
      {message && <p className="hint">{message}</p>}
      <div className="terminal-output" ref={scroller} data-testid="terminal-output">
        {workspace ? lines.map((line, index) => (
          <span key={index} className={line.stream}>{line.text}</span>
        )) : <span className="meta">Open a project folder to use the terminal.</span>}
        {lines.length === 0 && workspace && <span className="meta">The shell starts in the project folder. This is a real local shell.</span>}
      </div>
      <form className="terminal-form" onSubmit={(event) => void run(event)}>
        <span>&gt;</span>
        <input value={command} disabled={!sessionId} onChange={(event) => setCommand(event.target.value)} placeholder="Command" />
      </form>
    </section>
  )
}
