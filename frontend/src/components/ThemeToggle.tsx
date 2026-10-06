import { Moon, Sun } from 'lucide-react'
import { t } from '../lib/i18n'
import { applyTheme, useTheme } from '../lib/theme'

export function ThemeToggle() {
  const theme = useTheme()
  const next = theme === 'dark' ? 'light' : 'dark'
  const label = next === 'light' ? t('الوضع النهاري') : t('الوضع الليلي')
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={() => applyTheme(next)}
      className="grid h-9 w-9 place-items-center rounded-xl text-slate-600 transition hover:bg-slate-100 hover:text-slate-900"
    >
      {theme === 'dark' ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
    </button>
  )
}
