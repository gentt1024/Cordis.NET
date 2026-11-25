import { Context } from '@deepseek-ai/cordis'
import { installConnection, type ConnectionGenerationSource, type ConnectionHandle, type ConnectionInstallOptions,
  type ConnectionSinks } from '@deepseek-ai/dsh-client-connection/client'

/** Each native subscription owns its own source on the caller's Cordis root. */
export function startNativeConnection(context: Context | undefined, options: ConnectionInstallOptions,
  source: (handle: ConnectionHandle) => ConnectionGenerationSource, sinks: ConnectionSinks = {}) {
  const parent = context ?? new Context()
  let handle: ConnectionHandle
  let withdraw: (() => void) | undefined
  let pending = Promise.resolve()
  // The child fiber provides a distinct service and effect owner even when
  // management and access are mounted on the same caller fiber.
  const fiber = parent.isolate('connection').plugin({ name: 'native-connection', apply() {} })
  async function stop() {
    withdraw?.()
    try { await Promise.all([pending, fiber.dispose()]) }
    finally { if (!context) await parent.fiber.dispose() }
  }
  const cleanupFailedStart = () => stop().catch(reason => parent.logger.error(reason))
  try {
    // Connection validates recovery in start(). Run it before returning to the caller.
    installConnection(fiber.ctx, options)
    handle = fiber.ctx.get('connection', false)
    const run = source(handle)
    withdraw = handle.registerGenerationSource(async (signal, ready) => {
      await fiber.await()
      if (!signal.aborted) await (pending = run(signal, ready))
    })
    fiber.ctx.effect(() => () => { withdraw?.(); return pending }, 'native-connection.source')
    handle.start(sinks)
  } catch (error) {
    void cleanupFailedStart()
    throw error
  }
  const started = fiber.await().catch(async error => { await cleanupFailedStart(); throw error })
  return { started, reconnect: () => handle.reconnect(), stop }
}
