// Shared carrier and contribution lifetimes for generated Cordis.NET clients.
export class RemoteError extends Error {
  constructor(code, message, details = {}) {
    super(message);
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
const failure = (code, message, details) => ({ok:false, error:new RemoteError(code, message, details)});
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

export class TypertConnection {
  #base;
  #fetcher;
  #createSocket;
  #lifetime = new Lifetime();
  #mux;

  constructor(base, fetcher = fetch, createSocket = url => new WebSocket(url)) {
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
    const response = await this.#fetcher(this.#base.href.replace(/\/$/, '') + '/' + endpoint, {
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

  dispose() { return this.#lifetime.dispose(); }
}

class Inbox {
  values = [];
  terminal = false;
  error;
  wake;

  push(value) { if (!this.terminal) { this.values.push(value); this.wake?.(); } }
  end(error) { if (!this.terminal) { this.terminal = true; this.error = error; this.wake?.(); } }
  fail(error) { this.values.length = 0; this.error = error; this.terminal = true; this.wake?.(); }
  async next() {
    while (!this.values.length && !this.terminal) await new Promise(resolve => { this.wake = resolve; });
    this.wake = undefined;
    if (this.values.length) return {done:false, value:this.values.shift()};
    if (this.error) throw this.error;
    return {done:true, value:undefined};
  }
}

class Mux {
  failed = false;
  #socket;
  #ready;
  #calls = new Map();
  #pumps = new Set();
  #complete;
  completion = new Promise(resolve => { this.#complete = resolve; });

  constructor(base, signal, createSocket) {
    const url = new URL(base);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    url.pathname = url.pathname.replace(/\/$/, '') + '/remote.mux';
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
    const closeInput = () => { inputClosed = true; input.fail(); };
    const call = {inbox, terminal, remoteTerminal:false, closeInput};
    this.#calls.set(streamId, call);
    const work = (async () => {
      let opened = false;
      let cancel;
      let pumping;
      try {
        // Serialize before touching the carrier so local failures retain their identity.
        const open = JSON.stringify({type:'open', streamId, endpoint, payload:{args}});
        const cancelled = new Promise((_, reject) => {
          cancel = () => reject(combined.reason);
          combined.addEventListener('abort', cancel, {once:true});
          if (combined.aborted) cancel();
        });
        await Promise.race([this.#ready, cancelled]);
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
    this.#socket.close();
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
  for (const [endpoint, stream, uplink] of methods) {
    client[endpoint] = (args, signal) => {
      if (!state.active) {
        if (stream) throw new Error('The Remote contribution was withdrawn.');
        return Promise.resolve(withdrawn());
      }
      if (!connection?.active) throw new Error('Remote ' + endpoint + ' has no active Connection.');
      const combined = AbortSignal.any([state.abort.signal, ...(signal ? [signal] : [])]);
      if (stream) {
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
        try {
          const result = await connection.call(endpoint, args, combined);
          if (!state.active) return withdrawn();
          combined.throwIfAborted();
          return result;
        } catch (error) {
          return failure(combined.aborted ? 'gateway/cancelled' : 'gateway/internal', String(error));
        }
      })());
    };
  }
  return client;
}

export async function mountClient(ctx, remote, namespaceSpecs) {
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
          for (const [name, endpoint] of methods) service[name] = remote[endpoint];
          group = {service, dispose:ctx.root.provide(key, service)};
          table.set(key, group);
        }
        const names = [];
        installed.push([key, group, names]);
        for (const [name, endpoint] of methods) { group.service[name] = remote[endpoint]; names.push(name); }
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
