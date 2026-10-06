export type PendingMedia = { file: File; url: string; video: boolean }

// Files a visitor attached before signing in. They stay in memory while the app
// navigates to the login page and back; a full reload drops them.
let carried: PendingMedia[] = []

export function pendingMedia() {
  return carried
}

export function setPendingMedia(picks: PendingMedia[]) {
  carried = picks
}

export function clearPendingMedia() {
  carried = []
}
