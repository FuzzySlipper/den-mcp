import assert from 'node:assert/strict';
import test from 'node:test';
import {
  formatCoderContextPacket,
  buildCoderContextPacketMeta,
  summarizeDependencies,
  summarizeRecentPackets,
  resolveEffectiveCoderConfig,
} from '../../pi-dev/lib/den-coder-context-packet.ts';

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const FULL_INPUT = {
  task_id: 934,
  parent_task_id: 932,
  task_title: 'Implement coder context packet preparation helper',
  task_description: 'Add tooling to prepare a curated context packet for coder sub-agents.',
  task_status: 'in_progress',
  task_tags: ['den', 'pi', 'context', 'coder', 'tooling'],
  branch: 'task/934-coder-context-packet',
  worktree_path: '/home/patch/dev/den-mcp-task-934',
  base_commit: 'd4e9023f7584934fe7a1db601d4a601f530dde95',
  effective_coder_model: 'zai/glm-5.1',
  config_source: 'inherited:/home/patch/dev/den-mcp/.pi/den-config.json',
  user_intent: 'Build the next workflow-foundation piece that lets a conductor prepare a bounded, durable coder_context_packet.',
  acceptance_criteria: '- Given a task, produce a coder context packet containing task description, acceptance criteria, dependency summaries, etc.',
  dependency_summaries: [
    { task_id: 933, title: 'Define delegated coder workflow guidance', status: 'done', summary: 'Added project guidance defining packet metadata conventions.' },
    { task_id: 942, title: 'Ensure delegated coder worktrees honor project Den sub-agent config', status: 'done', summary: 'Linked worktrees inherit .pi/den-config.json.' },
  ],
  relevant_docs: [
    { ref: 'den-mcp/delegated-coder-workflow-policy', description: 'Defines packet metadata contract and conductor-vs-coder roles.' },
    { ref: 'den-mcp/delegated-coder-workflow-foundation-plan', description: 'Identifies the need for a reliable prepare coder context packet tool.' },
  ],
  recent_packets: [
    { type: 'implementation_packet', message_id: 1801, summary: 'Added den-implementation-packet.ts and auto-posting for coder runs.' },
  ],
  constraints: 'Do not edit generated AGENTS.md. Do not change global prompt defaults.',
  file_pointers: [
    { path: 'pi-dev/lib/den-prompt-templates.ts', description: 'STRUCTURED_PACKET_TYPES and summarizeTaskContext.' },
    { path: 'pi-dev/extensions/den-subagent.ts', description: 'Registered tools and commands.' },
  ],
  validation_commands: [
    'node --test tests/PiExtension.Tests/den-coder-context-packet.test.mjs',
    'git diff --check main...HEAD',
  ],
  extra_notes: 'Commit on the task branch and wait for review.',
};

const MINIMAL_INPUT = {
  task_id: 100,
};

// ---------------------------------------------------------------------------
// formatCoderContextPacket
// ---------------------------------------------------------------------------

test('formatCoderContextPacket produces markdown with all sections from full input', () => {
  const md = formatCoderContextPacket(FULL_INPUT);

  // Header and task identity
  assert.ok(md.includes('# Coder Context Packet — task 934'));
  assert.ok(md.includes('## Task'));
  assert.ok(md.includes('`#934 Implement coder context packet preparation helper`'));
  assert.ok(md.includes('`#932`'));
  assert.ok(md.includes('`task/934-coder-context-packet`'));
  assert.ok(md.includes('/home/patch/dev/den-mcp-task-934'));
  assert.ok(md.includes('d4e9023f7584934fe7a1db601d4a601f530dde95'));
  assert.ok(md.includes('zai/glm-5.1'));
  assert.ok(md.includes('inherited:/home/patch/dev/den-mcp/.pi/den-config.json'));

  // Sections
  assert.ok(md.includes('## User intent'));
  assert.ok(md.includes('## Acceptance criteria'));
  assert.ok(md.includes('## Dependency summaries'));
  assert.ok(md.includes('## Relevant docs'));
  assert.ok(md.includes('## Recent implementation packets'));
  assert.ok(md.includes('## Constraints / scope boundaries'));
  assert.ok(md.includes('## Suggested file pointers'));
  assert.ok(md.includes('## Validation commands'));
  assert.ok(md.includes('## Extra conductor notes'));

  // Dependency content
  assert.ok(md.includes('`#933`'));
  assert.ok(md.includes('[done]'));
  assert.ok(md.includes('packet metadata conventions'));

  // Doc content
  assert.ok(md.includes('delegated-coder-workflow-policy'));

  // Packet content
  assert.ok(md.includes('implementation_packet'));

  // File pointers
  assert.ok(md.includes('den-prompt-templates.ts'));

  // Validation commands
  assert.ok(md.includes('node --test tests/PiExtension.Tests/den-coder-context-packet.test.mjs'));
});

test('formatCoderContextPacket handles minimal input with just task_id', () => {
  const md = formatCoderContextPacket(MINIMAL_INPUT);

  assert.ok(md.includes('# Coder Context Packet — task 100'));
  assert.ok(md.includes('## Task'));
  assert.ok(md.includes('`#100`'));
  // No optional sections should appear
  assert.ok(!md.includes('## User intent'));
  assert.ok(!md.includes('## Acceptance criteria'));
  assert.ok(!md.includes('## Dependency summaries'));
  assert.ok(!md.includes('## Relevant docs'));
  assert.ok(!md.includes('## Recent implementation packets'));
  assert.ok(!md.includes('## Constraints'));
  assert.ok(!md.includes('## Suggested file pointers'));
  assert.ok(!md.includes('## Validation commands'));
  assert.ok(!md.includes('## Extra conductor notes'));
});

test('formatCoderContextPacket truncates long free-text fields', () => {
  const longText = 'x'.repeat(10000);
  const md = formatCoderContextPacket({
    task_id: 1,
    user_intent: longText,
  });
  assert.ok(md.includes('truncated at 8000 chars'));
  assert.ok(md.length < 10000);
});

test('formatCoderContextPacket limits dependency count and shows overflow', () => {
  const deps = Array.from({ length: 15 }, (_, i) => ({
    task_id: 100 + i,
    title: `Dep ${i}`,
  }));
  const md = formatCoderContextPacket({ task_id: 1, dependency_summaries: deps });
  assert.ok(md.includes('`#109`')); // 10th dep (0-indexed: 9 => task 109)
  assert.ok(md.includes('and 5 more'));
  assert.ok(!md.includes('`#114`')); // 15th dep should not appear
});

test('formatCoderContextPacket limits recent packet count', () => {
  const packets = Array.from({ length: 8 }, (_, i) => ({
    type: 'implementation_packet',
    message_id: 100 + i,
    summary: `Packet ${i}`,
  }));
  const md = formatCoderContextPacket({ task_id: 1, recent_packets: packets });
  assert.ok(md.includes('and 3 more'));
});

test('formatCoderContextPacket limits file pointers', () => {
  const fps = Array.from({ length: 20 }, (_, i) => ({
    path: `file-${i}.ts`,
  }));
  const md = formatCoderContextPacket({ task_id: 1, file_pointers: fps });
  assert.ok(md.includes('and 5 more'));
});

// ---------------------------------------------------------------------------
// buildCoderContextPacketMeta
// ---------------------------------------------------------------------------

test('buildCoderContextPacketMeta produces correct metadata from full input', () => {
  const meta = buildCoderContextPacketMeta(FULL_INPUT);

  assert.equal(meta.type, 'coder_context_packet');
  assert.equal(meta.prepared_by, 'conductor');
  assert.equal(meta.workflow, 'expanded_isolation_with_context');
  assert.equal(meta.version, 1);
  assert.equal(meta.parent_task_id, 932);
  assert.equal(meta.branch, 'task/934-coder-context-packet');
  assert.equal(meta.worktree_path, '/home/patch/dev/den-mcp-task-934');
  assert.equal(meta.base_commit, 'd4e9023f7584934fe7a1db601d4a601f530dde95');
  assert.equal(meta.effective_coder_model, 'zai/glm-5.1');
  assert.equal(meta.config_source, 'inherited:/home/patch/dev/den-mcp/.pi/den-config.json');
});

test('buildCoderContextPacketMeta uses nulls for missing optional fields', () => {
  const meta = buildCoderContextPacketMeta(MINIMAL_INPUT);

  assert.equal(meta.type, 'coder_context_packet');
  assert.equal(meta.parent_task_id, null);
  assert.equal(meta.branch, null);
  assert.equal(meta.worktree_path, null);
  assert.equal(meta.base_commit, null);
  assert.equal(meta.effective_coder_model, null);
  assert.equal(meta.config_source, null);
});

test('buildCoderContextPacketMeta serializes cleanly to JSON', () => {
  const meta = buildCoderContextPacketMeta(FULL_INPUT);
  const json = JSON.stringify(meta);
  const parsed = JSON.parse(json);
  assert.equal(parsed.type, 'coder_context_packet');
  assert.equal(parsed.version, 1);
});

// ---------------------------------------------------------------------------
// summarizeDependencies
// ---------------------------------------------------------------------------

test('summarizeDependencies extracts structured deps from task detail', () => {
  const detail = {
    dependencies: [
      { task_id: 933, title: 'Define workflow guidance', status: 'done', summary: 'Added conventions.' },
      { task_id: 942, title: 'Worktree config', status: 'done' },
    ],
  };
  const result = summarizeDependencies(detail);
  assert.equal(result.length, 2);
  assert.equal(result[0].task_id, 933);
  assert.equal(result[0].title, 'Define workflow guidance');
  assert.equal(result[0].status, 'done');
  assert.equal(result[1].task_id, 942);
});

test('summarizeDependencies respects max limit', () => {
  const detail = {
    dependencies: Array.from({ length: 20 }, (_, i) => ({ task_id: i, title: `Task ${i}` })),
  };
  const result = summarizeDependencies(detail, 5);
  assert.equal(result.length, 5);
});

test('summarizeDependencies handles missing dependencies gracefully', () => {
  assert.deepEqual(summarizeDependencies({}), []);
  assert.deepEqual(summarizeDependencies({ dependencies: null }), []);
  assert.deepEqual(summarizeDependencies({ dependencies: [] }), []);
});

// ---------------------------------------------------------------------------
// summarizeRecentPackets
// ---------------------------------------------------------------------------

test('summarizeRecentPackets filters to structured packet types', () => {
  const messages = [
    { id: 10, metadata: { type: 'coder_context_packet' }, content: 'Context for task 934.' },
    { id: 11, metadata: { type: 'implementation_packet' }, content: 'Implemented the thing.' },
    { id: 12, metadata: null, content: 'Plain message.' },
    { id: 13, metadata: { type: 'random_type' }, content: 'Not a packet.' },
    { id: 14, metadata: { type: 'review_feedback' }, content: 'Looks good.' },
  ];
  const result = summarizeRecentPackets(messages);
  assert.equal(result.length, 3); // context, implementation, review_feedback
  assert.equal(result[0].type, 'coder_context_packet');
  assert.equal(result[0].message_id, 10);
  assert.equal(result[1].type, 'implementation_packet');
  assert.equal(result[2].type, 'review_feedback');
});

test('summarizeRecentPackets parses string metadata', () => {
  const messages = [
    { id: 20, metadata: JSON.stringify({ type: 'implementation_packet' }), content: 'Done.' },
  ];
  const result = summarizeRecentPackets(messages);
  assert.equal(result.length, 1);
  assert.equal(result[0].type, 'implementation_packet');
  assert.equal(result[0].message_id, 20);
});

test('summarizeRecentPackets respects max limit', () => {
  const messages = Array.from({ length: 10 }, (_, i) => ({
    id: 100 + i,
    metadata: { type: 'implementation_packet' },
    content: `Packet ${i}.`,
  }));
  const result = summarizeRecentPackets(messages, 3);
  assert.equal(result.length, 3);
});

test('summarizeRecentPackets handles empty or invalid inputs', () => {
  assert.deepEqual(summarizeRecentPackets([]), []);
  assert.deepEqual(summarizeRecentPackets(null), []);
  assert.deepEqual(summarizeRecentPackets(undefined), []);
});

test('summarizeRecentPackets truncates long content to one-line summary', () => {
  const longContent = 'A '.repeat(500);
  const messages = [
    { id: 30, metadata: { type: 'implementation_packet' }, content: longContent },
  ];
  const result = summarizeRecentPackets(messages);
  assert.equal(result.length, 1);
  assert.ok(result[0].summary.length <= 200, `Summary too long: ${result[0].summary.length}`);
});

// ---------------------------------------------------------------------------
// resolveEffectiveCoderConfig
// ---------------------------------------------------------------------------

test('resolveEffectiveCoderConfig returns model and source from config module', async () => {
  const mockConfig = {
    loadMergedDenExtensionConfig: async (_cwd) => ({
      subagents: { coder: { model: 'test/model-v1' } },
      fallback_model: 'fallback/model',
    }),
    denConfigPaths: async (_cwd) => [
      '/home/user/project/.pi/den-config.json',
      '/home/user/main-project/.pi/den-config.json',
    ],
  };

  const result = await resolveEffectiveCoderConfig('/home/user/project', mockConfig);
  assert.equal(result.effective_coder_model, 'test/model-v1');
  assert.equal(result.config_source, 'inherited:/home/user/main-project/.pi/den-config.json');
});

test('resolveEffectiveCoderConfig falls back to fallback_model when no coder model', async () => {
  const mockConfig = {
    loadMergedDenExtensionConfig: async (_cwd) => ({
      subagents: {},
      fallback_model: 'fallback/model',
    }),
    denConfigPaths: async (_cwd) => ['/home/user/project/.pi/den-config.json'],
  };

  const result = await resolveEffectiveCoderConfig('/home/user/project', mockConfig);
  assert.equal(result.effective_coder_model, 'fallback/model');
  assert.equal(result.config_source, '/home/user/project/.pi/den-config.json');
});

test('resolveEffectiveCoderConfig returns empty when config fails to load', async () => {
  const mockConfig = {
    loadMergedDenExtensionConfig: async (_cwd) => { throw new Error('ENOENT'); },
    denConfigPaths: async (_cwd) => { throw new Error('ENOENT'); },
  };

  const result = await resolveEffectiveCoderConfig('/tmp/nonexistent', mockConfig);
  assert.equal(result.effective_coder_model, undefined);
  assert.equal(result.config_source, undefined);
});

// ---------------------------------------------------------------------------
// Round-trip: format + build metadata
// ---------------------------------------------------------------------------

test('formatCoderContextPacket and buildCoderContextPacketMeta are consistent', () => {
  const md = formatCoderContextPacket(FULL_INPUT);
  const meta = buildCoderContextPacketMeta(FULL_INPUT);

  // Metadata fields should appear in the markdown body
  assert.ok(md.includes(meta.branch));
  assert.ok(md.includes(meta.worktree_path));
  assert.ok(md.includes(meta.base_commit));
  assert.ok(md.includes(meta.effective_coder_model));
});
