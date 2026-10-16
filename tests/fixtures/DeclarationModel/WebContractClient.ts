import { createRemote, resultCodecs, TypertConnection, type RemoteClient } from './remote.mjs'

type Revision = Parameters<RemoteClient['webContract/revision']>[0]
const omitted: Revision = {}
const supplied: Revision = { expectedRevision: 3 }
// @ts-expect-error Optional does not admit explicit null.
const nullRevision: Revision = { expectedRevision: null }
type Required = Parameters<RemoteClient['webContract/required']>[0]
// @ts-expect-error A non-optional constructor parameter is required by Host admission.
const missingName: Required = { value: {} }
type Selected = Parameters<RemoteClient['webContract/selected']>[0]
// @ts-expect-error The selected JsonConstructor determines requiredness.
const missingSelected: Selected = { value: {} }
type Direction = Parameters<RemoteClient['webContract/direction']>[0]
const valid: Direction = { value: { Count: 1, GlobalCount: 1, Value: 'input', OutputNullable: 'input', InputNullable: null } }
// @ts-expect-error Setter DisallowNull is an input restriction.
const invalidInput: Direction = { value: { Count: 1, GlobalCount: 1, Value: null, OutputNullable: 'input' } }
function checkOutput(result: Awaited<ReturnType<RemoteClient['webContract/direction']>>) {
  if (!result.ok) throw result.error
  if (resultCodecs['webContract/direction'].parse(result.value) !== result.value)
    throw new Error('Saved-model codec changed the directional output')
  let rejected = false
  try { resultCodecs['webContract/direction'].parse({ ...result.value, Always: undefined }) }
  catch (error) { rejected = error instanceof TypeError }
  if (!rejected) throw new Error('Saved-model codec lost required output presence')
  // @ts-expect-error A conditional write can omit Count.
  const requiredCount: number = result.value.Count
  // @ts-expect-error The global write condition can omit GlobalCount.
  const requiredGlobal: number = result.value.GlobalCount
  // @ts-expect-error Getter nullability is independent of setter DisallowNull.
  const notNull: string = result.value.Value
  // @ts-expect-error MaybeNull describes the getter.
  const alsoNotNull: string = result.value.OutputNullable
  void [requiredCount, requiredGlobal, notNull, alsoNotNull]
  if ('Count' in result.value || 'GlobalCount' in result.value || result.value.Value !== null ||
      result.value.OutputNullable !== null || result.value.Always !== 0 || result.value.InputNullable !== 'initial')
    throw new Error('Output does not match directional serialization')
}
void [nullRevision, missingName, missingSelected, invalidInput]

export async function run(origin: string) {
  const connection = new TypertConnection(origin + '/remote')
  const client = createRemote(connection)
  try {
    for (const [input, expected] of [[omitted, 'omitted'], [supplied, 'supplied']] as const) {
      const result = await client['webContract/revision'](input)
      if (!result.ok || result.value !== expected) throw new Error('Revision admission changed')
    }
    const required = await client['webContract/required']({ value: { Name: 'provided' } })
    const selected = await client['webContract/selected']({ value: { Name: 'selected' } })
    const optional = await client['webContract/optional']({ value: {} })
    if (!required.ok || required.value.Name !== 'provided' || !selected.ok || selected.value.Name !== 'selected' ||
        !optional.ok || optional.value.Name !== 'constructor-default') throw new Error('Constructor contract changed')
    checkOutput(await client['webContract/direction'](valid))
    const explicitNull = await client['webContract/direction']({ value: { ...valid.value, OptionalJson: null } })
    if (!explicitNull.ok || explicitNull.value.Count !== 1) throw new Error('Explicit JSON null was collapsed into omission')
    const enums = await client['webContract/enums']({ value: { Status: 37, Optional: null, History: [4, -7], Flags: 3, Always: 0 } })
    if (!enums.ok || enums.value.Status !== 37 || enums.value.Optional !== -1 ||
        enums.value.History?.[0] !== 4 || enums.value.History?.[1] !== -7 || enums.value.Flags !== 3 || enums.value.Always !== 0)
      throw new Error('Numeric enum values, aliases, flags or optional default changed')
    const always: number = enums.value.Always
    void always
    for (const [input, expected] of [
      [{}, [-1, 4, null, null]],
      [{ negative: null, positive: null }, [null, null, null, null]],
      [{ negative: -7, positive: 37 }, [-7, 37, null, null]],
    ] as const) {
      const result = await client['webContract/nullableEnums'](input)
      if (!result.ok || JSON.stringify(result.value) !== JSON.stringify(expected))
        throw new Error('Nullable enum default, omission or explicit null changed')
    }
    const before = await client['webContract/calls']({})
    for (const [endpoint, input] of [
      ['webContract/enums', { value: { Status: null } }],
      ['webContract/enums', { value: { Status: 'Succeeded' } }],
      ['webContract/enums', { value: { Status: 1.5 } }],
      ['webContract/enums', { value: { Flags: 256 } }],
      ['webContract/revision', { expectedRevision: null }],
      ['webContract/required', { value: {} }],
      ['webContract/selected', { value: {} }],
      ['webContract/direction', { value: { Count: 1, GlobalCount: 1, Value: null, OutputNullable: 'input' } }],
      ['webContract/direction', { value: { GlobalCount: 1, Value: 'input', OutputNullable: 'input' } }],
    ] as const) {
      const rejected = await connection.call(endpoint, input)
      if (rejected.ok) throw new Error('Host admitted invalid input: ' + endpoint)
    }
    const after = await client['webContract/calls']({})
    if (!before.ok || !after.ok || before.value !== after.value) throw new Error('Invalid input entered business code')
  } finally {
    await client.dispose()
    await connection.dispose()
  }
}
