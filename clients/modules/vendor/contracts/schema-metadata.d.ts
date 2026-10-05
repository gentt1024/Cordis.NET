import type { StandardSchemaV1 } from '@standard-schema/spec'

/** Type-only view used by the fixed Loader's raw comparison. No schema validator is shipped here. */
export default interface Schema extends StandardSchemaV1 {
  readonly type: string
  readonly dict?: Readonly<Record<string, Schema>>
  readonly meta?: { readonly volatile?: boolean; readonly default?: unknown }
}
