import { useState } from 'react'
import type { ChatQuestion } from '../lib/api'
import { t } from '../lib/i18n'

export function PlanQuestions({
  questions,
  busy,
  onAnswer,
}: {
  questions: ChatQuestion[]
  busy: boolean
  onAnswer: (text: string) => void
}) {
  const [picked, setPicked] = useState<Record<number, string>>(() => {
    const init: Record<number, string> = {}
    questions.forEach((q, i) => {
      const recommended = (q.options ?? []).find((o) => o.recommended)
      if (recommended) init[i] = recommended.label
    })
    return init
  })
  const [custom, setCustom] = useState<Record<number, string>>({})

  const submit = () => {
    const lines = questions
      .map((q, i) => {
        const typed = (custom[i] ?? '').trim()
        const answer = typed || picked[i] || ''
        return answer ? `${q.prompt}: ${answer}` : ''
      })
      .filter(Boolean)
    if (lines.length === 0) return
    onAnswer(lines.join('\n'))
  }

  return (
    <div className="mt-2 w-full max-w-[95%] space-y-3 rounded-2xl border border-slate-200 bg-white p-3">
      {questions.map((q, i) => {
        const typed = (custom[i] ?? '').trim()
        return (
          <div key={`${q.prompt}-${i}`}>
            <p className="text-sm font-semibold text-ink">{q.prompt}</p>
            <div className="mt-2 flex flex-col gap-1.5">
              {(q.options ?? []).map((o) => {
                const selected = !typed && picked[i] === o.label
                return (
                  <button
                    key={o.label}
                    type="button"
                    onClick={() => {
                      setPicked((current) => ({ ...current, [i]: o.label }))
                      setCustom((current) => ({ ...current, [i]: '' }))
                    }}
                    className={`flex items-center justify-between gap-2 rounded-xl border px-3 py-2 text-start text-sm ${selected ? 'border-brand-500 bg-brand-50 text-ink' : 'border-slate-200 text-slate-700'}`}
                  >
                    <span>{o.label}</span>
                    {o.recommended && (
                      <span className="shrink-0 rounded-full bg-brand-600 px-2 py-0.5 text-[11px] font-semibold text-white">{t('مقترح')}</span>
                    )}
                  </button>
                )
              })}
              <input
                value={custom[i] ?? ''}
                onChange={(e) => setCustom((current) => ({ ...current, [i]: e.target.value }))}
                placeholder={t('أو اكتب ردك')}
                className="h-10 rounded-xl border border-slate-200 px-3 text-sm text-slate-900 outline-none focus:border-brand-400"
              />
            </div>
          </div>
        )
      })}
      <button type="button" disabled={busy} onClick={submit} className="h-10 w-full rounded-xl bg-ink text-sm font-semibold text-white disabled:opacity-50">
        {t('إرسال الإجابة')}
      </button>
    </div>
  )
}
