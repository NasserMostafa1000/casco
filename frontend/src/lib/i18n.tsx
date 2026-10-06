import { Fragment, useEffect, useState, type ReactNode } from 'react'
import en from '../locales/en'
import hi from '../locales/hi'

export type Lang = 'ar' | 'en' | 'hi'

export const LANGUAGES: { code: Lang; label: string; short: string }[] = [
  { code: 'ar', label: 'العربية', short: 'ع' },
  { code: 'en', label: 'English', short: 'EN' },
  { code: 'hi', label: 'हिन्दी', short: 'हि' },
]

const STORAGE_KEY = 'casco_lang'
// Arabic is the source language: its text is the key, so a missing translation falls back to Arabic.
const dictionaries: Record<Lang, Record<string, string> | null> = { ar: null, en, hi }
const locales: Record<Lang, string> = { ar: 'ar-u-nu-latn', en: 'en-US', hi: 'hi-IN-u-nu-latn' }

function detect(): Lang {
  const fromUrl = new URLSearchParams(location.search).get('lang')
  if (fromUrl === 'ar' || fromUrl === 'en' || fromUrl === 'hi') {
    localStorage.setItem(STORAGE_KEY, fromUrl)
    return fromUrl
  }
  const saved = localStorage.getItem(STORAGE_KEY)
  if (saved === 'ar' || saved === 'en' || saved === 'hi') return saved
  const prefs = (navigator.languages?.length ? navigator.languages : [navigator.language]).map((l) => l?.toLowerCase() ?? '')
  if (prefs.some((l) => l.startsWith('ar'))) return 'ar'
  if (prefs[0]?.startsWith('hi')) return 'hi'
  if (prefs[0]?.startsWith('en')) return 'en'
  return 'ar'
}

let current: Lang = detect()
const listeners = new Set<(l: Lang) => void>()

export const getLang = () => current
export const isRtl = () => current === 'ar'

function applyToDocument() {
  document.documentElement.lang = current
  document.documentElement.dir = isRtl() ? 'rtl' : 'ltr'
}
applyToDocument()

export function setLang(lang: Lang) {
  if (lang === current) return
  current = lang
  localStorage.setItem(STORAGE_KEY, lang)
  applyToDocument()
  listeners.forEach((l) => l(lang))
}

/** Translates Arabic source text; `{name}` placeholders are filled from `vars`. */
export function t(text: string, vars?: Record<string, string | number>): string {
  let out = dictionaries[current]?.[text] ?? text
  if (vars) out = out.replace(/\{(\w+)\}/g, (m, k: string) => (k in vars ? String(vars[k]) : m))
  return out
}

type Pattern = { re: RegExp; key: string; names: string[] }
const patternCache = new Map<Lang, Pattern[]>()

function patternsFor(lang: Lang): Pattern[] {
  let list = patternCache.get(lang)
  if (list) return list
  list = Object.keys(dictionaries[lang] ?? {})
    .filter((k) => /\{\w+\}/.test(k) && k.replace(/\{\w+\}/g, '').trim().length >= 4)
    .sort((a, b) => b.replace(/\{\w+\}/g, '').length - a.replace(/\{\w+\}/g, '').length)
    .map((key) => {
      const names: string[] = []
      const src = key.replace(/[.*+?^$()|[\]\\]/g, '\\$&').replace(/\{(\w+)\}/g, (_, n: string) => (names.push(n), '(.*?)'))
      return { re: new RegExp(`^${src}$`, 's'), key, names }
    })
  patternCache.set(lang, list)
  return list
}

function translateLine(line: string, depth: number): string {
  const dict = dictionaries[current]
  const trimmed = line.trim()
  if (!dict || !trimmed || depth > 3) return line
  const lead = line.slice(0, line.indexOf(trimmed))
  const trail = line.slice(lead.length + trimmed.length)
  return lead + translateTrimmed(trimmed, depth) + trail
}

function translateTrimmed(text: string, depth: number): string {
  const dict = dictionaries[current]!
  if (dict[text]) return dict[text]
  const prefix = /^[^\p{L}\p{N}(«"]+/u.exec(text)?.[0]
  if (prefix) return prefix.replace('،', t('،')) + translateLine(text.slice(prefix.length), depth)
  for (const p of patternsFor(current)) {
    const m = p.re.exec(text)
    if (!m) continue
    const vars: Record<string, string> = {}
    p.names.forEach((n, i) => (vars[n] = translateLine(m[i + 1], depth + 1)))
    return t(p.key, vars)
  }
  return text
}

/**
 * Translates text produced by the server (errors, notices, chat progress): whole-text match first, then line by line,
 * where dictionary keys with `{placeholders}` also match messages that embed numbers or names.
 */
export function tServer(text: string | null | undefined): string {
  if (!text) return ''
  const dict = dictionaries[current]
  if (!dict) return text
  if (dict[text.trim()]) return dict[text.trim()]
  return text.split('\n').map((l) => translateLine(l, 0)).join('\n')
}

export const num = (n: number | null | undefined) => (n == null ? '…' : n.toLocaleString(locales[current]))

export function formatDate(iso: string | null | undefined) {
  if (!iso) return '-'
  return new Date(iso).toLocaleDateString(locales[current], { year: 'numeric', month: 'short', day: 'numeric' })
}

export function formatDateTime(iso: string) {
  return new Date(iso).toLocaleString(locales[current], { dateStyle: 'medium', timeStyle: 'short' })
}

/** Re-mounts the app when the language changes, so every `t()` call re-renders in the new language. */
export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setState] = useState(current)
  useEffect(() => {
    listeners.add(setState)
    return () => {
      listeners.delete(setState)
    }
  }, [])
  return <Fragment key={lang}>{children}</Fragment>
}
