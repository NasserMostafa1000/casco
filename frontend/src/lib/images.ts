const MAX_SIDE = 1600

export const MEDIA_ACCEPT = 'image/*,video/mp4,video/webm,video/quicktime'
export const MAX_VIDEO_BYTES = 50 * 1024 * 1024
export const isVideoFile = (file: File) => file.type.startsWith('video/')
export const isMediaFile = (file: File) => file.type.startsWith('image/') || isVideoFile(file)
export const isVideoUrl = (src: string) => /\.(mp4|webm|mov)([?#]|$)/i.test(src)

/** Shrinks large photos in the browser before upload (phones produce 5-12 MB images). GIFs are kept as-is. */
export async function prepareImage(file: File): Promise<File> {
  if (file.type === 'image/gif' || !file.type.startsWith('image/')) return file
  let bitmap: ImageBitmap
  try {
    bitmap = await createImageBitmap(file)
  } catch {
    return file
  }
  const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height))
  if (scale === 1 && file.size < 700_000) {
    bitmap.close()
    return file
  }
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(bitmap.width * scale)
  canvas.height = Math.round(bitmap.height * scale)
  canvas.getContext('2d')!.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
  bitmap.close()
  const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/webp', 0.85))
  if (!blob || blob.size >= file.size) return file
  return new File([blob], file.name.replace(/\.\w+$/, '') + '.webp', { type: 'image/webp' })
}
