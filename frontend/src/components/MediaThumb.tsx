import { Play } from 'lucide-react'
import { isVideoUrl } from '../lib/images'

/** Square preview of an uploaded photo or video. */
export function MediaThumb({ src, video, className = '' }: { src: string; video?: boolean; className?: string }) {
  if (!(video ?? isVideoUrl(src))) return <img src={src} alt="" loading="lazy" className={`object-cover ${className}`} />
  return (
    <span className={`relative block overflow-hidden bg-black ${className}`}>
      <video src={src} muted playsInline preload="metadata" className="h-full w-full object-cover" />
      <span className="absolute inset-0 grid place-items-center">
        <span className="grid h-6 w-6 place-items-center rounded-full bg-black/60 text-white">
          <Play className="h-3 w-3 fill-current" />
        </span>
      </span>
    </span>
  )
}
