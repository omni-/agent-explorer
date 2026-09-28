# Agent Instructions

## Request Scope

- Questions authorize investigation and explanation, not implementation or artifact generation. Use read-only diagnostics and research as needed.
- Do not modify files, generate assets, or start implementation unless requested.

## Protected Areas

- Do not execute large multi-class refactors without asking first.
- If a change appears to require a migration or compatibility shim, stop and ask the user how to handle it before implementing one.
- When editing JSON, preserve the file's existing formatting and merge changes into it rather than overwriting or reformatting unrelated content; if list members are inline, keep them inline.

## Code style

- `.editorconfig` and configured analyzers are the source of truth for deterministic code-style rules. Respect them for all modified files and run the repository's formatting/analyzer checks before finishing.
- Do not run `dotnet format` for JSON-only changes; validate JSON syntax, references, and whitespace with checks appropriate to the changed files instead.

## C# Style

- Follow Microsoft coding conventions.
- Use one public class per file.
- File names should match class names exactly.
- Follow the principle of least privilege when choosing member visibility.
- Use `const` for compile-time constants.
- Use `string.IsNullOrWhiteSpace` when whitespace should count as empty; otherwise use `IsNullOrEmpty` or `is null` as appropriate.
- Prefer `is null` over `== null` and `is not null` over `!= null`.
- Prefer the spread operator over `ToList` when target typed, except when chaining LINQ statements.
- Sort fields/properties by access modifiers one-per-line with an extra newline in between the groups (non-POCO classes only): public then protected then private. e.g.
	```
	public int Id { get; set; }
	public string Name { get; set; }

	public override Object Parent { get; set; }
	public override string Description { get; set; }

	public virtual Foo Bar { get; init; }

	protected int Value1 { get; set; }
	protected string Value2 { get; set; }

	private static readonly string _value = "Value";

	private Texture _texture;
	```
- POCO classes with no functions should separate members with an extra newline

## Architecture

- Avoid "feature envy" - ask good questions of our objects.
- Hide implementation details when possible.
- Expose high-level APIs from classes.
- Adhere to SOLID principles.
- Prefer `abstract` over `virtual` when a base class has no sensible default implementation.
- Never mark something `virtual` speculatively.

## Logging

- Log failures and unexpected early exits, not routine no-ops. Match severity to impact and avoid repeated messages.
- Use warning-level logging for expected but undesirable bail-outs, such as missing optional runtime dependencies, missing content, invalid state, or no valid spawn/location being available.
- Use error-level logging when the bail-out indicates data corruption, impossible state, or behavior that prevents the requested action from completing.
- Log interesting events at information level, or at least verbose level for high-frequency events, so logs can explain meaningful state changes.
- Keep bail-out log messages specific enough to identify the object/content key/state involved, but avoid noisy per-frame logs in hot-path loops unless the condition is throttled or state-gated.

## Performance

- Avoid unnecessary allocations and repeated collection scans in hot paths. Measure performance tradeoffs when the better approach is unclear.

## Verification scope

- Prefer minimal controlled setups, without bypassing the behavior being tested.
- Stop once the question is answered, and recheck only what later edits affect.

## Documentation

- Use XML documentation comments on public members when appropriate.
- Very simple classes and self-explanatory members do not need documentation.

## Prose and comments

- Write plain, direct English, not technical-manual prose. Avoid wording like "provenance", "materialize", "retain as", or "source of truth" when simpler language is equally precise.
- Keep comments short and focused on non-obvious reasons, constraints, or workarounds—not what the code already says.

## Unit Tests

- Follow Arrange, Act, Assert.
- Do not bypass the behavior being tested with mocks.

## Usings

- Keep third-party imports before project imports when both are present. System-first sorting, alphabetical ordering, and import-group spacing are configured in `.editorconfig`.

## Namespaces

- Keep namespaces aligned with directory structure.
- Follow the repository's existing taxonomy when placing types; classify by conceptual ownership and architectural role, not merely by where a type is used.
- Promote meaningful domains or reusable families into their own namespaces only when doing so clarifies the codebase.
- Subdivide broad namespaces when they contain clearly distinct families, but do not create hierarchy just to reduce file count.
- Prefer the namespace that best describes what a type *is* over one describing an incidental consumer. For example, debugging code belongs under `Debugging`, catalogs under `Content/Catalogs`, and animation infrastructure under `Animations`.
- Avoid speculative one-off namespaces.

## Working tree safety

- Always inspect `git status` before making repository changes.
- Preserve all pre-existing user changes.
- Do not modify, revert, format, stage, or otherwise alter unrelated pre-existing changes.
- If the working tree is dirty, scope broad automated fixers and formatters to task-relevant files whenever practical.
- After making changes, distinguish your edits from changes that existed before the task.
