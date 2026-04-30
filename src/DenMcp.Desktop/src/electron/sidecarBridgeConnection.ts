/**
 * WebSocket-based bridge transport for the Den Desktop sidecar.
 *
 * This module provides the main-process transport that connects to the
 * loopback WebSocket server exposed by the .NET sidecar. It implements
 * the BridgeClientTransport contract from the bridge/contract module.
 *
 * Supports both DOM-style WebSocket (addEventListener) and the `ws` npm
 * package (EventEmitter .on/.off) for flexibility across environments.
 */

import type {
  BridgeCancelFrame,
  BridgeClientTransport,
  BridgeEventFrame,
  BridgeProgressFrame,
  BridgeRequestFrame,
  BridgeResponseFrame,
} from '../bridge/contract.ts';

export interface SidecarBridgeConnectionOptions {
  /** Sidecar HTTP/WS base URL, e.g. `http://127.0.0.1:54321`. */
  baseUrl: string;
  /** Sidecar endpoint path, e.g. `/bridge`. */
  endpointPath: string;
  /** Auth token required by the sidecar WebSocket server. */
  authToken: string;
  /** WebSocket constructor (Node `ws` package or DOM WebSocket). */
  WebSocketCtor: any;
  /**
   * Optional callback invoked when the transport receives an event frame
   * (frame_type === 'event') from the sidecar. Event frames are not
   * request/response pairs, so they bypass the pending-request map.
   */
  onEvent?: (frame: BridgeEventFrame) => void;
}

/**
 * Attaches an event listener to a WebSocket instance, supporting both
 * DOM-style `addEventListener` and Node EventEmitter `.on()` APIs.
 */
function onSocketEvent(ws: any, event: string, handler: (...args: any[]) => void): void {
  if (typeof ws.addEventListener === 'function') {
    ws.addEventListener(event, handler);
  } else if (typeof ws.on === 'function') {
    ws.on(event, handler);
  }
}

/**
 * Creates a BridgeClientTransport that communicates with the sidecar over
 * a persistent WebSocket connection. Each request sends a JSON frame and
 * waits for the corresponding response.
 */
export function createSidecarBridgeTransport(
  options: SidecarBridgeConnectionOptions,
): BridgeClientTransport & { close(): void } {
  const wsUrl = `${options.baseUrl.replace(/^http/, 'ws')}${options.endpointPath}`;
  const pending = new Map<string, {
    resolve: (frame: BridgeResponseFrame) => void;
    reject: (error: Error) => void;
    onProgress?: (frame: BridgeProgressFrame) => void;
  }>();

  let socket: any = null;
  let closed = false;
  let connectPromise: Promise<void> | null = null;
  let connectReject: ((error: Error) => void) | null = null;

  function handleMessage(data: unknown): void {
    const text = typeof data === 'string' ? data : String(data);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      return;
    }

    const frame = parsed as Record<string, unknown>;

    // Event frames carry event_id and frame_type='event', not request_id.
    // Route them through the onEvent callback so the event source can broadcast.
    if (frame.frame_type === 'event' && typeof frame.event_id === 'string') {
      options.onEvent?.(parsed as BridgeEventFrame);
      return;
    }

    if (typeof frame.request_id !== 'string') {
      return;
    }

    // Progress frames carry request_id and frame_type='progress'; route to
    // the per-request onProgress callback if one is registered.
    if (frame.frame_type === 'progress' && typeof frame.request_id === 'string') {
      const progressEntry = pending.get(frame.request_id);
      if (progressEntry?.onProgress) {
        progressEntry.onProgress(parsed as BridgeProgressFrame);
      }
      return;
    }

    const entry = pending.get(frame.request_id);
    if (entry) {
      pending.delete(frame.request_id);
      entry.resolve(parsed as BridgeResponseFrame);
    }
  }

  function ensureConnected(): Promise<void> {
    if (socket && (socket.readyState === 1 /* OPEN */)) {
      return Promise.resolve();
    }

    if (connectPromise) {
      return connectPromise;
    }

    connectPromise = new Promise<void>((resolve, reject) => {
      connectReject = reject;
      const ws = new options.WebSocketCtor(wsUrl, {
        headers: {
          Authorization: `Bearer ${options.authToken}`,
        },
      });

      onSocketEvent(ws, 'open', () => {
        socket = ws;
        connectPromise = null;
        connectReject = null;
        resolve();
      });

      onSocketEvent(ws, 'error', (event: any) => {
        connectPromise = null;
        connectReject = null;
        if (!socket) {
          reject(new Error(`WebSocket connection failed: ${event?.message ?? 'unknown error'}`));
        }
      });

      onSocketEvent(ws, 'message', (event: any) => {
        // DOM WebSocket: event.data; ws package: data is the message directly
        const data = event?.data !== undefined ? event.data : event;
        handleMessage(data);
      });

      onSocketEvent(ws, 'close', () => {
        socket = null;
        if (!closed) {
          for (const entry of pending.values()) {
            entry.reject(new Error('WebSocket closed unexpectedly.'));
          }
          pending.clear();
        }
      });
    });

    return connectPromise;
  }

  return {
    async send(frame: BridgeRequestFrame, onProgress?: (frame: BridgeProgressFrame) => void): Promise<BridgeResponseFrame> {
      await ensureConnected();
      return new Promise<BridgeResponseFrame>((resolve, reject) => {
        const timeout = setTimeout(() => {
          pending.delete(frame.request_id);
          reject(new Error(`Bridge request '${frame.command}' timed out after 30s.`));
        }, 30_000);

        pending.set(frame.request_id, {
          resolve: (response) => {
            clearTimeout(timeout);
            resolve(response);
          },
          reject: (error) => {
            clearTimeout(timeout);
            reject(error);
          },
          onProgress,
        });

        socket!.send(JSON.stringify(frame));
      });
    },

    async cancel(frame: BridgeCancelFrame): Promise<void> {
      await ensureConnected();
      socket!.send(JSON.stringify(frame));
    },

    close(): void {
      closed = true;
      if (connectReject) {
        connectReject(new Error('Bridge connection closed.'));
        connectReject = null;
      }
      connectPromise = null;
      for (const entry of pending.values()) {
        entry.reject(new Error('Bridge connection closed.'));
      }
      pending.clear();
      if (socket) {
        socket.close();
        socket = null;
      }
    },
  };
}
