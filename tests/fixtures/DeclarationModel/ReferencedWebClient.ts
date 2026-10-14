import { createRemote, TypertConnection, type RemoteClient } from './remote.mjs'

type Input = Parameters<RemoteClient['referencedData/echo']>[0]['value']
const external = { external_name: 'web', Code: null, Next: { external_name: 'child', Code: 'present' } }
const input: Input = { External: external, Items: [external], ByName: { present: external, absent: null }, Groups: { nested: [external] }, Labels: { nullable: null } }
// @ts-expect-error The referenced authored required field cannot be omitted.
const missing: Input = { External: { Code: null } }
// @ts-expect-error Presence and nullability are separate authored facts.
const nullName: Input = { External: { external_name: null, Code: null } }
// @ts-expect-error A dictionary value retains its referenced declaration.
const wrongDictionary: Input = { External: external, ByName: { invalid: { external_name: 1, Code: null } } }
void [missing, nullName, wrongDictionary]
// @ts-expect-error Guid's Web representation is a string, not a numeric identifier.
const invalidGuid: Parameters<RemoteClient['referencedData/guid']>[0] = { value: 1 }
// @ts-expect-error Non-null Guid and Guid? have distinct contracts.
const nullGuid: Parameters<RemoteClient['referencedData/guid']>[0] = { value: null }
void [invalidGuid, nullGuid]

export async function run(origin: string, count: number, fetcher?: typeof fetch) {
  const connection = new TypertConnection(origin + '/remote', fetcher)
  const client = createRemote(connection)
  try {
    const empty = '00000000-0000-0000-0000-000000000000'
    for (const id of [empty, 'f031a626-7695-4b15-a915-15aa56e0cf94']) {
      const scalar = await client['referencedData/guid']({ value: id })
      if (!scalar.ok || scalar.value !== id) throw new Error('Guid root did not round trip')
      const nullable = await client['referencedData/nullableGuid']({ value: id })
      if (!nullable.ok || nullable.value !== id) throw new Error('Nullable Guid lost a present value')
      const item = await client['referencedData/item']({ value: {
        Id: id, Optional: null, Array: [empty, id], Values: [null, id], ByName: { id },
      } })
      if (!item.ok || item.value.Id !== id || item.value.Optional !== null ||
          item.value.Array?.[1] !== id || item.value.Values?.[0] !== null ||
          item.value.Values?.[1] !== id || item.value.ByName?.id !== id)
        throw new Error('Guid DTO and collection values did not round trip')
    }
    const defaultGuid = await client['referencedData/guid']({})
    const nullableGuid = await client['referencedData/nullableGuid']({ value: null })
    const absentGuid = await client['referencedData/nullableGuid']({})
    if (!defaultGuid.ok || defaultGuid.value !== empty ||
        !nullableGuid.ok || nullableGuid.value !== null || !absentGuid.ok || absentGuid.value !== null)
      throw new Error('Guid omission and nullable input changed')
    for (const value of [null, 42, 'not-a-guid', 'f031a62676954b15a91515aa56e0cf94',
        '{f031a626-7695-4b15-a915-15aa56e0cf94}']) {
      const rejected = await connection.call('referencedData/guid', { value })
      if (rejected.ok || rejected.error.code !== 'gateway/input-invalid')
        throw new Error('Host admitted a value outside its STJ Guid wire contract')
    }
    const result = await client['referencedData/echo']({ value: input })
    if (!result.ok) throw result.error
    if (result.value.External.external_name !== 'web' || result.value.External.Code !== null ||
        result.value.External.Count !== count || result.value.External.Note !== 'external-default' ||
        result.value.External.Next?.external_name !== 'child' || result.value.ByName?.absent !== null ||
        result.value.Groups?.nested?.[0]?.external_name !== 'web')
      throw new Error('Referenced wire names, defaults, recursive data or collection values changed')
    for (const value of [{ External: { external_name: 'bad' } }, { External: { external_name: null, Code: null } }]) {
      const rejected = await connection.call('referencedData/echo', { value })
      if (rejected.ok) throw new Error('Host accepted an input refused by the generated declaration')
    }
    const abort = new AbortController()
    const reason = new Error('Consumer cancelled before send')
    abort.abort(reason)
    try {
      await connection.call('referencedData/echo', { value: input }, abort.signal)
      throw new Error('Cancelled call completed')
    } catch (error) {
      if (error !== reason) throw error
    }
  } finally {
    await client.dispose()
    await connection.dispose()
  }
}
