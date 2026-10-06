import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { installDesktopApi } from './api'
import { App } from './App'
import './monaco'
import './styles.css'

installDesktopApi()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
