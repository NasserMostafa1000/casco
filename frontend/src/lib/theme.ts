import { useEffect, useState } from 'react'

const KEY = 'casco_theme'
export type Theme = 'dark' | 'light'

const listeners = new Set<(theme: Theme) => void>()

export function getTheme(): Theme {
  return document.documentElement.classList.contains('dark') ? 'dark' : 'light'
}

export function applyTheme(theme: Theme) {
  document.documentElement.classList.toggle('dark', theme === 'dark')
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#181818' : '#ffffff')
  try {
    localStorage.setItem(KEY, theme)
  } catch {
    /* private mode */
  }
  listeners.forEach((l) => l(theme))
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(getTheme)
  useEffect(() => {
    listeners.add(setTheme)
    return () => {
      listeners.delete(setTheme)
    }
  }, [])
  return theme
}
