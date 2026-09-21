import assert from 'node:assert/strict';

// Only the host clock is controlled. The imported TimerService is the unmodified vendored source.
export async function runTimerProbes(Context, TimerService) {
  const originals = { setTimeout, clearTimeout, setInterval, clearInterval, now: Date.now };
  let now = 0, nextId = 0;
  const timers = new Map();
  const schedule = (callback, delay, repeat, args) => {
    const id = ++nextId; timers.set(id, { callback, due: now + delay, repeat, args }); return id;
  };
  globalThis.setTimeout = (cb, delay, ...args) => schedule(cb, delay, 0, args);
  globalThis.setInterval = (cb, delay, ...args) => schedule(cb, delay, delay, args);
  globalThis.clearTimeout = globalThis.clearInterval = id => { timers.delete(id); };
  Date.now = () => now;
  function advance(ms) {
    now += ms;
    for (const [id, timer] of [...timers]) {
      if (timer.due > now || !timers.has(id)) continue;
      if (timer.repeat) timer.due = now + timer.repeat; else timers.delete(id);
      timer.callback(...timer.args);
    }
  }
  const cases = [];
  try {
    let ctx = new Context();
    try {
      new TimerService(ctx); const trace = [];
      ctx.timer.timeout(() => { trace.push('timeout'); }, 50);
      const pending = ctx.timer.timeout(100).then(() => { throw new Error('must cancel'); }, () => { trace.push('cancelled'); });
      const debounce = ctx.timer.debounce(value => { trace.push(`debounce:${value}`); }, 10);
      debounce(1); debounce(2); advance(10); advance(40);
      await ctx.fiber.dispose(); await pending;
      assert.deepEqual(trace, ['debounce:2', 'timeout', 'cancelled']);
      cases.push({ id: 'C26-owned-timers-and-debounce', trace });
    } finally { await ctx.fiber.dispose(); }
    ctx = new Context();
    try {
      new TimerService(ctx); const trace = [];
      const iterator = ctx.timer.interval(10);
      advance(20); let settled = false;
      const next = iterator.next().then(result => { settled = true; return result; });
      await Promise.resolve(); assert.equal(settled, false);
      advance(10); assert.equal((await next).done, false); trace.push('tick');
      advance(20); const returning = iterator.next();
      await iterator.return(); assert.equal((await returning).done, true); trace.push('return');
      const unenumerated = ctx.timer.interval(10);
      await ctx.fiber.dispose(); await assert.rejects(unenumerated.next()); trace.push('owned-before-next');
      cases.push({ id: 'C27-unbuffered-eager-timer-iterator', trace });
    } finally { await ctx.fiber.dispose(); }
    for (const item of cases) console.error(`PASS ${item.id}`);
    return cases;
  } finally {
    Object.assign(globalThis, { setTimeout: originals.setTimeout, clearTimeout: originals.clearTimeout,
      setInterval: originals.setInterval, clearInterval: originals.clearInterval });
    Date.now = originals.now;
  }
}
