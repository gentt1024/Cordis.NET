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
