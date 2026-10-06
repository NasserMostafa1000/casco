import { apiUrl, tokenStore } from './api'

export type TaskStreamEvent =
  | { type: 'snapshot'; text?: string; attempt?: number; part?: number; status?: string }
  | { type: 'delta'; text: string }
  | { type: 'thinking'; text?: string }
  | { type: 'attempt'; attempt: number; part?: number }
  | { type: 'reset' }
  | { type: 'done'; status: string }

/** Follows the live model output of a task (server-sent events over fetch, so the auth header can be sent). */
export async function followTask(taskId: string, onEvent: (e: TaskStreamEvent) => void, signal: AbortSignal) {
  const res = await fetch(apiUrl(`/api/tasks/${taskId}/stream`), {
    headers: { Authorization: `Bearer ${tokenStore.get() ?? ''}`, Accept: 'text/event-stream' },
    signal,
  })
  if (!res.ok || !res.body) throw new Error(`stream ${res.status}`)
  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })
    let sep: number
    while ((sep = buffer.indexOf('\n\n')) >= 0) {
      const chunk = buffer.slice(0, sep)
      buffer = buffer.slice(sep + 2)
      for (const line of chunk.split('\n')) {
        if (!line.startsWith('data:')) continue
        try {
          onEvent(JSON.parse(line.slice(5).trim()) as TaskStreamEvent)
        } catch {
          /* ignore malformed event */
        }
      }
    }
  }
}
