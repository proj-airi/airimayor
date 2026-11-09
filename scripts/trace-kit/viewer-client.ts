import type { Remote } from 'comlink'
import type { ViewerSession } from './viewer-session'
import { releaseProxy, wrap } from 'comlink'

declare const __VIEWER_WORKER__: string

export class ViewerClient {
  readonly remote: Remote<ViewerSession>
  private url = URL.createObjectURL(new Blob([__VIEWER_WORKER__], { type: 'text/javascript' }))
  private worker = new Worker(this.url, { name: 'airimayor-session-replay' })

  private disposed = false

  constructor(onError: (message: string) => void) {
    this.remote = wrap<ViewerSession>(this.worker)
    this.worker.onerror = (event) => onError(event.message || 'The recording worker stopped unexpectedly.')
  }

  dispose() {
    if (this.disposed) return
    this.disposed = true
    this.remote[releaseProxy]()
    this.worker.terminate()
    URL.revokeObjectURL(this.url)
  }
}
