#!/usr/bin/env node

/**
 * Cross-platform launcher for Electron hot dev mode.
 *
 * Sets DEN_DESKTOP_ELECTRON_LOAD_MODE=hot in the Node process env before
 * spawning Electron, so the main process resolves the Vite dev server URL
 * instead of loading the built UI. Replaces the POSIX-only inline env syntax
 * (DEN_DESKTOP_ELECTRON_LOAD_MODE=hot npx electron …) that does not work
 * under Windows cmd.exe.
 *
 * @see rendererLoadMode.ts — reads DEN_DESKTOP_ELECTRON_LOAD_MODE from env
 */

import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Set the hot-load mode flag before spawning Electron.
// The Electron main process reads this from process.env.
process.env.DEN_DESKTOP_ELECTRON_LOAD_MODE = 'hot';

// Resolve the local Electron binary path through the npm electron package.
// On POSIX this resolves to node_modules/.bin/electron (a shell script);
// on Windows it resolves to node_modules/.bin/electron.cmd or similar.
const require = createRequire(import.meta.url);
const electronPath = require('electron');

const mainScript = resolve(__dirname, 'electron-dist/main.mjs');

const child = spawn(electronPath, [mainScript], {
  stdio: 'inherit',
  cwd: __dirname,
  env: process.env,
});

child.on('exit', (code, signal) => {
  process.exit(code ?? (signal ? 1 : 0));
});

child.on('error', (err) => {
  console.error('[electron-launch-hot] Failed to spawn Electron:', err.message);
  process.exit(1);
});
