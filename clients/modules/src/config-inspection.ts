import type { StandardSchemaV1 } from '@standard-schema/spec'

type SchemaDocument = boolean | Readonly<Record<string, unknown>>
type PortableSchema = {
  readonly status: 'schema'
  readonly document: SchemaDocument
  /** JSON Schema alone describes JSON values and cannot express an omitted outer config. */
  readonly acceptsMissing: boolean | 'unknown'
  readonly limitations: readonly string[]
}
type Unsupported = { readonly status: 'unsupported'; readonly diagnostic: string }

/** A live validator and its portable input declaration have independent capabilities. */
export type ClientConfigInspection =
  | { readonly status: 'absent' }
  | { readonly status: 'schema'; readonly schema: StandardSchemaV1; readonly portable: PortableSchema | Unsupported }
  | { readonly status: 'portable'; readonly portable: PortableSchema }
  | Unsupported

/** Optional tooling exports from the pinned DSH app-boot projector. No boot services are required. */
export interface ClientNativeConfigProjection<Native> {
  readonly isNativeConfigSchema: (value: unknown) => value is Native
  readonly createConfigProjector: () => Promise<(root: Native, prefix: string) => {
    readonly schema: Readonly<Record<string, unknown>>
    readonly definitions: Readonly<Record<string, SchemaDocument>>
    readonly acceptsMissing: boolean | 'unknown'
    readonly limitations: readonly string[]
  }>
  readonly LOADER_EXPRESSION_SCHEMA: Readonly<Record<string, unknown>>
}

const dialect = 'https://json-schema.org/draft/2020-12/schema'

function objectLike(value: unknown): value is object {
  return value !== null && (typeof value === 'object' || typeof value === 'function')
}

function properties(value: unknown): object | undefined {
  const spec: unknown = objectLike(value) ? Reflect.get(value, '~standard') : undefined
  return objectLike(spec) && 'version' in spec && spec.version === 1 && 'vendor' in spec && typeof spec.vendor === 'string'
    ? spec : undefined
}

function hasValidator(value: unknown, spec: object | undefined): value is StandardSchemaV1 {
  return objectLike(value) && spec !== undefined && 'validate' in spec && typeof spec.validate === 'function'
}

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }

/** Take a detached JSON snapshot without running toJSON/getters or silently dropping values. */
function snapshot(value: unknown, visiting: Set<object>): Json {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return value
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value !== 'object' || visiting.has(value)) throw new TypeError('Plugin Config input schema must be acyclic JSON data.')
  visiting.add(value)
  try {
    const read = (key: string): Json => {
      const field = Object.getOwnPropertyDescriptor(value, key)
      if (!field?.enumerable || !('value' in field)) throw new TypeError('Plugin Config input schema contains a non-JSON property.')
      return snapshot(field.value, visiting)
    }
    const keys = Reflect.ownKeys(value)
    if (Array.isArray(value)) {
      if (keys.length !== value.length + 1) throw new TypeError('Plugin Config input schema contains a sparse array or extra array properties.')
      return Array.from({ length: value.length }, (_, index) => read(String(index)))
    }
    const proto: object | null = Object.getPrototypeOf(value)
    if (proto !== null && Object.getPrototypeOf(proto) !== null) throw new TypeError('Plugin Config input schema must contain plain objects.')
    return Object.fromEntries(keys.map(key => {
      if (typeof key !== 'string') throw new TypeError('Plugin Config input schema contains a symbol key.')
      return [key, read(key)]
    }))
  } finally {
    visiting.delete(value)
  }
}

function documentOf(value: unknown): SchemaDocument {
  const document = snapshot(value, new Set())
  if (typeof document === 'boolean') return document
  if (document === null || typeof document !== 'object' || Array.isArray(document)) {
    throw new TypeError('Plugin Config input schema must be an object or boolean.')
  }
  if ('$schema' in document && document.$schema !== dialect) throw new TypeError('Plugin Config input schema must target draft 2020-12.')
  return { ...document, $schema: dialect }
}

async function portableInput<Native>(
  declaration: unknown, spec: object | undefined, native: ClientNativeConfigProjection<Native> | undefined,
): Promise<PortableSchema | Unsupported> {
  if (native?.isNativeConfigSchema(declaration)) {
    const project = await native.createConfigProjector()
    const { schema, definitions, acceptsMissing, limitations } = project(declaration, 'config')
    const document = documentOf({ ...schema, $defs: { ...definitions, loaderExpression: native.LOADER_EXPRESSION_SCHEMA } })
    return { status: 'schema', document, acceptsMissing, limitations: [...limitations] }
  }
  const converter = spec && 'jsonSchema' in spec ? spec.jsonSchema : undefined
  if (objectLike(converter) && 'input' in converter && typeof converter.input === 'function') {
    const document = documentOf(converter.input({ target: 'draft-2020-12' }))
    return { status: 'schema', document, acceptsMissing: 'unknown', limitations: ['Standard JSON Schema input conversion does not describe omission of the outer config.'] }
  }
  return { status: 'unsupported', diagnostic: 'Plugin Config has no portable draft 2020-12 input declaration.' }
}

/** Read the resolved plugin declaration without activating the plugin or invoking its config validator. */
export async function inspectConfig<Native>(plugin: unknown, native?: ClientNativeConfigProjection<Native>): Promise<ClientConfigInspection> {
  const declaration: unknown = objectLike(plugin) && 'Config' in plugin ? plugin.Config : undefined
  if (declaration == null) return { status: 'absent' }
  const spec = properties(declaration)
  const portable = await portableInput(declaration, spec, native)
  if (hasValidator(declaration, spec)) return { status: 'schema', schema: declaration, portable }
  if (portable.status === 'schema') return { status: 'portable', portable }
  return portable
}
