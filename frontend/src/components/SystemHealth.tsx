import { useCallback, useEffect, useState } from 'react'
import { Alert, Button, Card } from './ui'
import { errorMessage, get } from '../lib/api'

interface Health {
  windowMinutes: number
  process: { cpuPercent: number; cores: number; workingSetMb: number; gcHeapMb: number; threads: number; uptimeMinutes: number }
  agent: {
    workers: number
    running: number
    queued: number
    succeeded: number
    failed: number
    avgGenerationSeconds: number
    p90GenerationSeconds: number
    avgQueueWaitSeconds: number
    maxParts: number
  }
  ai: { calls: number; failures: number; rateLimited: number; avgLatencyMs: number; p90LatencyMs: number }
  database: { connections: number | null }
  disk: { freeGb: number; totalGb: number }
  credits: { activeHolds: number; heldCredits: number }
}

/** Live server health (last hour), refreshed every 10 seconds while open. */
export default function SystemHealth() {
  const [health, setHealth] = useState<Health | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    get<Health>('/api/admin/system?minutes=60')
      .then((h) => {
        setHealth(h)
        setError(null)
      })
      .catch((e) => setError(errorMessage(e)))
  }, [])

  useEffect(() => {
    load()
    const timer = window.setInterval(load, 10_000)
    return () => window.clearInterval(timer)
  }, [load])

  const items: [string, string, boolean?][] = health
    ? [
        ['المهام الآن', `${health.agent.running} تعمل / ${health.agent.workers} • ${health.agent.queued} في الانتظار`, health.agent.queued > 0],
        ['CPU', `${health.process.cpuPercent}% من ${health.process.cores} أنوية`, health.process.cpuPercent > 80],
        ['الذاكرة', `${health.process.workingSetMb} MB (heap ${health.process.gcHeapMb} MB)`, health.process.workingSetMb > 3000],
        ['مدة الطلب', `متوسط ${health.agent.avgGenerationSeconds}s • أعلى 10% ${health.agent.p90GenerationSeconds}s`],
        ['انتظار في الطابور', `${health.agent.avgQueueWaitSeconds}s`, health.agent.avgQueueWaitSeconds > 30],
        ['طلبات فاشلة', `${health.agent.failed} من ${health.agent.failed + health.agent.succeeded}`, health.agent.failed > 0],
        ['زمن رد الـ AI', `متوسط ${(health.ai.avgLatencyMs / 1000).toFixed(1)}s • أعلى 10% ${(health.ai.p90LatencyMs / 1000).toFixed(1)}s`],
        ['حدود المزود (429)', `${health.ai.rateLimited} من ${health.ai.calls} نداء`, health.ai.rateLimited > 0],
        ['اتصالات قاعدة البيانات', health.database.connections == null ? '— (SQLite)' : `${health.database.connections}`],
        ['القرص', `${health.disk.freeGb} GB متاح من ${health.disk.totalGb}`, health.disk.freeGb < 10],
        ['نقاط محجوزة', `${health.credits.heldCredits} في ${health.credits.activeHolds} مهمة`],
        ['يعمل منذ', `${Math.floor(health.process.uptimeMinutes / 60)} ساعة ${health.process.uptimeMinutes % 60} دقيقة`],
      ]
    : []

  return (
    <Card className="mt-8">
      <div className="flex items-center justify-between gap-2">
        <div>
          <h2 className="text-lg font-bold">صحة السيرفر (آخر ساعة)</h2>
          <p className="text-sm text-slate-500">تتحدث كل 10 ثوان. راقبها أثناء اختبار الضغط أو عند زيادة المستخدمين.</p>
        </div>
        <Button variant="secondary" onClick={load}>
          تحديث
        </Button>
      </div>
      {error && (
        <div className="mt-3">
          <Alert tone="error">{error}</Alert>
        </div>
      )}
      <div className="mt-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        {items.map(([label, value, warn]) => (
          <div key={label} className={`rounded-xl border p-3 ${warn ? 'border-amber-300 bg-amber-50' : 'border-slate-200'}`}>
            <p className="text-xs text-slate-500">{label}</p>
            <p className="mt-1 text-sm font-bold">{value}</p>
          </div>
        ))}
      </div>
    </Card>
  )
}
