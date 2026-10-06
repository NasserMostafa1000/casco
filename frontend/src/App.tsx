import { lazy, Suspense, useEffect, useRef, type ReactNode } from 'react'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { AppShell, RequireAuth } from './components/AppShell'
import { useAuth } from './lib/auth'
import { identifyUser, trackView } from './lib/tiktok'
import { Spinner } from './components/ui'
import Dashboard from './pages/Dashboard'
import Settings from './pages/Settings'
import Landing from './pages/Landing'
import { Login, Register } from './pages/Auth'
import { DesktopConnect } from './pages/DesktopConnect'
import NewProject from './pages/NewProject'

const Admin = lazy(() => import('./pages/Admin'))
const Billing = lazy(() => import('./pages/Billing'))
const BillingResult = lazy(() => import('./pages/BillingResult'))
const Editor = lazy(() => import('./pages/Editor'))
const SiteData = lazy(() => import('./pages/SiteData'))
const ReactHost = lazy(() => import('./pages/ReactHost'))

function Page({ children }: { children: ReactNode }) {
  return (
    <Suspense
      fallback={
        <div className="grid min-h-[50vh] place-items-center">
          <Spinner className="h-7 w-7 text-brand-600" />
        </div>
      }
    >
      {children}
    </Suspense>
  )
}

function TikTokPageView() {
  const { pathname, search } = useLocation()
  const { me } = useAuth()
  const first = useRef(true)
  useEffect(() => {
    if (!first.current) window.ttq?.page()
    first.current = false
    trackView(pathname)
  }, [pathname])
  useEffect(() => {
    if (me) void identifyUser(me.user)
  }, [me])
  useEffect(() => {
    const ref = new URLSearchParams(search).get('ref')
    if (ref && /^[a-z0-9]{6,12}$/i.test(ref)) localStorage.setItem('casco_ref', ref.toLowerCase())
  }, [search])
  return null
}

export default function App() {
  return (
    <>
      <TikTokPageView />
      <Routes>
      <Route path="/" element={<Landing />} />
      <Route path="/login" element={<Login />} />
      <Route path="/desktop" element={<DesktopConnect />} />
      <Route path="/register" element={<Register />} />
      <Route path="/app/new" element={<NewProject />} />
      <Route element={<RequireAuth />}>
        <Route path="/app/p/:id" element={<Page><Editor /></Page>} />
        <Route element={<AppShell />}>
          <Route path="/app" element={<Dashboard />} />
          <Route path="/app/settings" element={<Settings />} />
          <Route path="/app/react/:id" element={<Page><ReactHost /></Page>} />
          <Route path="/app/p/:id/data" element={<Page><SiteData /></Page>} />
          <Route path="/app/billing" element={<Page><Billing /></Page>} />
          <Route path="/app/billing/result" element={<Page><BillingResult /></Page>} />
          <Route
            path="/app/admin"
            element={
              <div dir="rtl" lang="ar">
                <Page>
                  <Admin />
                </Page>
              </div>
            }
          />
        </Route>
      </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </>
  )
}
