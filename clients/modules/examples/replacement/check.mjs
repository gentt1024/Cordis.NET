function check(value, message) { if (!value) throw new Error(message) }
function deferred() { let resolve; const promise = new Promise(done => { resolve = done }); return { promise, resolve } }

export function bundleSource(request) {
  const url = new URL(request, 'http://localhost')
  const id = url.pathname.split('/').at(-1).replace('.js', '')
  const revision = url.searchParams.get('rev')
  return `globalThis.__ModuleLoader__.load({id:${JSON.stringify(id)},factory(require) {
    const id = ${JSON.stringify(id)}, revision = ${JSON.stringify(revision)};
    const probe = require('@fixture/probe');
    probe.factories.push(id + ':' + revision);
    if (id === 'subject' && revision === 'v2' && probe.failure === 'import') throw new Error('intentional import rejection');
    const read = id === 'dependent' ? require('subject').read : () => revision;
    if (typeof document.createElement === 'function') {
      const style = document.createElement('style'); style.setAttribute('data-plugin', id);
      document.head.append(style);
    }
    return {read, inject:id === 'dependent' ? ['subject'] : [], async apply(ctx) {
      const key = id + ':' + revision;
      probe.starts.push(key);
      ctx.effect(() => () => {probe.stops.push(key)});
      if (id === 'subject' && revision === 'v2') {
        if (probe.pause?.stage === 'startup') {probe.pause.entered.resolve(); await probe.pause.release.promise;}
        if (probe.failure === 'startup') throw new Error('intentional startup rejection');
      }
      ctx.provide(id, {read});
    }};
  }});`
}

function graph(revision) {
  const entries = [
    {id:'stable', rev:'stable', external:['@fixture/probe'], inject:[]},
    {id:'subject', rev:revision, external:['@fixture/probe'], inject:[]},
    {id:'dependent', rev:'dependent', external:['@fixture/probe', 'subject'], inject:['subject']},
  ].map(row => ({...row, url:'/replacement/' + row.id + '.js?rev=' + row.rev}))
  return {rev:revision, entries, batches:entries.map(row => ({phase:'application', rev:row.rev, url:row.url, entries:[row.id]}))}
}

// The installed public bootstrap owns code identity, retry, dependencies and cleanup.
// The supplied delivery function executes the same neutral factory scripts in Node or a real browser.
export async function verifyReplacement(bootClientModules, deliver, extended = true) {
  const results = []
  async function fixture() {
    const probe = {loads:[], factories:[], starts:[], stops:[], failure:null, pause:null}
    const client = await bootClientModules({graph:graph('v1'), staticModules:{'@fixture/probe':probe},
      async loadBundle(request) {
        const url = new URL(request, 'http://localhost')
        probe.loads.push(url.pathname + url.search)
        if (url.pathname.endsWith('/subject.js') && url.searchParams.get('rev') === 'v2') {
          if (probe.pause?.stage === 'prefetch') {probe.pause.entered.resolve(); await probe.pause.release.promise}
          if (probe.failure === 'prefetch') throw new Error('intentional download rejection')
        }
        await deliver(request)
      },
    })
    const stable = client.context.get('stable')
    const count = (list, key) => list.filter(value => value === key).length
    function active(revision) {
      check(client.context.get('subject')?.read() === revision, 'Wrong subject exports for ' + revision)
      check(client.context.get('dependent')?.read() === revision, 'Dependent retained the wrong code exports')
      check(client.context.get('stable') === stable && count(probe.starts, 'stable:stable') === 1 &&
        count(probe.stops, 'stable:stable') === 0, 'Unrelated owner restarted or was cleaned early')
      check(client.state.getSnapshot().failures.length === 0, 'Recovered graph still reports failure')
    }
    async function dispose() {
      await client.dispose()
      for (const key of new Set(probe.starts))
        check(count(probe.starts, key) === count(probe.stops, key), 'Unsettled effects for ' + key)
      check(globalThis.__ModuleLoader__ === undefined, 'Disposed page retained the facade')
      check(document.querySelectorAll('style[data-plugin]').length === 0, 'Disposed page retained module styles')
    }
    active('v1')
    return {probe, client, active, dispose, count}
  }

  for (const failure of extended ? ['startup', 'import', 'prefetch'] : ['startup']) {
    const test = await fixture()
    const {probe, client, active, dispose, count} = test
    try {
      probe.failure = failure
      await client.sync(graph('v2'))
      check(client.state.getSnapshot().failures.some(row => row.id === 'subject'), 'Target failure disappeared')
      const failedStarts = count(probe.starts, 'subject:v2')
      if (failure === 'prefetch') {
        check(client.context.get('subject')?.read() === 'v1' && count(probe.stops, 'subject:v1') === 0,
          'Download failure withdrew the usable old Fiber')
      } else check(count(probe.stops, 'subject:v1') === 1, 'Replacement did not release the old Fiber')
      if (failure === 'startup') check(count(probe.stops, 'subject:v2') === failedStarts, 'Failed startup leaked its effects')
      await client.sync(graph('v1'))
      active('v1')
      await client.retry()
      active('v1')
      check(count(probe.starts, 'subject:v2') === failedStarts, 'Returning to V1 executed the failed V2 again')
      if (failure === 'startup') {
        await client.sync(graph('v2'))
        probe.failure = null
        await client.retry()
        active('v2')
        check(probe.factories.filter(value => value === 'subject:v2').length === 2,
          'Same-version retry reimported the installed subject')
      }
    } finally { await dispose() }
    results.push({scenario:failure, ...probe})
  }

  for (const stage of extended ? ['prefetch', 'startup'] : []) for (const action of ['new-graph', 'dispose']) {
    const {probe, client, active, dispose, count} = await fixture()
    probe.pause = {stage, entered:deferred(), release:deferred()}
    try {
      const pending = client.sync(graph('v2'))
      await probe.pause.entered.promise
      const next = action === 'new-graph' ? client.sync(graph('v1')) : client.dispose()
      probe.pause.release.resolve()
      await Promise.all([pending, next])
      if (action === 'new-graph') { active('v1'); await client.retry(); active('v1') }
      if (stage === 'prefetch') check(count(probe.starts, 'subject:v2') === 0, 'Late arrival activated an obsolete target')
    } finally { probe.pause.release.resolve(); await dispose() }
    results.push({scenario:stage + '-' + action, ...probe, pause:undefined})
  }
  return results
}
