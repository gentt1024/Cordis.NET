import assert from 'node:assert/strict'

// Exercise a generated unary method through its public Connection transport override.
export async function verifyClientCauses(createRemote, TypertConnection, endpoint, args) {
  for (const cause of [new Error('local retirement'), {code: 'local/retired'}, Object.create(null), null, undefined, false, 0, 'local']) {
    for (const cancelled of [false, true]) {
      const cancellation = new AbortController()
      let calls = 0
      const connection = new TypertConnection('http://127.0.0.1/', async () => {
        calls++
        if (cancelled) cancellation.abort('different signal reason')
        throw cause
      })
      const client = createRemote(connection)
      try {
        const result = await client[endpoint](args, cancellation.signal)
        assert.equal(result.ok, false)
        assert.equal(result.error.code, cancelled ? 'gateway/cancelled' : 'gateway/internal')
        assert.ok(Object.hasOwn(result.error, 'cause'))
        assert.equal(result.error.cause, cause)
        assert.deepEqual(result.error.details, {})
        assert.equal(Object.hasOwn(JSON.parse(JSON.stringify(result.error)), 'cause'), false)
        assert.equal(calls, 1, 'the generated caller must not replay a failed call')
      } finally {
        await client.dispose()
        await connection.dispose()
      }
    }
  }
  const connection = new TypertConnection('http://127.0.0.1/', async (_, request) => {
    const {rpcId} = JSON.parse(request.body)
    return Response.json({type: 'server-response', rpcId, result: {
      ok: false, error: {code: 'business/refused', message: 'refused', details: {value: 3}, cause: {code: 'local/retired'}},
    }})
  })
  const client = createRemote(connection)
  try {
    const result = await client[endpoint](args)
    assert.equal(result.error.code, 'business/refused')
    assert.deepEqual(result.error.details, {value: 3})
    assert.equal(Object.hasOwn(result.error, 'cause'), false, 'wire causes are not local errors')
  } finally {
    await client.dispose()
    await connection.dispose()
  }
}
