import { mkdir, readFile, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import type { ReasoningCaptureOptions } from "./den-subagent-pipeline.ts";

export type DenReasoningCaptureConfig = {
  capture_provider_summaries?: boolean;
  capture_raw_local_previews?: boolean;
  preview_chars?: number;
};

export type DenExtensionConfig = {
  version?: number;
  fallback_model?: string;
  reasoning?: DenReasoningCaptureConfig;
  subagents?: Record<string, {
    model?: string;
    tools?: string;
  }>;
};

export type ConfigScope = "project" | "global";

export const DEN_CONFIG_FILENAME = "den-config.json";

export async function loadMergedDenExtensionConfig(cwd: string): Promise<DenExtensionConfig> {
  const globalConfig = await loadDenExtensionConfig("global", cwd);
  const projectConfig = await loadDenExtensionConfig("project", cwd);
  const roles = new Set([
    ...Object.keys(globalConfig.subagents ?? {}),
    ...Object.keys(projectConfig.subagents ?? {}),
  ]);
  const subagents: NonNullable<DenExtensionConfig["subagents"]> = {};
  for (const role of roles) {
    subagents[role] = {
      ...(globalConfig.subagents?.[role] ?? {}),
      ...(projectConfig.subagents?.[role] ?? {}),
    };
  }

  const reasoning = mergeReasoningConfig(globalConfig.reasoning, projectConfig.reasoning);
  return {
    version: 1,
    ...globalConfig,
    ...projectConfig,
    reasoning,
    subagents,
  };
}

export async function loadDenExtensionConfig(scope: ConfigScope, cwd: string): Promise<DenExtensionConfig> {
  try {
    const text = await readFile(denConfigPath(scope, cwd), "utf8");
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object") return parsed;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw error;
  }
  return { version: 1, subagents: {} };
}

export async function saveDenExtensionConfig(scope: ConfigScope, cwd: string, config: DenExtensionConfig) {
  const file = denConfigPath(scope, cwd);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(cleanDenExtensionConfig(config), null, 2)}\n`, "utf8");
}

export function denConfigPath(scope: ConfigScope, cwd: string): string {
  if (scope === "global") return path.join(os.homedir(), ".pi", "agent", DEN_CONFIG_FILENAME);
  return path.join(cwd, ".pi", DEN_CONFIG_FILENAME);
}

export function reasoningCaptureOptionsFromConfig(config: DenExtensionConfig): ReasoningCaptureOptions {
  const reasoning = config.reasoning ?? {};
  return {
    captureProviderSummaries: optionalBoolean(reasoning.capture_provider_summaries),
    captureRawLocalPreviews: optionalBoolean(reasoning.capture_raw_local_previews),
    previewChars: optionalFiniteNumber(reasoning.preview_chars),
  };
}

function mergeReasoningConfig(
  globalReasoning?: DenReasoningCaptureConfig,
  projectReasoning?: DenReasoningCaptureConfig,
): DenReasoningCaptureConfig | undefined {
  const merged = {
    ...(globalReasoning ?? {}),
    ...(projectReasoning ?? {}),
  };
  return Object.keys(merged).length > 0 ? merged : undefined;
}

function cleanDenExtensionConfig(config: DenExtensionConfig): DenExtensionConfig {
  const cleaned: DenExtensionConfig = { ...config, version: 1 };
  if (cleaned.reasoning && Object.keys(cleaned.reasoning).length === 0) delete cleaned.reasoning;
  if (cleaned.subagents && Object.keys(cleaned.subagents).length === 0) delete cleaned.subagents;
  return cleaned;
}

function optionalBoolean(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function optionalFiniteNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
