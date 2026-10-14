// Shared carrier and contribution lifetimes for generated Cordis.NET clients.
export class RemoteError extends Error {
  constructor(code, message, details = {}, options) {
    super(message, options);
    this.name = 'RemoteError';
    this.isDSHRemoteError = true;
    this.code = code;
    this.details = details;
  }
}

export class RemoteStreamCarrierError extends Error {
  constructor(message, cause) {
    super(message, {cause});
    this.name = 'RemoteStreamCarrierError';
  }
}

const record = value => typeof value === 'object' && value !== null && !Array.isArray(value);
const failure = (code, message, details, options) => ({ok:false, error:new RemoteError(code, message, details, options)});
const withdrawn = () => failure('gateway/internal', 'The Remote contribution was withdrawn.');

// Fixed DSH typert/protocol json-value: allow only lossless JSON and top-level absence.
function uplinkValue(value, ancestors = new Set()) {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return true;
  if (typeof value === 'number') return Number.isFinite(value) && !Object.is(value, -0);
  if (typeof value !== 'object' || ancestors.has(value)) return false;
  ancestors.add(value);
  try {
    if (Array.isArray(value)) {
      if (Object.getPrototypeOf(value) !== Array.prototype || Reflect.ownKeys(value).length !== value.length + 1) return false;
      for (let index = 0; index < value.length; index++)
        if (!Object.hasOwn(value, index) || !uplinkValue(value[index], ancestors)) return false;
      return true;
    }
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) return false;
    for (const key of Reflect.ownKeys(value))
      if (typeof key !== 'string' || Object.getOwnPropertyDescriptor(value, key)?.enumerable !== true || !uplinkValue(value[key], ancestors)) return false;
    return true;
  } finally { ancestors.delete(value); }
}

class Lifetime {
  active = true;
  abort = new AbortController();
  pending = new Set();
  disposal;

  track(work) {
    this.pending.add(work);
    work.then(() => this.pending.delete(work), () => this.pending.delete(work));
    return work;
  }

  dispose() {
    if (!this.disposal) {
      this.active = false;
      this.disposal = Promise.resolve().then(async () => {
        this.abort.abort();
        await Promise.allSettled(this.pending);
      });
    }
    return this.disposal;
  }
}

function parseResult(result) {
  if (!record(result) || typeof result.ok !== 'boolean') throw new Error('Invalid Remote result.');
  if (result.ok) return {ok:true, value:result.value};
  const error = result.error;
  if (!record(error) || typeof error.code !== 'string' || typeof error.message !== 'string' || !record(error.details))
    throw new Error('Invalid Remote error.');
  return failure(error.code, error.message, error.details);
}

async function binaryResponse(response) {
  const body = await response.formData();
  const fields = new Map();
  for (const [name, value] of body) {
    if (fields.has(name)) throw new TypeError('Invalid Remote binary fields.');
    fields.set(name, value);
  }
  const metadata = fields.get('metadata');
  fields.delete('metadata');
  if (typeof metadata !== 'string') throw new TypeError('Missing Remote binary metadata.');
  const envelope = JSON.parse(metadata);
  if (!record(envelope) || !parseResult(envelope.result).ok ||
      !Array.isArray(envelope.attachments) || !envelope.attachments.length)
    throw new TypeError('Invalid Remote binary result.');
  for (const attachment of envelope.attachments) {
    if (!record(attachment) || attachment.codec !== 'bytes' || typeof attachment.part !== 'string' ||
        !Array.isArray(attachment.path)) throw new TypeError('Invalid Remote binary attachment.');
    const bytes = fields.get(attachment.part);
    fields.delete(attachment.part);
    if (!(bytes instanceof Blob)) throw new TypeError('Invalid Remote binary fields.');
    let parent = envelope.result;
    let key = 'value';
    for (const segment of attachment.path) {
      const value = parent[key];
      if (typeof value !== 'object' || value === null ||
          (Array.isArray(value) ? typeof segment !== 'number' || !Number.isSafeInteger(segment) ||
            segment < 0 || segment >= value.length : typeof segment !== 'string') ||
          !Object.hasOwn(value, segment)) throw new TypeError('Invalid Remote binary path.');
      parent = value;
      key = segment;
    }
    if (parent[key] !== null) throw new TypeError('Invalid Remote binary placeholder.');
    Object.defineProperty(parent, key, {value:new Uint8Array(await bytes.arrayBuffer()),
      enumerable:true, writable:true, configurable:true});
  }
  if (fields.size) throw new TypeError('Unexpected Remote binary fields.');
  return envelope;
}

const eventLifetime = Symbol('event-lifetime');

export class TypertConnection {
  #base;
  #fetcher;
  #createSocket;
  #lifetime = new Lifetime();
  #mux;

  constructor(base, fetcher = globalThis.fetch.bind(globalThis), createSocket = url => new WebSocket(url)) {
    this.#base = new URL(base);
    this.#fetcher = fetcher;
    this.#createSocket = createSocket;
  }

  get active() { return this.#lifetime.active; }

  call(endpoint, args, signal) {
    if (!this.active) throw new Error('Remote has no active Connection.');
    const combined = AbortSignal.any([this.#lifetime.abort.signal, ...(signal ? [signal] : [])]);
    return this.#lifetime.track(this.#call(endpoint, args, combined));
  }

  async #call(endpoint, args, signal) {
    const rpcId = crypto.randomUUID();
    const send = this.#fetcher;
    const response = await send(this.#base.href.replace(/\/$/, '') + '/' + endpoint, {
      method:'POST', headers:{'content-type':'application/json'}, signal,
      body:JSON.stringify({type:'client-request', rpcId, method:endpoint, payload:{args}}),
    });
    if (!response.ok) throw new Error('Remote HTTP ' + response.status);
    const envelope = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase() === 'multipart/form-data'
      ? await binaryResponse(response) : await response.json();
    if (!record(envelope) || envelope.type !== 'server-response' || envelope.rpcId !== rpcId)
      throw new Error('Invalid or uncorrelated Remote response.');
    return parseResult(envelope.result);
  }

  open(endpoint, args, signal, uplink = false) {
    if (!this.active) throw new Error('Remote has no active Connection.');
    if (!this.#mux || this.#mux.failed) {
      this.#mux = new Mux(this.#base, this.#lifetime.abort.signal, this.#createSocket);
      this.#lifetime.track(this.#mux.completion);
    }
    return this.#mux.open(endpoint, args, signal, uplink);
  }

  [eventLifetime](start) {
    if (!this.active) throw new Error('Remote has no active Connection.');
    return this.#lifetime.track(start(this.#lifetime.abort.signal));
  }

  dispose() { return this.#lifetime.dispose(); }
}

class Inbox {
  values = [];
  terminal = false;
  failed = false;
  error;
  wake;

  push(value) { if (!this.terminal) { this.values.push(value); this.wake?.(); } }
  end(error) { if (!this.terminal) { this.terminal = true; this.failed = error !== undefined; this.error = error; this.wake?.(); } }
  fail(error) { this.values.length = 0; this.error = error; this.failed = true; this.terminal = true; this.wake?.(); }
  close() { this.values.length = 0; this.end(); }
  async next() {
    while (!this.values.length && !this.terminal) await new Promise(resolve => { this.wake = resolve; });
    this.wake = undefined;
    if (this.values.length) return {done:false, value:this.values.shift()};
    if (this.failed) throw this.error;
    return {done:true, value:undefined};
  }
}

class Mux {
  failed = false;
  #socket;
  #ready;
  #connect;
  #calls = new Map();
  #pumps = new Set();
  #complete;
  completion = new Promise(resolve => { this.#complete = resolve; });

  constructor(base, signal, createSocket) {
    const url = new URL(base);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    url.pathname = url.pathname.replace(/\/$/, '') + '/remote.mux';
    this.#connect = () => {
      if (this.#ready) return this.#ready;
      this.#socket = createSocket(url);
      this.#ready = new Promise((resolve, reject) => {
        this.#socket.addEventListener('open', resolve, {once:true});
        this.#socket.addEventListener('error', () => reject(new RemoteStreamCarrierError('Remote socket failed.')), {once:true});
        this.#socket.addEventListener('close', () => reject(new RemoteStreamCarrierError('Remote socket closed.')), {once:true});
      });
      this.#ready.catch(error => this.#fail(error));
      this.#socket.addEventListener('message', event => {
        try { this.#dispatch(event.data); }
        catch (error) { this.#fail(new RemoteStreamCarrierError('Invalid Remote frame.', error)); }
      });
      this.#socket.addEventListener('close', () => this.#fail(new RemoteStreamCarrierError('Remote socket closed.')));
      return this.#ready;
    };
    const abort = () => this.#fail(new RemoteStreamCarrierError('Remote Connection closed.'));
    signal.addEventListener('abort', abort, {once:true});
    this.completion.then(() => signal.removeEventListener('abort', abort));
    if (signal.aborted) abort();
  }

  open(endpoint, args, signal, uplink) {
    const streamId = crypto.randomUUID();
    const inbox = new Inbox();
    const abort = new AbortController();
    const combined = AbortSignal.any([abort.signal, ...(signal ? [signal] : [])]);
    let terminal;
    const ended = new Promise(resolve => { terminal = resolve; });
    const input = new Inbox();
    let inputClosed = false;
    const closeInput = () => { inputClosed = true; input.close(); };
    const call = {inbox, terminal, remoteTerminal:false, closeInput};
    this.#calls.set(streamId, call);
    const work = (async () => {
      let opened = false;
      let cancel;
      let pumping;
      try {
        // Serialize before touching the carrier so local failures retain their identity.
        const open = JSON.stringify({type:'open', streamId, endpoint, payload:{args}});
        combined.throwIfAborted();
        const cancelled = new Promise((_, reject) => {
          cancel = () => reject(combined.reason);
          combined.addEventListener('abort', cancel, {once:true});
          if (combined.aborted) cancel();
        });
        await Promise.race([this.#connect(), cancelled]);
        combined.throwIfAborted();
        if (this.failed) throw new RemoteStreamCarrierError('Remote socket is no longer active.');
        this.#send(open);
        opened = true;
        if (uplink) pumping = (async () => {
          try {
            while (!inputClosed && !combined.aborted) {
              const item = await input.next();
              if (inputClosed || combined.aborted) return;
              this.#send(JSON.stringify(item.done ? {type:'end', streamId} : {type:'item', streamId, value:item.value}));
              if (item.done) return;
            }
          } catch (error) {
            if (error instanceof RemoteStreamCarrierError) this.#fail(error);
            else { inbox.fail(error); terminal(); }
          }
        })();
        await Promise.race([ended, cancelled]);
      } catch (error) { inbox.fail(error); }
      finally {
        closeInput();
        if (pumping) await pumping;
        if (cancel) combined.removeEventListener('abort', cancel);
        if (opened && !call.remoteTerminal && !this.failed) {
          try { this.#send(JSON.stringify({type:'cancel', streamId})); }
          catch (error) { this.#fail(error); }
        }
        this.#calls.delete(streamId);
      }
    })();
    this.#pumps.add(work);
    work.then(() => this.#pumps.delete(work));
    return {next:async () => {
      try { const value = await inbox.next(); if (value.done) terminal(); return value; }
      catch (error) { terminal(); throw error; }
    }, completion:work, dispose:async () => { closeInput(); abort.abort(); await work; },
    send:value => {
      if (inputClosed || input.terminal || combined.aborted) throw new Error('The Remote stream uplink is closed.');
      if (value !== undefined && !uplinkValue(value)) throw new Error('Remote uplink item is not a lossless JSON value.');
      input.push(value);
    }, end:() => input.end()};
  }

  #send(text) {
    try { this.#socket.send(text); }
    catch (error) { throw new RemoteStreamCarrierError('Remote socket write failed.', error); }
  }

  #dispatch(data) {
    if (typeof data !== 'string') throw new Error('Remote mux requires text.');
    const frame = JSON.parse(data);
    if (!record(frame) || typeof frame.streamId !== 'string' || !frame.streamId) throw new Error('Invalid stream id.');
    const fields = frame.type === 'item' ? ['type','streamId','value'] :
      frame.type === 'end' ? ['type','streamId'] : frame.type === 'error' ? ['type','streamId','error'] : [];
    if (!fields.length || Object.keys(frame).some(key => !fields.includes(key))) throw new Error('Invalid stream frame.');
    let error;
    if (frame.type === 'error') {
      if (!record(frame.error) || Object.keys(frame.error).some(key => !['code','message','details'].includes(key)))
        throw new Error('Invalid stream failure.');
      error = parseResult({ok:false,error:frame.error}).error;
    }
    const call = this.#calls.get(frame.streamId);
    if (!call) return;
    if (frame.type === 'item') call.inbox.push(frame.value);
    else { call.remoteTerminal = true; call.closeInput(); call.inbox.end(error); }
  }

  #fail(error) {
    if (this.failed) return;
    this.failed = true;
    for (const call of this.#calls.values()) { call.closeInput(); call.inbox.fail(error); call.terminal(); }
    this.#socket?.close();
    Promise.allSettled(this.#pumps).then(this.#complete);
  }
}

class StreamHandle {
  #call;
  #state;
  #consumed = false;
  #disposed = false;
  constructor(call, state) { this.#call = call; this.#state = state; }
  dispose() { this.#disposed = true; return this.#call.dispose(); }
  [Symbol.asyncIterator]() {
    if (this.#consumed) throw new Error('A Remote stream has one consumer.');
    this.#consumed = true;
    const iterator = this.#iterate();
    return {next:() => iterator.next(), return:async () => { await this.dispose(); return iterator.return(); }};
  }
  async *#iterate() {
    try {
      while (true) {
        const next = await this.#call.next();
        if (this.#disposed || next.done) return;
        if (!this.#state.active) throw new Error('The Remote contribution was withdrawn.');
        yield next.value;
      }
    } catch (error) { if (!this.#disposed) throw error; }
    finally { await this.dispose(); }
  }
}

export function createClient(connection, methods) {
  const state = new Lifetime();
  const client = {dispose:() => state.dispose()};
  for (const [endpoint, stream, uplink, required, projection] of methods) {
    client[endpoint] = (args, signal, binding) => {
      if (!state.active) {
        if (stream) throw new Error('The Remote contribution was withdrawn.');
        return Promise.resolve(withdrawn());
      }
      const adapter = binding && projection ? binding.adapters?.getClient(projection[0]) : undefined;
      const identity = adapter?.identity(binding.caller);
      const prepare = () => {
        if (binding && projection) {
          const [key, wire, direct] = projection;
          if (identity !== undefined) args = {...args, [wire]:identity};
          else if (!direct) throw new Error(!adapter
              ? 'Remote ' + endpoint + ' has no Client Context adapter for ' + key
              : 'Remote ' + endpoint + ' requires a ' + key + ' Context.');
        }
        if (!record(args)) throw new Error('Remote ' + endpoint + ' requires a named argument object.');
        for (const wire of required)
          if (!Object.hasOwn(args, wire)) throw new Error('Remote ' + endpoint + ' requires argument ' + JSON.stringify(wire) + '.');
        if (!connection?.active) throw new Error('Remote ' + endpoint + ' has no active Connection.');
        return AbortSignal.any([state.abort.signal, ...(signal ? [signal] : [])]);
      };
      if (stream) {
        const combined = prepare();
        const call = connection.open(endpoint, args, combined, uplink);
        state.track(call.completion);
        const handle = new StreamHandle(call, state);
        if (uplink) {
          handle.send = value => { if (!state.active) throw new Error('The Remote contribution was withdrawn.'); call.send(value); };
          handle.end = () => call.end();
        }
        return handle;
      }
      return state.track((async () => {
        const combined = prepare();
        try {
          const result = await connection.call(endpoint, args, combined);
          if (!state.active) return withdrawn();
          combined.throwIfAborted();
          return result;
        } catch (error) {
          return failure(combined.aborted ? 'gateway/cancelled' : 'gateway/internal', String(error), {}, {cause:error});
        }
      })());
    };
  }
  return client;
}

export async function mountClient(ctx, remote, namespaceSpecs, adapters) {
  const tableKey = Symbol.for('cordis.net.typert.remoteNamespaces');
  const registry = ctx.root[tableKey] ??= {groups:new Map(), retiring:new Map(), tail:Promise.resolve()};
  const table = registry.groups;
  const enqueue = operation => { const result = registry.tail.then(operation); registry.tail = result.catch(() => {}); return result; };
  const dispose = ctx.effect(async () => {
    let outcome;
    do {
    outcome = await enqueue(() => {
    const retirements = namespaceSpecs.map(([key]) => registry.retiring.get(key)).filter(Boolean);
    if (retirements.length) return {wait:Promise.all(retirements)};
    const installed = [];
    const cleanup = async () => {
      const closing = remote.dispose();
      const settlements = [];
      for (const [key, group, names] of installed.reverse()) {
        for (const name of names) delete group.service[name];
        if (!Object.keys(group.service).length && table.get(key) === group) {
          table.delete(key);
          const settlement = Promise.resolve().then(() => group.dispose());
          registry.retiring.set(key, settlement);
          const release = () => { if (registry.retiring.get(key) === settlement) registry.retiring.delete(key); };
          settlement.then(release, release);
          settlements.push(settlement);
        }
      }
      await Promise.all([...settlements, closing]);
    };
    try {
      for (const [key, methods] of namespaceSpecs) {
        const group = table.get(key);
        if (!group && ctx.root.get(key) !== undefined) throw new Error('Remote namespace is already provided: ' + key);
        for (const [name] of methods) if (group && Object.hasOwn(group.service, name)) throw new Error('Remote method is already mounted: ' + key + '.' + name);
      }
      for (const [key, methods] of namespaceSpecs) {
        let group = table.get(key);
        if (!group) {
          const service = Object.create(null);
          Object.defineProperties(service, {
            ctx:{value:ctx.root, configurable:true},
            [Symbol.for('cordis.tracker')]:{value:{associate:key, property:'ctx'}},
          });
          group = {service, dispose:undefined};
        }
        const names = [];
        installed.push([key, group, names]);
        for (const [name, endpoint] of methods) {
          Object.defineProperty(group.service, name, {configurable:true, enumerable:true, get() {
            const caller = this.ctx;
            return (args, signal) => remote[endpoint](args, signal, {caller, adapters});
          }});
          names.push(name);
        }
        if (!group.dispose) {
          group.dispose = ctx.root.provide(key, group.service);
          table.set(key, group);
        }
      }
      return {cleanup};
    } catch (error) { return {error, settlement:cleanup()}; }
    });
    if (outcome.wait) await outcome.wait;
    } while (outcome.wait);
    if (outcome.error) { await outcome.settlement; throw outcome.error; }
    return outcome.cleanup;
  });
  await dispose;
  return {remote, dispose};
}

// Fixed DSH owned-value identity also works across independently bundled providers.
export const TYPERT_OWNED_VALUE = Symbol.for('dsh.typert.owned-value');
export function typertOwnedValue(value, release) {
  let active = true;
  return { [TYPERT_OWNED_VALUE]: true, value, [Symbol.dispose]() {
    if (!active) return;
    active = false;
    release();
  }};
}

const eventNext = Symbol('remote-event.next');
const exactEvent = (value, keys) => record(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const eventText = value => typeof value === 'string' && value.length > 0;

export function createEventsClient(owner, connection, selection, adapters) {
  const selected = new Set(selection);
  const prefix = 'internal/remote-events/' + crypto.randomUUID() + '/';
  const lifetime = new Lifetime();
  let running = false;
  const report = (name, error) => console.error('Remote event ' + name, error);
  async function answer(frame, clientId, signal) {
    let resolved;
    try { resolved = adapters?.getClient('agent')?.resolve(frame.agentId); }
    catch (error) { report(frame.event, error); }
    const owned = resolved?.[TYPERT_OWNED_VALUE] === true ? resolved : undefined;
    try {
      const target = owned ? owned.value : resolved;
      let outcome = {kind:'next'};
      if (target !== undefined && target !== null) {
        try {
          const result = await target.waterfall(target, prefix + frame.event,
            {...frame.request, agent:target, signal}, async () => eventNext);
          if (result !== eventNext) {
            if (result !== undefined && !uplinkValue(result)) throw new Error('Remote event result must be lossless JSON.');
            outcome = result === undefined ? {kind:'result'} : {kind:'result',value:result};
          }
        } catch (error) {
          if (signal.aborted) return;
          const rejection = {name: typeof error?.name === 'string' ? error.name : 'Error',
            message: typeof error?.message === 'string' ? error.message : String(error)};
          if (typeof error?.code === 'string') rejection.code = error.code;
          if (error?.details !== undefined && uplinkValue(error.details)) rejection.details = error.details;
          outcome = {kind:'rejected',error:rejection};
        }
      }
      if (signal.aborted) return;
      const response = await connection.call('$events/result', {clientId,eventId:frame.eventId,outcome}, signal);
      if (!response.ok) throw response.error;
    } finally { owned?.[Symbol.dispose](); }
  }
  async function pump(signal, ready) {
    const failed = new AbortController();
    const combined = AbortSignal.any([signal,failed.signal]);
    const tasks = new Set();
    const active = new Map();
    const source = connection.open('$events', {}, combined);
    let clientId;
    try {
      for await (const frame of { [Symbol.asyncIterator]: () => source }) {
        if (clientId === undefined) {
          if (!exactEvent(frame,['type','clientId','host']) || frame.type !== 'ready' || !eventText(frame.clientId) ||
              !exactEvent(frame.host,['home']) || typeof frame.host.home !== 'string') throw new Error('Invalid event readiness.');
          clientId = frame.clientId;
          await ready(frame.host);
        } else if (exactEvent(frame,['type','eventId']) && frame.type === 'cancel' && eventText(frame.eventId)) {
          active.get(frame.eventId)?.abort();
        } else if (exactEvent(frame,['type','event','args']) && frame.type === 'emit' && eventText(frame.event) && Array.isArray(frame.args) && uplinkValue(frame.args)) {
          if (selected.has(frame.event)) void owner.parallel(prefix + frame.event, ...frame.args).catch(error => report(frame.event,error));
        } else if (exactEvent(frame,['type','event','eventId','agentId','request']) && frame.type === 'waterfall' &&
            eventText(frame.event) && eventText(frame.eventId) && eventText(frame.agentId) && record(frame.request) &&
            !Object.hasOwn(frame.request,'agent') && !Object.hasOwn(frame.request,'signal') && uplinkValue(frame.request)) {
          const controller = new AbortController();
          active.set(frame.eventId,controller);
          const delivery = AbortSignal.any([combined,controller.signal]);
          const task = answer(frame,clientId,delivery).catch(error => { if (!delivery.aborted) failed.abort(error); })
            .finally(() => { if(active.get(frame.eventId) === controller) active.delete(frame.eventId); tasks.delete(task); });
          tasks.add(task);
        } else throw new Error('Invalid Remote event frame.');
      }
      if (!signal.aborted) throw new RemoteStreamCarrierError('The Remote event stream ended unexpectedly.');
    } catch(error) {
      if (failed.signal.aborted) throw failed.signal.reason;
      if (!signal.aborted) throw error;
    } finally {
      for (const controller of active.values()) controller.abort();
      await Promise.allSettled(tasks);
      await source.dispose();
    }
  }
  return {
    context: owner,
    on(caller, name, listener) {
      if (!lifetime.active || !selected.has(name)) throw new Error('Remote event is not selected or its Client was withdrawn.');
      if (caller.root !== owner.root) throw new Error('Remote event listeners must share their Client root.');
      return caller.on(prefix + name, listener);
    },
    run(signal, ready) {
      if (!lifetime.active || running) throw new Error('Remote event Client is withdrawn or already running.');
      running = true;
      try {
        return lifetime.track(connection[eventLifetime](carrier => pump(AbortSignal.any([signal,lifetime.abort.signal,carrier]),ready)))
          .finally(() => {running=false;});
      } catch(error) { running=false; throw error; }
    },
    dispose() { return lifetime.dispose(); }
  };
}
