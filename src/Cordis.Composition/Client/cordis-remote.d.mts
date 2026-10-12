/// <reference lib="esnext.disposable" />
export declare class RemoteError extends Error {
  readonly isDSHRemoteError: true;
  readonly code: string;
  readonly details: unknown;
  constructor(code: string, message: string, details?: unknown);
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
