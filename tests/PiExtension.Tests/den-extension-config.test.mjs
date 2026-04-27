import assert from 'node:assert/strict';
import test from 'node:test';
import { reasoningCaptureOptionsFromConfig } from '../../pi-dev/lib/den-extension-config.ts';
import { resolveReasoningCaptureOptions } from '../../pi-dev/lib/den-subagent-pipeline.ts';

function restoreEnv(name, value) {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

test('Den extension config maps reasoning capture knobs to normalizer options', () => {
  const previous = process.env.DEN_PI_SUBAGENT_RAW_REASONING;
  delete process.env.DEN_PI_SUBAGENT_RAW_REASONING;
  try {
    const options = reasoningCaptureOptionsFromConfig({
      version: 1,
      reasoning: {
        capture_provider_summaries: false,
        capture_raw_local_previews: true,
        preview_chars: 500,
      },
    });

    assert.deepEqual(options, {
      captureProviderSummaries: false,
      captureRawLocalPreviews: true,
      previewChars: 500,
    });
    assert.deepEqual(resolveReasoningCaptureOptions(options), {
      captureProviderSummaries: false,
      captureRawLocalPreviews: true,
      previewChars: 500,
      rawEnvOverride: false,
      rawEnvValue: undefined,
    });

    process.env.DEN_PI_SUBAGENT_RAW_REASONING = 'false';
    assert.equal(resolveReasoningCaptureOptions(options).captureRawLocalPreviews, false);
  } finally {
    restoreEnv('DEN_PI_SUBAGENT_RAW_REASONING', previous);
  }
});
