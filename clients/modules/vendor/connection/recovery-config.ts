/** Native SDK boundary adapter for the fixed connection timing contract.
 * Schemastery is not shipped as a runtime dependency; numeric validation and defaults
 * match packages/client/connection/src/recovery-config.ts at the recorded upstream commit.
 */
export interface ConnectionRecoveryConfig {
  /** First retry cap in milliseconds; actual delay is 50–100% of this. Default: 500. */
  backoffBaseMs?: number
  /** Finite growth factor, at least one. Default: 2. */
  backoffFactor?: number
  /** Maximum retry cap in milliseconds. Default: 10000. */
  backoffMaxMs?: number
  /** Slow handshake warning delay in milliseconds. Default: 3000. */
  generationReadyWarnMs?: number
  /** Handshake deadline in milliseconds, including carrier setup. Default: 15000. */
  generationReadyTimeoutMs?: number
}

export function resolveConnectionConfig(config: ConnectionRecoveryConfig = {}): Required<ConnectionRecoveryConfig> {
  const timer = (value: number | undefined, fallback: number, name: string): number => {
    const result = value === undefined ? fallback : value
    if (!Number.isSafeInteger(result) || result < 1 || result > 2_147_483_647) {
      throw new RangeError(`connection recovery ${name} must be an integer in [1, 2147483647]`)
    }
    return result
  }
  const factor = config.backoffFactor === undefined ? 2 : config.backoffFactor
  if (!Number.isFinite(factor) || factor < 1) throw new RangeError('connection recovery backoffFactor must be finite and at least 1')
  return {
    backoffBaseMs: timer(config.backoffBaseMs, 500, 'backoffBaseMs'),
    backoffFactor: factor,
    backoffMaxMs: timer(config.backoffMaxMs, 10_000, 'backoffMaxMs'),
    generationReadyWarnMs: timer(config.generationReadyWarnMs, 3_000, 'generationReadyWarnMs'),
    generationReadyTimeoutMs: timer(config.generationReadyTimeoutMs, 15_000, 'generationReadyTimeoutMs'),
  }
}
