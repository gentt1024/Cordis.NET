import { createRemote, TypertConnection, type RemoteClient } from './remote.mjs'
import type { EventListeners } from './events.mjs'

type Input = Parameters<RemoteClient['webNumeric/echo']>[0]
const input: Input = {value: {name: 'frame', frame: {scale: '+1e2'}, samples: ['.5', null], counts: {one: '01'}}}
const changed: EventListeners['webNumeric/changed'] = value => {
  const scale: number = value.frame.scale
  // @ts-expect-error Event arguments use the author's output direction.
  const text: string = value.frame.scale
  void [scale, text]
}
void changed
function outputType(result: Awaited<ReturnType<RemoteClient['webNumeric/echo']>>) {
  if (!result.ok) throw result.error
  const scale: number = result.value.frame.scale
  // @ts-expect-error AllowReadingFromString is not WriteAsString.
  const text: string = result.value.frame.scale
  return scale
}

export async function run(origin: string) {
  const connection = new TypertConnection(origin + '/remote')
  const remote = createRemote(connection)
  try {
    const first = await remote['webNumeric/echo'](input)
    if (!first.ok || outputType(first) !== 100 || first.value.frameLocked !== true || first.value.samples[0] !== .5 || first.value.counts.one !== 1)
      throw new Error('Web metadata/defaults/numeric input did not reach the generated Host binding.')
    const explicit = await remote['webNumeric/echo']({value: {name: 'frame', frameLocked: false, frame: {scale: 0}}})
    if (!explicit.ok || explicit.value.frameLocked !== false || explicit.value.frame.scale !== 0)
      throw new Error('Explicit false/zero was replaced by an initializer.')
    const stream = remote['webNumeric/stream'](input)
    let received = 0
    try {
      for await (const value of stream) {
        if (value.frame.scale !== 100 || value.frameLocked !== true) throw new Error('Socket result lost the output contract.')
        received++
      }
    } finally { await stream.dispose() }
    if (received !== 1) throw new Error('The real socket did not deliver one numeric result.')
    // Raw input tests transport admission; generated types cannot construct these invalid values.
    for (const value of [{}, {name: null}, {name: 'a', frame: {scale: 'bad'}},
      {name: 'a', samples: ['NaN']}, {name: 'a', counts: {one: '-1'}}]) {
      const result = await connection.call('webNumeric/echo', {value})
      if (result.ok || result.error.code !== 'gateway/input-invalid')
        throw new Error('Invalid numeric DTO reached business execution: ' + JSON.stringify(result))
    }
  } finally { await remote.dispose(); await connection.dispose() }
}
