import assert from 'node:assert/strict';
import test from 'node:test';
import {
  classifySubagentInfrastructureFailure,
  classifySubagentStderrIssue,
  createSubagentOutputExtractor,
  isSubagentInfrastructureFailure,
  isTerminalAssistantMessage,
  parsePiStdoutLine,
} from '../../pi-dev/lib/den-subagent-pipeline.ts';

function assistantMessage(text, extra = {}) {
  return {
    role: 'assistant',
    model: 'gpt-test',
    stopReason: 'stop',
    content: [{ type: 'text', text }],
    ...extra,
  };
}

test('parsePiStdoutLine preserves json and raw stdout separately', () => {
  const json = parsePiStdoutLine('{"type":"message_end","message":{"role":"assistant"}}');
  assert.equal(json?.kind, 'json');
  assert.equal(json?.line, '{"type":"message_end","message":{"role":"assistant"}}');
  assert.equal(json?.event.type, 'message_end');

  const raw = parsePiStdoutLine('not json');
  assert.deepEqual(raw, { kind: 'raw_stdout', line: 'not json' });

  assert.equal(parsePiStdoutLine('   '), undefined);
});

test('output extractor accepts assistant final output and records model', () => {
  const events = [];
  const extractor = createSubagentOutputExtractor('Say hi', {
    appendEvent(event) {
      events.push(event);
    },
  });

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: assistantMessage('hello'),
  });

  assert.equal(output, 'hello');
  assert.deepEqual(extractor.snapshot(), {
    finalOutput: 'hello',
    model: 'gpt-test',
    messageCount: 1,
    assistantMessageCount: 1,
    promptEchoDetected: false,
    childErrorMessage: undefined,
  });
  assert.equal(events[0].type, 'subagent.assistant_output');
});

test('output extractor ignores user prompt echoes', () => {
  const prompt = 'Reply with exactly: SUBAGENT_SMOKE_OK';
  const extractor = createSubagentOutputExtractor(prompt);

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: {
      role: 'user',
      content: [{ type: 'text', text: prompt }],
    },
  });

  assert.equal(output, undefined);
  assert.equal(extractor.snapshot().finalOutput, '');
  assert.equal(extractor.snapshot().promptEchoDetected, false);
  assert.equal(extractor.snapshot().messageCount, 1);
  assert.equal(extractor.snapshot().assistantMessageCount, 0);
});

test('output extractor classifies assistant prompt echoes as unusable', () => {
  const prompt = 'This is a deliberately long prompt that should be detected if an assistant echoes the beginning of it back instead of producing an actual answer.';
  const events = [];
  const extractor = createSubagentOutputExtractor(prompt, {
    appendEvent(event) {
      events.push(event);
    },
  });

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: assistantMessage(`${prompt}\n\nMore prompt material.`),
  });

  assert.equal(output, undefined);
  assert.equal(extractor.snapshot().finalOutput, '');
  assert.equal(extractor.snapshot().promptEchoDetected, true);
  assert.equal(extractor.snapshot().assistantMessageCount, 1);
  assert.equal(events[0].type, 'subagent.prompt_echo_detected');
});

test('terminal assistant detection excludes tool-call messages', () => {
  assert.equal(isTerminalAssistantMessage(assistantMessage('done')), true);
  assert.equal(isTerminalAssistantMessage(assistantMessage('needs tool', {
    content: [{ type: 'text', text: 'needs tool' }, { type: 'toolCall', name: 'search' }],
  })), false);
  assert.equal(isTerminalAssistantMessage({ role: 'user', content: [{ type: 'text', text: 'hello' }] }), false);
});

test('infrastructure failures are classified before fallback retry', () => {
  assert.equal(isSubagentInfrastructureFailure({ timeout_kind: 'startup' }), true);
  assert.equal(isSubagentInfrastructureFailure({ forced_kill: true }), true);
  assert.equal(isSubagentInfrastructureFailure({ signal: 'SIGTERM' }), true);
  assert.equal(isSubagentInfrastructureFailure({ child_error_message: 'spawn ENOENT' }), true);
  assert.equal(classifySubagentInfrastructureFailure({
    stderr_tail: 'Error: Failed to load extension "/tmp/bad.ts": Extension does not export a valid factory function',
  }), 'extension_load');
  assert.equal(classifySubagentInfrastructureFailure({
    stderr: 'Extension error (/tmp/footer.ts): This extension ctx is stale after session replacement or reload.',
  }), 'extension_runtime');
  assert.equal(classifySubagentStderrIssue(
    'Extension error (/tmp/footer.ts): This extension ctx is stale after session replacement or reload.',
  ), 'extension_runtime');
  assert.equal(isSubagentInfrastructureFailure({}), false);
});
