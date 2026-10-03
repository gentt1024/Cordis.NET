import assert from 'node:assert/strict';

async function entryGenerations(Context, Loader, Schema) {
  const trace = []; const ctx = new Context(); const references = [];
  let applies = 0, notices = 0;
  const raw = (limit, ordinary = 'same') => ({ limit, ordinary });
  const plugin = { Config: Schema.object({ limit: Schema.number().min(1).volatile(), ordinary: Schema.string() }),
    apply(owner, config) {
      applies++; references.push(config.limit);
      owner.on('loader/volatile-update', () => { notices++; });
    } };
  try {
    ctx.logger.exporter({ export(message) { if (message.type === 'error') console.error(...message.args); } });
    const loaderFiber = ctx.plugin(Loader).ctx.fiber; await loaderFiber.await();
    const loader = ctx.loader; loader.builtins.upgrade = plugin;
    await loader.root.update([{ id: 'p', name: 'cordis:upgrade', config: raw(1) }]); await loader.await();
    const entry = loader.resolve('p'), first = entry.fiber, effective = first.config;
    trace.push(`initial:${references[0].get()};apply:${applies}`);
    await entry.update({ config: raw(2) }); await loader.await();
    assert.equal(entry.fiber, first); assert.equal(first.config, effective);
    trace.push(`live:${references[0].get()};apply:${applies};notice:${notices}`);
    await entry.update({ config: raw(-1) }); await loader.await();
    trace.push(`invalid-raw:${entry.fiber._config.limit};live:${references[0].get()};apply:${applies}`);
    await entry.update({ config: raw(3, 'changed') }); await loader.await();
    trace.push(`mixed:old:${references[0].get()};new:${references[1].get()};apply:${applies}`);
    entry.fiber.update(raw(4, 'changed'), true); await loader.await();
    trace.push(`no-save:saved:${entry.options.config.limit};old:${references[1].get()};new:${references[2].get()};apply:${applies}`);
    await entry.update({ config: raw(5, 'changed') }); await loader.await();
    trace.push(`after-no-save:old:${references[0].get()},${references[1].get()};new:${references[2].get()};apply:${applies};notice:${notices}`);
    return [{ id: 'V01-volatile-entry-generations', trace }];
  } finally { await ctx.fiber.dispose(); }
}

export async function runVolatileUpgradeScenarios(Context, Loader, Schema) {
  const cases = await entryGenerations(Context, Loader, Schema);
  const ctx = new Context(); let map, b, changes;
  try {
    const loaderFiber = ctx.plugin(Loader).ctx.fiber; await loaderFiber.await(); const loader = ctx.loader;
    loader.builtins.upgrade = { Config: Schema.object({ map: Schema.object({ n: Schema.number() }).volatile(), b: Schema.number().volatile() }),
      apply(owner, config) { map = config.map; b = config.b; owner.on('loader/volatile-update', paths => { changes = paths.map(path => path.join('/')); }); } };
    const raw = b => ({ map: { n: 1 }, b });
    await loader.root.update([{ id: 'p', name: 'cordis:upgrade', config: raw(1) }]); await loader.await();
    const old = map.get(); await loader.resolve('p').update({ config: raw(2) }); await loader.await();
    assert.equal(map.get(), old);
    cases.push({ id: 'V02-unchanged-snapshot-identity', trace: [`live:map:${map.get().n},b:${b.get()};same-snapshot:true;changed:${changes.join(',')}`] });
    return cases;
  } finally { await ctx.fiber.dispose(); }
}
