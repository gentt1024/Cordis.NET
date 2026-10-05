import { SettingsFormModel, settingsNumberField, type SettingsFormPathOp, type SettingsFormScopeSnapshot } from './vendor/form-model'

interface LiveView {
  version: 1
  mode: 'primitive-live-set'
  revision: string
  fields: { name: string; kind: string; value: unknown; overridden: boolean }[]
  activations: number
}

/** Client draft/cache adapter. ProfileSession remains the only configuration authority. */
export class LiveSettingsScope {
  private snapshot: SettingsFormScopeSnapshot<Record<string, unknown>> = {
    status: 'loading', value: undefined, base: undefined, user: undefined, writable: false, revision: undefined,
  }
  private readonly listeners = new Set<() => void>()
  private readonly revisions = new Map<number, string>()
  private nextRevision = 0
  private offered = new Set<string>()
  constructor(private readonly origin: string) {}
  getSnapshot = (): SettingsFormScopeSnapshot<Record<string, unknown>> => this.snapshot
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
  async refresh(): Promise<LiveView> {
    const response = await fetch(this.origin + '/settings')
    if (!response.ok) throw new Error('Settings unavailable')
    const view: LiveView = await response.json()
    if (view.version !== 1 || view.mode !== 'primitive-live-set' || typeof view.revision !== 'string') throw new Error('Unsupported live view')
    // The local number identifies the hash observed at this instant. Drafts keep their older token.
    const revision = ++this.nextRevision
    this.revisions.set(revision, view.revision)
    this.offered = new Set(view.fields.map(field => field.name))
    this.snapshot = {
      status: 'ready', value: Object.fromEntries(view.fields.map(field => [field.name, field.value])),
      base: undefined, user: Object.fromEntries(view.fields.filter(field => field.overridden).map(field => [field.name, field.value])),
      writable: true, revision,
    }
    for (const listener of this.listeners) listener()
    return view
  }
  async mutate(ops: readonly SettingsFormPathOp[], expectedRevision?: number): Promise<boolean> {
    // SET-only: do not split an atomic upstream request into partial durable writes.
    if (ops.length !== 1 || ops[0].op !== 'set' || ops[0].path.length !== 1
      || !this.offered.has(ops[0].path[0]) || expectedRevision === undefined) return false
    const revision = this.revisions.get(expectedRevision)
    if (revision === undefined) return false
    const response = await fetch(this.origin + '/settings', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ field: ops[0].path[0], value: ops[0].value, revision }),
    })
    const result: { saved: boolean; applied: boolean } = await response.json()
    await this.refresh()
    return response.ok && result.saved && result.applied
  }
  dispose(): void { this.listeners.clear(); this.revisions.clear() }
}

function check(condition: boolean, contract: string): asserts condition {
  if (!condition) throw new Error('application client: ' + contract)
}

/** Actual unchanged upstream form model over a running native HTTP host. */
export async function runApplicationClient(origin: string): Promise<string> {
  const scope = new LiveSettingsScope(origin)
  const initial = await scope.refresh()
  const offered = initial.fields.map(field => field.name)
  check(offered.length === 1 && offered[0] === 'limit' && !JSON.stringify(initial).includes('SYNTHETIC_SECRET_MUST_NOT_LEAK'), 'live view excludes ordinary and secret values')
  const model = new SettingsFormModel(scope, [settingsNumberField('limit')])
  const rendered = model.bind(() => model.field('limit'))
  try {
    model.actions().edit('limit', '2')
    check(rendered.getSnapshot().text === '2' && model.shell().dirty, 'actual snapshot store renders staged draft')
    check((await scope.refresh()).fields[0].value === 1, 'staging does not persist')
    await model.save()
    check(!model.shell().failed && !model.shell().dirty && (await scope.refresh()).fields[0].value === 2, 'save commits live value')
    model.actions().edit('limit', '-1')
    await model.save()
    check(model.shell().failed && model.field('limit').text === '-1' && (await scope.refresh()).fields[0].value === 2, 'host validation refuses and retains draft')
    model.actions().discard()
    model.actions().edit('limit', '4')
    const peer = new LiveSettingsScope(origin)
    await peer.refresh()
    check(await peer.mutate([{ op: 'set', path: ['limit'], value: 3 }], peer.getSnapshot().revision), 'peer accepts correct revision')
    await scope.refresh()
    await model.save()
    check(model.shell().failed && model.field('limit').text === '4' && (await scope.refresh()).fields[0].value === 3, 'draft keeps original revision fence after refresh')
    check(!await scope.mutate([{ op: 'unset', path: ['limit'] }], scope.getSnapshot().revision), 'unset is explicitly unsupported')
    check(!await scope.mutate([{ op: 'set', path: ['limit'], value: 5 }, { op: 'set', path: ['limit'], value: 6 }], scope.getSnapshot().revision), 'multi-op refusal has no partial write')
    const view = await scope.refresh()
    check(view.fields[0].value === 3 && view.activations === 1, 'refusals preserve activation and committed value')
    for (const field of ['label', 'token', 'unoffered']) {
      const result = await fetch(origin + '/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ field, value: 'unauthorized', revision: view.revision }) })
      check((await result.json()).error === 'field-not-offered', 'host enforces field policy on direct submission')
    }
    peer.dispose()
    return 'application client scenario passed'
  } finally { model.dispose(); scope.dispose() }
}
