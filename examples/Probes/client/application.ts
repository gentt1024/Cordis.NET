import { createRemote, type RemoteClient } from 'settings-client'
import { SettingsFormModel, settingsNumberField, type SettingsFormPathOp, type SettingsFormScopeSnapshot } from './vendor/form-model'

type DescribeValue = Extract<Awaited<ReturnType<RemoteClient['settings/describe']>>, { ok: true }>['value']
type LiveView = DescribeValue['namespaces'][number]

function objectValue(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('Settings form requires an object')
  return Object.fromEntries(Object.entries(value))
}

/** Client drafts consume the generated standard Settings contract. The profile remains the configuration authority. */
export class LiveSettingsScope {
  private snapshot: SettingsFormScopeSnapshot<Record<string, unknown>> = {
    status: 'loading', value: undefined, base: undefined, user: undefined, writable: false, revision: undefined,
  }
  private readonly listeners = new Set<() => void>()
  private readonly remote: RemoteClient
  constructor(origin: string) { this.remote = createRemote(origin + '/remote') }
  getSnapshot = (): SettingsFormScopeSnapshot<Record<string, unknown>> => this.snapshot
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
  async refresh(): Promise<LiveView> {
    const result = await this.remote['settings/describe']({})
    if (!result.ok) throw result.error
    const view = result.value.namespaces.find(row => row.ns === 'worker')
    if (view === undefined) throw new Error('Settings namespace unavailable')
    this.snapshot = {
      status: 'ready', value: objectValue(view.value), base: view.base, user: view.user,
      writable: result.value.writable, revision: view.revision,
    }
    for (const listener of this.listeners) listener()
    return view
  }
  async mutate(ops: readonly SettingsFormPathOp[], expectedRevision?: number): Promise<boolean> {
    const result = await this.remote['settings/mutate']({ ns: 'worker', ops,
      ...(expectedRevision === undefined ? {} : { expectedRevision }) })
    await this.refresh()
    return result.ok
  }
  dispose(): void { this.listeners.clear(); this.remote.dispose() }
}

function check(condition: boolean, contract: string): asserts condition {
  if (!condition) throw new Error('application client: ' + contract)
}

/** Actual unchanged upstream form model over a running native HTTP host. */
export async function runApplicationClient(origin: string): Promise<string> {
  const scope = new LiveSettingsScope(origin)
  const initial = await scope.refresh()
  const offered = Object.keys(objectValue(initial.value))
  check(offered.length === 1 && offered[0] === 'limit' && !JSON.stringify(initial).includes('SYNTHETIC_SECRET_MUST_NOT_LEAK'), 'live view excludes ordinary and secret values')
  const model = new SettingsFormModel(scope, [settingsNumberField('limit')])
  const rendered = model.bind(() => model.field('limit'))
  try {
    model.actions().edit('limit', '2')
    check(rendered.getSnapshot().text === '2' && model.shell().dirty, 'actual snapshot store renders staged draft')
    check(objectValue((await scope.refresh()).value).limit === 1, 'staging does not persist')
    await model.save()
    check(!model.shell().failed && !model.shell().dirty && objectValue((await scope.refresh()).value).limit === 2, 'save commits live value')
    model.actions().edit('limit', '-1')
    await model.save()
    check(model.shell().failed && model.field('limit').text === '-1' && objectValue((await scope.refresh()).value).limit === 2, 'host validation refuses and retains draft')
    model.actions().discard()
    model.actions().edit('limit', '4')
    const peer = new LiveSettingsScope(origin)
    await peer.refresh()
    check(await peer.mutate([{ op: 'set', path: ['limit'], value: 3 }], peer.getSnapshot().revision), 'peer accepts correct revision')
    await scope.refresh()
    await model.save()
    check(model.shell().failed && model.field('limit').text === '4' && objectValue((await scope.refresh()).value).limit === 3, 'draft keeps original revision fence after refresh')
    check(await scope.mutate([{ op: 'unset', path: ['limit'] }], scope.getSnapshot().revision), 'unset restores inherited value')
    check(objectValue((await scope.refresh()).value).limit === 1, 'reset uses composition inheritance')
    check(await scope.mutate([{ op: 'set', path: ['limit'], value: 5 }, { op: 'set', path: ['limit'], value: 6 }], scope.getSnapshot().revision), 'ordered operations commit one final candidate')
    const view = await scope.refresh()
    const status: unknown = await (await fetch(origin + '/status')).json()
    check(objectValue(view.value).limit === 6 && objectValue(status).activations === 1, 'live writes preserve activation')
    for (const field of ['label', 'token', 'unoffered']) {
      check(!await scope.mutate([{ op: 'set', path: [field], value: 'unauthorized' }], view.revision), 'host enforces field policy on direct submission')
    }
    peer.dispose()
    return 'application client scenario passed'
  } finally { model.dispose(); scope.dispose() }
}
