/// <reference lib="esnext.disposable" />
export declare class RemoteError extends Error {
  readonly isDSHRemoteError: true;
  readonly code: string;
  readonly details: unknown;
  constructor(code: string, message: string, details?: unknown, options?: ErrorOptions);
}
export declare class RemoteStreamCarrierError extends Error {}
export type RemoteResult<T> = {readonly ok: true; readonly value: T} | {readonly ok: false; readonly error: RemoteError};
export interface RemoteStreamHandle<T> extends AsyncIterable<T> { dispose(): Promise<void>; }
export interface RemoteDuplexHandle<TOutput, TInput> extends RemoteStreamHandle<TOutput> {
  send(value: TInput): void;
  end(): void;
}
export declare class TypertConnection {
  constructor(base: string | URL, fetcher?: typeof fetch, createSocket?: (url: URL) => WebSocket);
  readonly active: boolean;
  call(endpoint: string, args: unknown, signal?: AbortSignal): Promise<RemoteResult<unknown>>;
  dispose(): Promise<void>;
}
export interface RemoteOwner {
  readonly root: {get(name: string): unknown; provide(name: string, value: object): () => void | Promise<void>};
  effect(setup: () => Promise<() => Promise<void>>): PromiseLike<unknown> & (() => Promise<void>);
}

/** A borrowed Cordis Context used to select the current Remote service view. */
export interface RemoteCaller { get(name: string): unknown; }
/** Read the current adapter on each invocation; undefined identity means absence, null does not. */
export interface RemoteContextAdapters {
  getClient(key: string): {identity(caller: RemoteCaller): unknown} | undefined;
}

/** Cordis event operations used by the Client; listeners remain owned by their calling Fiber. */
export interface RemoteEventContext {
  readonly root: object;
  on(name: string, listener: (...args: never[]) => unknown): () => unknown;
  parallel(name: string, ...args: unknown[]): Promise<void>;
  waterfall(receiver: RemoteEventContext, name: string, request: object, next: () => Promise<symbol>): unknown;
}
export declare const TYPERT_OWNED_VALUE: unique symbol;
export interface TypertOwnedValue<T> { readonly [TYPERT_OWNED_VALUE]: true; readonly value: T; [Symbol.dispose](): void; }
export declare function typertOwnedValue<T>(value: T, release: () => void): TypertOwnedValue<T>;
export interface RemoteEventAdapters {
  getClient(key: 'agent'): {resolve(identity: string): RemoteEventContext | TypertOwnedValue<RemoteEventContext> | undefined} | undefined;
}
export interface RemoteEventsClient<Events> {
  readonly context: RemoteEventContext;
  on<K extends keyof Events>(caller: RemoteEventContext, event: K, listener: Events[K]): () => unknown;
  run(signal: AbortSignal, ready: (host: {readonly home: string}) => void | Promise<void>): Promise<void>;
  dispose(): Promise<void>;
}


/** A generated parser validates values without loading the owning Service. */
export interface RemoteSchema<T = unknown> { parse(value: unknown): T; }
export interface RemoteValue<T = unknown> {
  readonly create: () => RemoteSchema<T>;
  readonly description?: string;
  readonly jsonSchema?: Readonly<Record<string, unknown>>;
  readonly jsonSchemaError?: string;
}
export interface RemoteCodec<T = unknown> extends RemoteValue<T> {
  readonly mode: 'strict';
  readonly typeSymbol: string;
}
export interface RemoteParameter {
  readonly name: string;
  readonly wire: string;
  readonly source: 'json' | 'lookup';
  readonly lookup?: string;
  readonly acceptsUndefined?: true;
  readonly description?: string;
  readonly codec: RemoteCodec;
  readonly strictCodec: RemoteCodec;
}
export interface RemoteDescriptor {
  readonly id: string;
  readonly service: string;
  readonly namespace: string;
  readonly method: string;
  readonly implementation?: string;
  readonly mode?: 'stream';
  readonly invocation: { readonly kind: 'direct' } | {
    readonly kind: 'context'; readonly context: string; readonly wire: string; readonly codec: RemoteCodec;
  };
  readonly scope?: { readonly context: string; readonly wire: string };
  readonly parameters: readonly RemoteParameter[];
  readonly result: RemoteCodec;
  readonly uplink?: { readonly codec: RemoteCodec };
  readonly cancellation?: { readonly parameter: 'signal' };
  readonly description?: string;
}
export interface RemoteInputOptions { readonly strict?: boolean; }
/** Named invocation borrows the mounted namespace and retains its original return value. */
export type RemoteMethod<Input extends object, Output, Return, Services extends object> =
  Omit<RemoteDescriptor, 'invocation' | 'result' | 'mode'> &
  (Return extends RemoteStreamHandle<unknown> ? { readonly mode: 'stream' } : { readonly mode?: never }) & {
    readonly invocation: { readonly kind: 'direct' };
    readonly result: RemoteCodec<Output>;
    input(options?: RemoteInputOptions): RemoteValue<Input>;
    invoke(remote: Services | RemoteCaller, input: Input, signal?: AbortSignal): Return;
  };
export interface RemoteInfo<Methods extends object = Readonly<Record<string, RemoteDescriptor>>> {
  readonly package: string;
  readonly descriptors: readonly RemoteDescriptor[];
  readonly methods: Methods;
}
/** Compiler facts consumed by the shared lazy metadata runtime. */
export interface RemoteCodecSpec {
  readonly typeSymbol: string;
  readonly description?: string;
  readonly create: () => RemoteSchema;
  readonly project: () => { readonly jsonSchema?: Readonly<Record<string, unknown>>; readonly jsonSchemaError?: string };
}
export interface RemoteMethodSpec {
  readonly id?: string;
  readonly service: string;
  readonly implementation?: string;
  readonly mode?: 'stream';
  readonly invocation: { readonly kind: 'direct' } | {
    readonly kind: 'context'; readonly context: string; readonly wire: string; readonly codec: RemoteCodecSpec;
  };
  readonly scope?: { readonly context: string; readonly wire: string };
  readonly parameters: readonly {
    readonly name: string; readonly wire?: string; readonly source: 'json' | 'lookup'; readonly lookup?: string;
    readonly acceptsUndefined?: true; readonly description?: string;
    readonly codec: RemoteCodecSpec; readonly strictCodec: RemoteCodecSpec;
  }[];
  readonly result: RemoteCodecSpec;
  readonly uplink?: { readonly codec: RemoteCodecSpec };
  readonly cancellation?: { readonly parameter: 'signal' };
  readonly description?: string;
}
export declare function createRemoteInfo(spec: {
  readonly package: string;
  readonly methods: Readonly<Record<string, RemoteMethodSpec>>;
}): RemoteInfo;
