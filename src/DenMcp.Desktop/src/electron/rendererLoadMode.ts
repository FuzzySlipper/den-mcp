import * as path from 'node:path';

export type ElectronRendererLoadMode = 'build' | 'hot';

export type ElectronRendererLoadTarget =
  | { kind: 'file'; path: string; mode: 'build' }
  | { kind: 'url'; url: string; mode: 'hot' };

export type ElectronRendererLoadEnv = Partial<Record<'DEN_DESKTOP_ELECTRON_LOAD_MODE' | 'VITE_DEV_SERVER_URL', string>>;

export interface ResolveRendererLoadTargetOptions {
  isPackaged: boolean;
  electronDistDir: string;
  env?: ElectronRendererLoadEnv;
}

export const defaultViteDevServerUrl = 'http://127.0.0.1:1421';

export function resolveRendererLoadTarget({
  isPackaged,
  electronDistDir,
  env = process.env,
}: ResolveRendererLoadTargetOptions): ElectronRendererLoadTarget {
  if (!isPackaged && env.DEN_DESKTOP_ELECTRON_LOAD_MODE === 'hot') {
    return {
      kind: 'url',
      mode: 'hot',
      url: env.VITE_DEV_SERVER_URL || defaultViteDevServerUrl,
    };
  }

  return {
    kind: 'file',
    mode: 'build',
    path: path.resolve(electronDistDir, '../dist/index.html'),
  };
}
