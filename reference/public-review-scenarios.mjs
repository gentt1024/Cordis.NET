import assert from 'node:assert/strict';

export async function runPublicReviewScenarios(Context, Loader, Schema) {
  const cases = [];
  let ctx = new Context();
  try {
    await ctx.plugin(Loader).ctx.fiber.await();
    const loader = ctx.loader, refs = [];
    loader.builtins.review = {
      Config: Schema.object({ ordinary: Schema.number(), live: Schema.number().volatile() }),
      apply(_owner, config) { refs.push(config.live); },
    };
    const raw = (live, source = '1') => ({ ordinary: { __jsExpr: source }, live });
    await loader.root.update([{ id: 'p', name: 'cordis:review', config: raw(1) }]); await loader.await();
    const entry = loader.resolve('p'), effective = entry.fiber.config, trace = [];
    await entry.update({ config: JSON.parse('{"ordinary":{"__jsExpr":"1"},"live":1}') }); await loader.await();
    assert.equal(entry.fiber.config, effective);
    trace.push(`same-source:apply:${refs.length};live:${refs[0].get()}`);
    await entry.update({ config: raw(2) }); await loader.await();
    assert.equal(entry.fiber.config, effective);
    trace.push(`live:apply:${refs.length};value:${refs[0].get()}`);
    await entry.update({ config: raw(2, '1 + 0') }); await loader.await();
    trace.push(`changed-source:apply:${refs.length};old:${refs[0].get()};new:${refs.at(-1).get()}`);
    cases.push({ id: 'V03-expression-source', trace });
  } finally { await ctx.fiber.dispose(); }

  ctx = new Context();
  try {
    function tree() { return Schema.object({ name: Schema.string(), children: Schema.array(Schema.lazy(tree)).default([]) }); }
    let value = { name: 'leaf', children: [] };
    for (let i = 0; i < 3; i++) value = { name: 'branch', children: [value] };
    let names;
    const fiber = ctx.plugin({ Config: tree(), apply(_owner, config) {
      names = []; let node = config;
      while (node) { names.push(node.name); node = node.children[0]; }
    } }, value).ctx.fiber;
    await fiber.await();
    assert.deepEqual(names, ['branch', 'branch', 'branch', 'leaf']);
    cases.push({ id: 'V04-finite-lazy-tree', trace: [`tree:${names.join(',')};active:true`] });
  } finally { await ctx.fiber.dispose(); }

  ctx = new Context();
  try {
    await ctx.plugin(Loader).ctx.fiber.await();
    const loader = ctx.loader; let applies = 0, updates = 0;
    loader.builtins.review = {
      Config: Schema.object({ nested: Schema.object({ fixed: Schema.string() }).default({ fixed: 'default' }) }),
      apply(owner) { applies++; owner.on('internal/config', (_raw, next) => { updates++; return next(); }); },
    };
    await loader.root.update([{ id: 'p', name: 'cordis:review', config: {} }]); await loader.await();
    const entry = loader.resolve('p'), effective = entry.fiber.config, raw = { nested: { fixed: 'default' } };
    await entry.update({ config: raw }); await loader.await();
    assert.equal(entry.fiber.config, effective); assert.equal(entry.fiber._config, raw); assert.equal(updates, 0);
    const trace = [`default:apply:${applies};same:true;config-hooks:${updates}`];
    await entry.update({ config: { nested: { fixed: 'changed' } } }); await loader.await();
    trace.push(`changed:apply:${applies}`);
    cases.push({ id: 'V05-zero-reference-default', trace });
  } finally { await ctx.fiber.dispose(); }
  return cases;
}
