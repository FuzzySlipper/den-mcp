export type JsonObject = Record<string, unknown>;

export type PiStdoutParseResult =
  | { kind: "json"; line: string; event: any }
  | { kind: "raw_stdout"; line: string };

export type SubagentOutputSnapshot = {
  finalOutput: string;
  model?: string;
  messageCount: number;
  assistantMessageCount: number;
  promptEchoDetected: boolean;
  childErrorMessage?: string;
};

export type SubagentOutputObserver = {
  appendEvent(event: JsonObject): Promise<void> | void;
};

export type SubagentOutputExtractor = {
  updateFromEvent(event: any): string | undefined;
  recordChildError(message: string): void;
  snapshot(): SubagentOutputSnapshot;
};

export type InfrastructureFailureLike = {
  aborted?: boolean;
  timeout_kind?: string;
  forced_kill?: boolean;
  signal?: string;
  child_error_message?: string;
};

export function parsePiStdoutLine(line: string): PiStdoutParseResult | undefined {
  if (!line.trim()) return undefined;
  try {
    return { kind: "json", line: line.trim(), event: JSON.parse(line) };
  } catch {
    return { kind: "raw_stdout", line };
  }
}

export function createSubagentOutputExtractor(
  prompt: string,
  observer?: SubagentOutputObserver,
): SubagentOutputExtractor {
  let finalOutput = "";
  let model: string | undefined;
  let childErrorMessage: string | undefined;
  let messageCount = 0;
  let assistantMessageCount = 0;
  let promptEchoDetected = false;

  return {
    updateFromEvent(event: any): string | undefined {
      const message = event.message;
      if (!message) return undefined;
      if (event.type !== "message_end" && event.type !== "tool_result_end") return undefined;
      messageCount++;
      const text = extractText(message);
      if (message.errorMessage && typeof message.errorMessage === "string") childErrorMessage = message.errorMessage;
      if (message.role !== "assistant") return undefined;

      assistantMessageCount++;
      if (message.model && typeof message.model === "string") model = message.model;
      if (!text) return undefined;

      if (isPromptEcho(text, prompt)) {
        promptEchoDetected = true;
        void observer?.appendEvent({
          type: "subagent.prompt_echo_detected",
          ts: Date.now(),
          chars: text.length,
          terminal: isTerminalAssistantMessage(message),
        });
        return undefined;
      }

      finalOutput = text;
      void observer?.appendEvent({
        type: "subagent.assistant_output",
        ts: Date.now(),
        chars: text.length,
        terminal: isTerminalAssistantMessage(message),
      });
      return text;
    },
    recordChildError(message: string) {
      childErrorMessage = message;
    },
    snapshot() {
      return {
        finalOutput,
        model,
        messageCount,
        assistantMessageCount,
        promptEchoDetected,
        childErrorMessage,
      };
    },
  };
}

export function isSubagentInfrastructureFailure(result: InfrastructureFailureLike): boolean {
  return Boolean(
    result.aborted
      || result.timeout_kind
      || result.forced_kill
      || result.signal
      || result.child_error_message,
  );
}

export function isTerminalAssistantMessage(message: any): boolean {
  if (!message || message.role !== "assistant") return false;
  if (!extractText(message)) return false;
  const stopReason = String(message.stopReason ?? message.stop_reason ?? "");
  if (stopReason && stopReason !== "stop" && stopReason !== "end_turn") return false;
  if (!Array.isArray(message.content)) return true;
  return !message.content.some((part: any) => part?.type === "toolCall" || part?.type === "tool_call");
}

export function extractText(message: any): string | undefined {
  if (!Array.isArray(message.content)) return undefined;
  for (let i = message.content.length - 1; i >= 0; i--) {
    const part = message.content[i];
    if (part?.type === "text" && typeof part.text === "string") return part.text;
  }
  return undefined;
}

export function isPromptEcho(text: string, prompt: string): boolean {
  const normalizedText = normalizeForEchoDetection(text);
  const normalizedPrompt = normalizeForEchoDetection(prompt);
  if (!normalizedText || !normalizedPrompt) return false;
  if (normalizedText === normalizedPrompt) return true;
  const prefix = normalizedPrompt.slice(0, Math.min(normalizedPrompt.length, 500));
  return prefix.length > 80 && normalizedText.includes(prefix);
}

function normalizeForEchoDetection(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}
