import type { BridgeEventFrame } from '../bridge/contract.ts';
import type { PlaceholderRuntimeEvent, SidecarBridgeClient } from './sidecarProtocol.ts';
import { createSidecarBridgeFacade, type SidecarHealthResponse, type SidecarCapabilitiesResponse } from './sidecarProtocol.ts';

export interface DenDesktopSidecarApi {
  getHealth(): Promise<SidecarHealthResponse>;
  getCapabilities(): Promise<SidecarCapabilitiesResponse>;
  onPlaceholderRuntimeEvent(listener: (event: PlaceholderRuntimeEvent) => void): () => void;
}

export interface PlaceholderEventSource {
  subscribe(listener: (frame: BridgeEventFrame) => void): () => void;
}

export function createDenDesktopSidecarApi(
  client: SidecarBridgeClient,
  placeholderEvents: PlaceholderEventSource,
): DenDesktopSidecarApi {
  const facade: ReturnType<typeof createSidecarBridgeFacade> = createSidecarBridgeFacade(client);
  return Object.freeze({
    getHealth: facade.getHealth,
    getCapabilities: facade.getCapabilities,
    onPlaceholderRuntimeEvent(listener: (event: PlaceholderRuntimeEvent) => void) {
      return placeholderEvents.subscribe((frame) => {
        facade.assertPlaceholderRuntimeEvent(frame);
        listener(frame.payload);
      });
    },
  });
}
