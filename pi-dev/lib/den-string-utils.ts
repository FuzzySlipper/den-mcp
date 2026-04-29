export function normalizeString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

export function oneLine(value: string, maxChars = 220): string {
  return value.replace(/\s+/g, " ").trim().slice(0, maxChars);
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
