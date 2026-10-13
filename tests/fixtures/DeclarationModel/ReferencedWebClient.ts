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

export async function run(origin: string, count: number) {
  const connection = new TypertConnection(origin + '/remote')
  const client = createRemote(connection)
  try {
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
  } finally {
    await client.dispose()
    await connection.dispose()
  }
}
