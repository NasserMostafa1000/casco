/// <reference types="vite/client" />

import type { CascoDesktopApi } from '../shared/types'

declare global {
  interface Window {
    cascoDesktop: CascoDesktopApi
  }
}

export {}
