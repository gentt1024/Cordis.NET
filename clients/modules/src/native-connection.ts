import type { Context } from '@deepseek-ai/cordis'
import { installConnection, type ConnectionGenerationSource, type ConnectionHandle, type ConnectionInstallOptions,
  type ConnectionSinks } from '@deepseek-ai/dsh-client-connection/client'

/** Each native subscription owns its own source on the caller's Cordis root. */
export function startNativeConnection(context: Context, options: ConnectionInstallOptions,
  source: (handle: ConnectionHandle) => ConnectionGenerationSource, sinks: ConnectionSinks = {}) {
  let handle: ConnectionHandle | undefined
  let withdraw: (() => void) | undefined
  let pending = Promise.resolve()
  const fiber = context.isolate('connection').plugin({
    name: 'native-connection',
    apply(owner: Context) {
      installConnection(owner, options)
      // The service belongs to this still-loading fiber.
      const installed: ConnectionHandle = owner.get('connection', false)
      handle = installed
      const run = source(installed)
      withdraw = installed.registerGenerationSource((signal, ready) => pending = run(signal, ready))
      owner.effect(() => () => { withdraw?.(); return pending }, 'native-connection.source')
      installed.start(sinks)
    },
  })
  return {
    reconnect() { handle?.reconnect() },
    async stop() {
      withdraw?.()
      // Parent unload may already have invoked the fiber disposer. Both paths
      // must wait for the source before a borrowed native namespace is withdrawn.
      await Promise.all([pending, fiber.dispose()])
    },
  }
}
