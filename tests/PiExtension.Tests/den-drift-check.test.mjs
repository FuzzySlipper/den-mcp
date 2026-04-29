import assert from 'node:assert/strict';
import test from 'node:test';
import {
  analyzeDriftCheck,
  categorizeChangedPaths,
  compareExpectedScope,
  extractDeclaredTestsFromImplementationPacket,
  extractExpectedScopeFromContextPacket,
  extractTaskIntentFromContextPacket,
  formatDriftCheckPacketMessage,
  buildDriftCheckPacketMeta,
} from '../../pi-dev/lib/den-drift-check.ts';

test('analyzeDriftCheck keeps scoped source/test changes at medium risk with declared tests', () => {
  const result = analyzeDriftCheck({
    task_id: 937,
    branch: 'task/937-drift-check',
    head_commit: 'abc1234',
    base_ref: 'main',
    changed_paths: [
      { status: 'A', path: 'pi-dev/lib/den-drift-check.ts', additions: 200, deletions: 0 },
      { status: 'A', path: 'tests/PiExtension.Tests/den-drift-check.test.mjs', additions: 80, deletions: 0 },
    ],
    expected_scope: {
      paths: ['pi-dev/lib/den-drift-check.ts', 'tests/PiExtension.Tests/den-drift-check.test.mjs'],
    },
    declared_tests: ['node --test tests/PiExtension.Tests/den-drift-check.test.mjs — pass'],
  });

  // Test file changes are intentionally surfaced, but they should not become
  // high-risk when the harness itself was not changed.
  assert.equal(result.risk, 'medium');
  assert.equal(result.recommendation, 'flag-for-review');
  assert.deepEqual(result.scope.out_of_scope_paths, []);
  assert.ok(result.signals.some((s) => s.code === 'test_or_scoring_harness_changes'));
  assert.ok(!result.signals.some((s) => s.code === 'missing_declared_tests'));
});

test('analyzeDriftCheck flags paths outside expected context scope', () => {
  const result = analyzeDriftCheck({
    head_commit: 'abc1234',
    changed_paths: [
      { status: 'M', path: 'pi-dev/lib/den-drift-check.ts', additions: 10, deletions: 2 },
      { status: 'M', path: 'src/DenMcp.Server/Program.cs', additions: 20, deletions: 1 },
    ],
    expected_scope: { paths: ['pi-dev/lib/den-drift-check.ts'] },
    declared_tests: ['unit tests pass'],
  });

  assert.equal(result.risk, 'medium');
  assert.deepEqual(result.scope.out_of_scope_paths, ['src/DenMcp.Server/Program.cs']);
  assert.ok(result.signals.some((s) => s.code === 'outside_expected_scope'));
});

test('analyzeDriftCheck raises high risk for package/project/dependency and CI harness changes', () => {
  const result = analyzeDriftCheck({
    head_commit: 'abc1234',
    changed_paths: [
      { status: 'M', path: 'package.json', additions: 1, deletions: 1 },
      { status: 'M', path: '.github/workflows/ci.yml', additions: 5, deletions: 0 },
      { status: 'M', path: 'tests/scoring/harness.ts', additions: 5, deletions: 5 },
    ],
    declared_tests: ['node --test tests/PiExtension.Tests/den-drift-check.test.mjs — pass'],
  });

  assert.equal(result.risk, 'high');
  assert.ok(result.signals.some((s) => s.code === 'package_project_dependency_changes' && s.severity === 'high'));
  assert.ok(result.signals.some((s) => s.code === 'test_or_scoring_harness_changes' && s.severity === 'high'));
  assert.ok(result.categories.package_project_dependency_changes.includes('package.json'));
});

test('analyzeDriftCheck raises high risk for dirty worktree status but does not block', () => {
  const result = analyzeDriftCheck({
    head_commit: 'abc1234',
    git_status_short: [' M pi-dev/extensions/den-subagent.ts', '?? scratch.txt'],
    changed_paths: [{ status: 'M', path: 'pi-dev/extensions/den-subagent.ts', additions: 8, deletions: 1 }],
    declared_tests: ['node --test tests/PiExtension.Tests/den-drift-check.test.mjs — pass'],
  });

  assert.equal(result.risk, 'high');
  assert.equal(result.recommendation, 'flag-for-review');
  assert.ok(result.signals.some((s) => s.code === 'dirty_worktree'));
});

test('analyzeDriftCheck treats zero fail/skip declared tests as passing', () => {
  const result = analyzeDriftCheck({
    head_commit: 'abc1234',
    changed_paths: [{ status: 'M', path: 'pi-dev/lib/den-drift-check.ts', additions: 10, deletions: 1 }],
    declared_tests: ['node --test tests/PiExtension.Tests/den-drift-check.test.mjs — 11 pass, 0 fail, 0 skip'],
  });

  assert.equal(result.risk, 'low');
  assert.ok(!result.signals.some((s) => s.code === 'tests_skipped_or_failed'));
});

test('analyzeDriftCheck flags generated files and skipped or failed declared tests', () => {
  const result = analyzeDriftCheck({
    head_commit: 'abc1234',
    changed_paths: [{ status: 'A', path: 'dist/generated/client.min.js', additions: 1000, deletions: 0 }],
    declared_tests: ['Tests not run because build is blocked'],
  });

  assert.equal(result.risk, 'high');
  assert.ok(result.signals.some((s) => s.code === 'generated_files'));
  assert.ok(result.signals.some((s) => s.code === 'tests_skipped_or_failed'));
});

test('categorizeChangedPaths identifies representative suspicious path cases', () => {
  const categories = categorizeChangedPaths([
    { path: 'AGENTS.md' },
    { path: 'deploy-cli.sh' },
    { path: 'src/DenMcp.Core/DenMcp.Core.csproj' },
    { path: 'tests/PiExtension.Tests/example.test.mjs' },
    { path: 'src/generated/model.g.cs' },
  ]);

  assert.deepEqual(categories.suspicious_files, ['AGENTS.md', 'deploy-cli.sh']);
  assert.deepEqual(categories.package_project_dependency_changes, ['src/DenMcp.Core/DenMcp.Core.csproj']);
  assert.deepEqual(categories.test_or_scoring_harness_changes, ['tests/PiExtension.Tests/example.test.mjs']);
  assert.deepEqual(categories.generated_files, ['src/generated/model.g.cs']);
});

test('compareExpectedScope supports exact paths, directory prefixes, and globs', () => {
  const comparison = compareExpectedScope([
    { path: 'pi-dev/lib/den-drift-check.ts' },
    { path: 'tests/PiExtension.Tests/den-drift-check.test.mjs' },
    { path: 'docs/out-of-scope.md' },
  ], {
    paths: ['pi-dev/lib/den-drift-check.ts', 'tests/PiExtension.Tests/'],
    globs: ['pi-dev/extensions/*.ts'],
  });

  assert.equal(comparison.has_expected_scope, true);
  assert.deepEqual(comparison.in_scope_paths, [
    'pi-dev/lib/den-drift-check.ts',
    'tests/PiExtension.Tests/den-drift-check.test.mjs',
  ]);
  assert.deepEqual(comparison.out_of_scope_paths, ['docs/out-of-scope.md']);
});

test('extractExpectedScopeFromContextPacket reads likely file path hints', () => {
  const packet = `
# Coder Context Packet — task 937

## Scope guidance

Likely files:
- \`pi-dev/lib/den-drift-check.ts\` or similarly named new library.
- \`pi-dev/extensions/den-subagent.ts\` for command/tool wiring.
- \`tests/PiExtension.Tests/den-drift-check.test.mjs\` for representative cases.
- Existing patterns: \`pi-dev/lib/den-implementation-packet.ts\`.
`;

  const scope = extractExpectedScopeFromContextPacket(packet);
  assert.ok(scope.paths.includes('pi-dev/lib/den-drift-check.ts'));
  assert.ok(scope.paths.includes('pi-dev/extensions/den-subagent.ts'));
  assert.ok(scope.paths.includes('tests/PiExtension.Tests/den-drift-check.test.mjs'));
  assert.ok(!scope.paths.includes('937'));
});

test('extractExpectedScopeFromContextPacket ignores constraint-only forbidden paths when scope section exists', () => {
  const packet = `
# Coder Context Packet — task 937

## Scope guidance

Likely files:
- \`pi-dev/lib/den-drift-check.ts\`
- \`tests/PiExtension.Tests/den-drift-check.test.mjs\`

## Important constraints

- Preserve unrelated main-worktree deletion \`deploy-cli.sh\`.
- Do not edit generated \`AGENTS.md\` snapshots.
`;

  const scope = extractExpectedScopeFromContextPacket(packet);
  assert.ok(scope.paths.includes('pi-dev/lib/den-drift-check.ts'));
  assert.ok(scope.paths.includes('tests/PiExtension.Tests/den-drift-check.test.mjs'));
  assert.ok(!scope.paths.includes('deploy-cli.sh'));
  assert.ok(!scope.paths.includes('AGENTS.md'));
});

test('extractDeclaredTestsFromImplementationPacket reads tests run section', () => {
  const packet = `
# Implementation Packet

## Tests Run

- \`node --test tests/PiExtension.Tests/den-drift-check.test.mjs\` — pass
- \`git diff --check main...HEAD\` — pass

## Risk Notes

Low.
`;

  assert.deepEqual(extractDeclaredTestsFromImplementationPacket(packet), [
    '`node --test tests/PiExtension.Tests/den-drift-check.test.mjs` — pass',
    '`git diff --check main...HEAD` — pass',
  ]);
});

test('formatDriftCheckPacketMessage and metadata include risk, paths, and Den packet type', () => {
  const result = analyzeDriftCheck({
    task_id: 937,
    task_intent: 'Add deterministic drift check tooling.',
    implementation_summary: 'Added pure analysis and Pi wiring.',
    branch: 'task/937-drift-check',
    base_ref: 'main',
    head_commit: 'abc1234',
    changed_paths: [{ status: 'M', path: 'pi-dev/lib/den-drift-check.ts', additions: 5, deletions: 1 }],
    declared_tests: ['node --test tests/PiExtension.Tests/den-drift-check.test.mjs — pass'],
  });

  const message = formatDriftCheckPacketMessage(result);
  const meta = buildDriftCheckPacketMeta(result);

  assert.ok(message.includes('# Drift Check Packet'));
  assert.ok(message.includes('**Risk:** low'));
  assert.ok(message.includes('## Task'));
  assert.ok(message.includes('- Task: `#937`'));
  assert.ok(message.includes('`pi-dev/lib/den-drift-check.ts`'));
  assert.equal(meta.type, 'drift_check_packet');
  assert.equal(meta.task_id, 937);
  assert.equal(meta.risk, 'low');
});

test('formatDriftCheckPacketMessage omits Task section when task fields are absent', () => {
  const result = analyzeDriftCheck({
    branch: 'task/no-task-fields',
    base_ref: 'main',
    head_commit: 'abc1234',
    changed_paths: [{ status: 'M', path: 'pi-dev/lib/den-drift-check.ts', additions: 1, deletions: 0 }],
  });

  const message = formatDriftCheckPacketMessage(result);

  const h2Headings = message.match(/^## .+$/gm) ?? [];

  assert.ok(message.includes('# Drift Check Packet'));
  assert.ok(!h2Headings.includes('## Task'));
  assert.equal(h2Headings[0], '## Branch and Base');
  assert.match(message, /\*\*Recommendation:\*\* flag-for-review\n\n## Branch and Base/);
});

test('extractTaskIntentFromContextPacket prefers user intent', () => {
  const packet = `
## Task description

Fallback description.

## User intent

Finish delegated-coder workflow foundation before roadmap work.
`;

  assert.equal(
    extractTaskIntentFromContextPacket(packet),
    'Finish delegated-coder workflow foundation before roadmap work.',
  );
});
