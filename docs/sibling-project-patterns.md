# Sibling Project Patterns

den-mcp follows conventions established across George's other .NET projects. This doc captures those patterns for consistency.

## Common .NET Conventions (all projects)

- **Target**: .NET 10 (`net10.0`)
- **Solution file**: `.slnx` (modern SDK-style)
- **Build props**: Root `Directory.Build.props` with strict settings:
  - `Nullable: enable`
  - `ImplicitUsings: enable`
  - `TreatWarningsAsErrors: true`
  - `DisableTransitiveProjectReferences: true`
  - `EnforceCodeStyleInBuild: true`
  - `AnalysisLevel: latest`
  - `ManagePackageVersionsCentrally: true`
- **Package versions**: `Directory.Packages.props` at root
- **Test infra**: Shared `tests/Directory.Build.props` that adds xunit, test SDK, coverlet
- **DI pattern**: Explicit registration in Program.cs / bootstrap class — no auto-scanning
- **Architecture tests**: Dedicated test project enforcing dependency boundaries via reflection

## Project-Specific Patterns

### QuillForge (agentic novel writing webapp)
- Structure: `Core / Providers / Storage / Web`
- Core: models, services (interfaces), agents, tools, pipeline stages
- Providers: LLM adapters (Anthropic, OpenAI, Ollama, etc.) via `Microsoft.Extensions.AI`
- Storage: File-system persistence with `AtomicFileWriter` (write-to-temp-then-rename)
- Web: ASP.NET Core composition root, REST API + React SPA
- Encryption: AES-256-GCM for API keys via `EncryptedKeyStore`
- Config: YAML-based (`YamlDotNet`)

### VoxelForge (voxel editor + model converter)
- Structure: `Core / Content / LLM / App / Engine.MonoGame`
- Core: pure data models, meshing, serialization — no framework deps
- App: editor state, undo/redo commands, console commands
- Engine: FNA rendering, Myra UI
- LLM: `ChatClientCompletionService` adapter for vision-based voxel editing
- CLI elements use `Spectre.Console` for rich output
- No DI container — explicit constructor injection throughout
- Config: `EditorConfig` JSON file loaded at startup

### RuleWeaver (MonoGame CRPG)
- Structure: `Core / App / Content / Engine.MonoGame` + `ContentTool` + `DevConsole`
- Core: ECS (`SessionState`), rule pipeline, entity/component system — pure domain model
- App: `GameBootstrap` composition root creates `AppRuntime` service context
- Content: JSON-driven definitions with `$kind` discriminator for polymorphism
- ContentTool: CLI with `Spectre.Console` for content validation/editing
- DevConsole: headless simulation runner
- Deterministic: seeded RNG, simulation mode support

## What den-mcp borrows

| Pattern | Source | How used |
|---------|--------|----------|
| `Directory.Build.props` strict settings | All projects | Identical config |
| `tests/Directory.Build.props` shared test deps | QuillForge | Same pattern |
| Explicit DI in Program.cs | QuillForge, RuleWeaver | Server Program.cs |
| Architecture.Tests boundary enforcement | QuillForge, RuleWeaver | Same reflection-based approach |
| `DisableTransitiveProjectReferences` | All projects | Same |
| Core with no framework deps | All projects | Core only has Sqlite + Logging.Abstractions |
| `.slnx` with `/src/` and `/tests/` folders | All projects | Same structure |
