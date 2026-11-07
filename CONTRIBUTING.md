# Contributing to Cabinet

Thank you for your interest in contributing to Cabinet! This guide will help you get started.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Workflow](#development-workflow)
- [Code Standards](#code-standards)
- [Testing Requirements](#testing-requirements)
- [Pull Request Process](#pull-request-process)
- [Architecture Guidelines](#architecture-guidelines)

## Code of Conduct

Be respectful and constructive. We're building software together — treat others as you'd want to be treated. Harassment, discrimination, and unconstructive behavior will not be tolerated.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), which is required. `global.json` pins it (10.0.100 or a later feature band).
- [Docker](https://docs.docker.com/get-docker/) (for container builds, and an easy way to run backends for integration tests)
- Git
- A C# IDE with .NET 10 support (Visual Studio, VS Code with C# Dev Kit, JetBrains Rider)

### First-Time Setup

```bash
# Clone the repository
git clone https://github.com/bogware/cabinet.git
cd cabinet

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run all tests
dotnet test

# Start the API locally
dotnet run --project src/Cabinet.API
```

### Verify Your Setup

```bash
# Health check (API must be running)
curl http://localhost:5068/health

# Test with dev API key
curl -H "X-API-Key: dev-basic-key" http://localhost:5068/api/v1/logs/stats
```

## Development Workflow

### Branch Naming

```
feature/<description>     # New features (e.g., feature/json-parser)
fix/<description>         # Bug fixes (e.g., fix/hec-auth-header)
docs/<description>        # Documentation (e.g., docs/api-guide)
refactor/<description>    # Refactoring (e.g., refactor/query-engine-streaming)
test/<description>        # Test additions (e.g., test/api-controller-tests)
```

### Commit Messages

Write concise commit messages that explain **why**, not just **what**:

```
Good:  Deny capability checks when a principal has no ClientId
Bad:   Update CapabilityAuthorizationFilter.cs
Good:  Add JSON parser for structured log ingestion
Bad:   New parser
```

For multi-line commits:
```
Short summary (50 chars or less)

Longer explanation if needed. Wrap at 72 characters. Explain the
motivation for the change and how it differs from previous behavior.

Fixes #123
```

### Development Loop

1. Create a feature branch from `main`
2. Make changes in small, focused commits
3. Write/update tests for your changes
4. Ensure `dotnet build` succeeds (warnings are errors!)
5. Ensure `dotnet test` passes
6. Push and create a Pull Request

## Code Standards

### Enforced by Build

- **Target framework:** `net10.0`
- **Package versions:** declared centrally in `Directory.Packages.props`
- **Nullable reference types:** Enabled (`<Nullable>enable</Nullable>`)
- **Warnings as errors:** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
- All warnings must be resolved — the build will fail otherwise

### Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Types (classes, interfaces, enums) | PascalCase | `LogEvent`, `ILogStorage` |
| Methods, Properties | PascalCase | `QueryAsync`, `Timestamp` |
| Local variables, parameters | camelCase | `logEvent`, `queryContext` |
| Private fields | _camelCase | `_storage`, `_logger` |
| Constants | PascalCase | `MaxRetries`, `DefaultTimeout` |
| Interfaces | I-prefix + PascalCase | `ILogParser`, `IRbacService` |

### Code Style

```csharp
// File-scoped namespaces
namespace Cabinet.Core.Models;

// Sealed classes where inheritance not intended
public sealed class LogEvent { }

// Required modifier for mandatory properties
public required string RawLine { get; set; }

// Async methods end with Async suffix
public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken ct = default)

// CancellationToken as last parameter, with default value
public async IAsyncEnumerable<LogEvent> QueryAsync(
    QueryContext context,
    [EnumeratorCancellation] CancellationToken ct = default)

// Use expression-bodied members for simple operations
public string Name => _config.Name;

// XML doc comments on public interfaces
/// <summary>
/// Stores a single log event.
/// </summary>
/// <param name="logEvent">The event to store.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>True if stored successfully.</returns>
Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default);
```

### Architecture Rules

1. **Core depends on nothing** — `Cabinet.Core` has no project references
2. **All other projects depend on Core** — Models and interfaces live in Core
3. **No circular dependencies** — If A references B, B cannot reference A
4. **Interface-driven** — Public services expose interfaces, not implementations
5. **DI registration** — New services must be registered in `Program.cs` or `ServiceCollectionExtensions.cs`
6. **Thread-safety** — All shared mutable state must use proper synchronization (see patterns in ARCHITECTURE.md)

### What NOT to Do

- Don't add `using` directives that are covered by implicit usings
- Don't add empty catch blocks — always log or rethrow
- Don't use `string.Format()` — use string interpolation
- Don't store secrets in code — use environment variables or secrets manager
- Don't use `Thread.Sleep()` — use `await Task.Delay()`
- Don't add NuGet packages without discussion — propose in the PR description

## Testing Requirements

### Before Submitting a PR

```bash
# All tests must pass
dotnet test

# Build must succeed (includes warning-as-error check)
dotnet build -c Release

# Skip tests that need external backends
dotnet test --filter "Category!=Integration"
```

`Integration` is the only test category. Postgres and OpenSearch integration tests skip themselves unless `CABINET_TEST_POSTGRES_CONNSTR` or `CABINET_TEST_OPENSEARCH_URL` (plus optional `_USER` and `_PASS`) is set.

`api-spec.json` is generated from the API and checked by a test. If you change the API surface, regenerate it:

```bash
CABINET_UPDATE_API_SPEC=1 dotnet test --filter ApiSpec_MatchesTheRunningApi
```

### Test Coverage Expectations

| Change Type | Test Requirement |
|-------------|-----------------|
| New feature | Unit tests + integration test if applicable |
| Bug fix | Regression test that fails without fix, passes with fix |
| Refactoring | Existing tests must still pass; add tests if coverage was low |
| API endpoint | Controller unit test with mock services |
| Storage backend | Integration test with actual backend (or in-memory substitute) |
| BQL stage/function | Parser test + execution test with sample data |
| Detection rule | Test with both matching and non-matching events |

### Test Structure

```csharp
namespace Cabinet.Tests.Query;

public class BqlParserTests
{
    private readonly BqlParser _parser = new();

    [Fact]
    public void ParseBql_SimpleSearch_ProducesSearchStage()
    {
        // Arrange
        var text = "find errors";

        // Act
        var query = _parser.ParseBql(text);

        // Assert
        query.Stages.Should().HaveCount(1);
        query.Stages[0].Should().BeOfType<SearchStage>();
    }

    [Theory]
    [InlineData("find *")]
    [InlineData("search *")]
    [InlineData("* | head 10")]
    public void ParseBql_ValidQueries_DoesNotThrow(string query)
    {
        var act = () => _parser.ParseBql(query);
        act.Should().NotThrow();
    }
}
```

### Test Location

```
tests/Cabinet.Tests/
  Agent/          # Forwarding agent tests
  CLI/            # CLI tests
  Controllers/    # API controller tests
  Core/           # Core model and utility tests
  Detection/      # Detection suite tests
  Middleware/     # API middleware tests
  Parsers/        # Log parser tests
  Query/          # BQL lexer, parser, engine, optimizer tests
  Services/       # API service and startup tests
  Storage/        # Storage backend tests
tests/EndToEnd/
  run-tests.sh    # CLI-driven end-to-end script
```

## Pull Request Process

### PR Title Format

```
[Component] Short description

Examples:
[Query] Fix eventstats group-global aggregation bug
[API] Add scheduled search REST endpoints
[Storage] Implement PostgreSQL search_after pagination
[Docs] Add API integration guide
```

### PR Description Template

```markdown
## Summary
Brief description of what this PR does and why.

## Changes
- Bullet list of specific changes
- One line per logical change

## Testing
- [ ] All existing tests pass
- [ ] New tests added for changed/new code
- [ ] Manual testing performed (describe what you tested)

## Breaking Changes
List any breaking changes, or "None"

## Related Issues
Fixes #123, Related to #456
```

### Review Checklist

Before requesting review, verify:

- [ ] Code builds without warnings (`dotnet build -c Release`)
- [ ] All tests pass (`dotnet test`)
- [ ] New code has tests
- [ ] No secrets or credentials in code
- [ ] Thread-safety considered for shared state
- [ ] CancellationToken propagated through async chains
- [ ] Error handling follows existing patterns (no empty catches)
- [ ] XML doc comments on new public interfaces/models

### Review Process

1. At least one maintainer approval required
2. CI pipeline must pass (build + test)
3. No unresolved review comments
4. Squash-merge to `main` (preserves clean history)

## Architecture Guidelines

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed architecture documentation including:

- Layer diagram and dependency rules
- Key design decisions (ADRs)
- Thread-safety patterns
- Storage backend implementation guide
- BQL query engine internals
- Adding new components (parsers, stages, storage backends)

## Questions?

- Open an [Issue](https://github.com/bogware/cabinet/issues) for questions, bugs or feature requests
- Report security vulnerabilities as described in [SECURITY.md](SECURITY.md), not in an issue
- See [README.md](README.md) for project overview and quick start
