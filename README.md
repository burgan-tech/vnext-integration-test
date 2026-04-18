# vNext Integration Testing — SDK & Template

This document is intended for the **vNext Platform team**. It covers the SDK architecture, component reference, and development/release workflows.

For instructions on how domain teams use this infrastructure in their own projects, see [GETTING_STARTED.md](./GETTING_STARTED.md).

---

## Motivation

The goal is to enable teams building on vNext to run consistent, reliable integration tests as part of every release cycle. Two deliverables have been defined to achieve this:

| Deliverable | Purpose |
|---|---|
| `VNext.Testing.Sdk` | Packages reusable infrastructure components (HTTP client, Docker stack, domain publisher) as a NuGet library |
| `VNext.Testing.Template` | Scaffolds a ready-to-run integration test project via `dotnet new` with zero configuration overhead |

---

## Repository Structure

```
vnext-integration/
├── src/
│   ├── VNext.Testing.Sdk/              # Shared infrastructure SDK (NuGet package)
│   │   ├── Client/
│   │   │   ├── VNextApiClient.cs
│   │   │   └── VNextApiClientOptions.cs
│   │   ├── Infrastructure/
│   │   │   ├── VNextTestEnvironment.cs
│   │   │   ├── IntegrationTestBase.cs
│   │   │   └── LocalDomainPublisher.cs
│   │   ├── Builders/
│   │   │   └── TestDataBuilderBase.cs
│   │   └── Resources/
│   │       └── DaprComponents/
│   │           ├── orchestration/      # Embedded YAML — orchestrator sidecar
│   │           ├── execution/          # Embedded YAML — execution sidecar
│   │           └── db-migrator/        # Embedded YAML — migrator sidecar
│   └── VNext.Testing.Template/         # dotnet new template package
│       └── content/
│           └── VNextIntegrationTestTemplate/
├── tests/
│   └── MyDomain.IntegrationTests/     # Sample project (debug / local development)
├── common.props
├── VNext.Testing.slnx
└── .gitignore
```

---

## Architecture

```
┌─────────────────────────────┐
│     VNext.Testing.Sdk       │  NuGet package — shared infrastructure
│                             │
│  VNextApiClient             │
│  VNextTestEnvironment       │
│  IntegrationTestBase<T>     │
│  LocalDomainPublisher       │
│  TestDataBuilderBase        │
└──────────────┬──────────────┘
               │ reference (NuGet or ProjectReference)
               ▼
┌─────────────────────────────┐     ┌─────────────────────────────────────┐
│  VNext.Testing.Template     │────▶│  Team Project                       │
│  (dotnet new template)      │     │  MorphFx.IntegrationTests           │
└─────────────────────────────┘     │                                     │
                                    │  VNextTestEnvironment (override)    │
                                    │  IntegrationTestBase (override)     │
                                    │  TestDataBuilder (domain-specific)  │
                                    │  Tests/                             │
                                    └─────────────────────────────────────┘
```

---

## SDK Component Reference

### `VNextApiClient`

A typed HTTP client for the vNext Runtime REST API. All operation methods are `virtual`, so subclasses can freely change behaviour.

**Constructors:**

```csharp
new VNextApiClient("http://localhost:5000")

new VNextApiClient(new VNextApiClientOptions
{
    BaseUrl        = "http://localhost:5000",
    Domain         = "touch",
    ApiVersion     = "1",
    TimeoutSeconds = 60
})
```

**Operation methods:**

| Method | HTTP | URL Pattern |
|---|---|---|
| `StartInstanceAsync(workflow, body)` | POST | `/api/v{v}/{domain}/workflows/{wf}/instances/start?sync=true` |
| `RunTransitionAsync(workflow, id, transition, body?)` | PATCH | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}/transitions/{t}?sync=true` |
| `GetInstanceAsync(workflow, id)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}` |
| `ListInstancesAsync(workflow, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances` |
| `CallFunctionAsync(fn, params?)` | GET | `/api/v{v}/{domain}/functions/{fn}` |
| `CallWorkflowFunctionAsync(wf, fn, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/functions/{fn}` |
| `WaitForHealthyAsync(timeout?)` | GET | `/health` |
| `GetRawAsync(path)` | GET | any path |

**Overridable hooks:**

| Hook | When to Override |
|---|---|
| `CreateHttpClient()` | To supply an `IHttpClientFactory`-managed client or a custom handler chain |
| `ConfigureHttpClient(http)` | To add default headers (e.g. `Authorization`) |
| `OnBeforeRequestAsync(request)` | To inject per-request headers (e.g. correlation ID) |
| `OnNonSuccessResponseAsync(op, status, body)` | To implement retry, circuit-breaker, or custom error handling |

---

### `VNextApiClientOptions`

| Property | Default | Description |
|---|---|---|
| `BaseUrl` | `http://localhost:5000` | Orchestrator address |
| `Domain` | `touch` | Domain slug used in REST path segments |
| `ApiVersion` | `1` | The `v{n}` segment in the URL |
| `TimeoutSeconds` | `60` | HTTP client timeout |
| `AcceptLanguage` | `tr-TR` | Language header sent with every request |
| `AdditionalHeaders` | `{}` | Extra headers added to every request |

---

### `VNextTestEnvironment`

Manages the full vNext Docker stack using Testcontainers. Startup sequence:

```
Docker network
  └─ PostgreSQL + Redis + Vault (parallel)
       └─ Vault secret seeding
            └─ CreateDatabase
                 └─ Dapr component YAMLs + appsettings preparation (all roles)
                      └─ Dapr placement + scheduler (parallel)
                           └─ db-migrator + daprd sidecar (run-to-completion — exits after migration)
                                └─ vNext orchestrator + daprd sidecar
                                     └─ vNext execution + daprd sidecar
                                          └─ Mocklab + daprd sidecar (long-lived mock HTTP service)
                                               └─ LocalDomainPublisher (uploads domain definitions)
                                                    └─ OnAfterEnvironmentReadyAsync
```

When the **`VNEXT_BASE_URL` environment variable** is set, Docker containers are skipped and only the domain publish step runs.

**Overridable properties:**

| Property | Default | Description |
|---|---|---|
| `Domain` | `"touch"` | `APP_DOMAIN` container env var |
| `DatabaseName` | `"vNext_{Domain}_Test"` | PostgreSQL database name |
| `VNextImage` | `"ghcr.io/burgan-tech/vnext"` | Base image for orchestrator/execution/migrator |
| `VNextImageVersion` | `"latest"` | Image tag shared by all vNext containers |
| `DbMigratorImage` | `"{VNextImage}/db-migrator:{VNextImageVersion}"` | Full image reference for the db-migrator worker |
| `PostgresImage` | `"postgres:latest"` | PostgreSQL image tag |
| `RedisImage` | `"redis:7.0-alpine"` | Redis image tag |
| `VaultImage` | `"vault:1.13.3"` | HashiCorp Vault image tag |
| `DaprPlacementImage` | `"daprio/dapr:latest"` | Dapr placement image |
| `DaprSchedulerImage` | `"daprio/scheduler:latest"` | Dapr scheduler image |
| `DaprSidecarImage` | `"daprio/daprd:latest"` | daprd sidecar image |
| `MocklabImage` | `"ghcr.io/burgan-tech/mocklab:latest"` | Mocklab mock service image |
| `MocklabSeedDirectory` | `Infrastructure/MocklabSeed/` (relative to output dir) | Host directory bind-mounted as seed data into Mocklab |

**Overridable methods:**

| Method | Purpose |
|---|---|
| `StartInfrastructureAsync()` | Customise or add Postgres/Redis/Vault containers |
| `SeedVaultAsync()` | Change the Vault seeding logic |
| `GetVaultSecrets()` | Return domain-specific secret paths |
| `CreateDatabaseAsync()` | Run extra pre-migration logic (after DB is created, before migrator) |
| `RunDbMigratorAsync(componentsDir, settingsPath)` | Override migrator startup / wait strategy |
| `GetDbMigratorEnvironment()` | Add extra env vars injected into the migrator container |
| `StartMocklabAsync()` | Override Mocklab + sidecar startup, health check, or seed mount |
| `GetMockoonAlias(role)` | Change the mock HTTP server network alias (default: `test-mocklab`) |
| `PrepareDaprComponentsAsync(role)` | Change the YAML resolution strategy |
| `ApplyDaprComponentSubstitutions(content, role)` | Replace additional YAML placeholders |
| `PrepareAppSettingsAsync(fileName)` | Change the appsettings source |
| `ApplyAppSettingsSubstitutions(content)` | Replace additional JSON placeholders (`POSTGRES_HOST`, `POSTGRES_DB`, `REDIS_HOST`) |
| `GetOrchestratorEnvironment()` | Extend orchestrator container env vars |
| `GetExecutionEnvironment()` | Extend execution container env vars |
| `OnAfterEnvironmentReadyAsync()` | Extension point — runs after the full stack is ready; start additional services or perform post-startup setup |

---

### `IntegrationTestBase<TEnvironment>`

Generic base class built on the xUnit `ICollectionFixture` mechanism. When a test class is annotated with `[Collection("VNextIntegration")]`, the same `TEnvironment` fixture instance is shared across all test classes.

**Protected members:**

| Member | Type | Description |
|---|---|---|
| `Environment` | `TEnvironment` | Access to the running Docker stack |
| `Api` | `VNextApiClient` | Pre-configured HTTP client |
| `CreateApiClient(baseUrl)` | `virtual` | Override to supply a custom client |
| `GetCurrentState(instance)` | `static` | Extracts `currentState` from a JSON response |

---

### `LocalDomainPublisher`

Walks up from the test assembly directory to find `vnext.config.json`, then:

1. Scans all JSON component files under `paths.componentsRoot`
2. Applies the version transform `artifactVersion-pkg.{packageVersion}+{domain}` to `version` fields
3. Replaces all `domain` fields with the `appDomain` value
4. POSTs each component to `/api/v1/definitions/publish`
5. Triggers a re-initialization via `/api/v1/definitions/re-initialize`

Supported component types: `schemas`, `workflows`, `tasks`, `functions`, `views`, `extensions`

**Expected `vnext.config.json` structure:**

```json
{
  "version": "1.0.0",
  "domain": "touch",
  "paths": {
    "componentsRoot": "core",
    "schemas": "schemas",
    "workflows": "workflows",
    "tasks": "tasks",
    "functions": "functions",
    "views": "views",
    "extensions": "extensions"
  }
}
```

---

### `TestDataBuilderBase`

Abstract base class for domain-specific `TestDataBuilder` types. All helper methods are `protected static` and are inherited by subclasses.

**Helper methods:**

| Method | Description |
|---|---|
| `BuildInstancePayload(key, tags?, attributes?)` | Standard workflow start/transition body |
| `BuildTransitionBody(attributes)` | Transition body containing only `attributes` |
| `UniqueKey(prefix)` | Unique key in `prefix-{guid}` format |
| `DeterministicKey(prefix, parts[])` | Deterministic key derived from a list of identifiers |
| `BuildCustomWorkingHours(schedule)` | Converts a schedule dictionary to the `{day: [{start, end}]}` shape |
| `DefaultWeekdaySchedule(...)` | Parameterised default weekly schedule |

---

## Dapr Component YAML Strategy

`VNextTestEnvironment.PrepareDaprComponentsAsync(role)` uses the following resolution order:

```
1. If Infrastructure/DaprComponents/{role}/ exists in the test project → LOCAL OVERRIDE
2. Otherwise → extracted from SDK embedded resources
```

This allows teams to include only the YAML files they need to customise; everything else is provided automatically by the SDK.

**Embedded YAML files (common to orchestration and execution roles):**
`state.yaml`, `secretstore.yaml`, `pubsub.yaml`, `pubsub-broadcast.yaml`, `lock.yaml`, `config.yaml`

**Orchestration-only:**
`invalidate-cache-subscription.yaml`

**Execution-only:**
`notification-binding.yaml` (contains the `MOCKOON_HOST` placeholder)

**db-migrator role:**
`config.yaml`, `lock.yaml`, `secretstore.yaml`

**YAML placeholders:**

| Placeholder | Resolved To | Source |
|---|---|---|
| `REDIS_HOST` | Docker network alias (`test-redis`) | `RedisAlias` constant |
| `VAULT_HOST` | Docker network alias (`test-vault`) | `VaultAlias` constant |
| `MOCKOON_HOST` | Return value of `GetMockoonAlias(role)` | Default: `test-mocklab` (built-in Mocklab alias) |

## appsettings Placeholder Strategy

`ApplyAppSettingsSubstitutions(content)` replaces the following placeholders in all `appsettings.*.json` files:

| Placeholder | Resolved To |
|---|---|
| `POSTGRES_HOST` | Docker network alias (`test-postgres`) |
| `POSTGRES_DB` | Value of the `DatabaseName` property |
| `REDIS_HOST` | Docker network alias (`test-redis`) |

---

## Development Workflow

### Prerequisites

- .NET 10 SDK
- Docker Desktop (required to run tests)

### Build

```bash
cd ai-docs/tests
dotnet build MorphTouch.IntegrationTests.sln
```

### Build the SDK Package

```bash
dotnet pack src/VNext.Testing.Sdk/VNext.Testing.Sdk.csproj -c Release -o ./artifacts
```

Output: `artifacts/VNext.Testing.Sdk.{version}.nupkg`

### Build the Template Package

```bash
dotnet pack src/VNext.Testing.Template/VNext.Testing.Template.csproj -c Release -o ./artifacts
```

Output: `artifacts/VNext.Testing.Template.{version}.nupkg`

### Test the Template Locally

```bash
# Install
dotnet new install ./artifacts/VNext.Testing.Template.1.0.0.nupkg

# Scaffold a project
dotnet new vnext-integration-test --DomainName TestDomain --AppDomain testdomain -o /tmp/test-scaffold

# Uninstall
dotnet new uninstall VNext.Testing.Template
```

### Version Bump

1. Update `<PackageVersion>` in `common.props` (applies to both `src/VNext.Testing.Sdk/` and `src/VNext.Testing.Template/`)
3. Update the `VNext.Testing.Sdk` package reference version in the template's `MyDomain.IntegrationTests.csproj`
4. Update the `SdkVersion` default value in `template.json`
5. Build the packages and publish to the NuGet feed

---

## POC Project (MorphTouch)

`MorphTouch.IntegrationTests.Dapr/` serves as both the reference implementation of the SDK and a regression safeguard. It references the SDK via `ProjectReference` and contains the following overrides:

| File | Override |
|---|---|
| `Infrastructure/VNextTestEnvironment.cs` | `Domain = "touch"`, `DatabaseName`, `VNextImage` |
| `Infrastructure/IntegrationTestBase.cs` | `CreateApiClient` → `VNextApiClientOptions { Domain = "touch" }` |
| `Helpers/TestDataBuilder.cs` | 15+ MorphTouch-specific workflow payload factories |

---

## Contribution Guidelines

- New operation methods added to the SDK must be declared `virtual` on `VNextApiClient`
- New configuration points added to `VNextTestEnvironment` must use `protected virtual`
- When embedded Dapr YAML files are changed, both `src/VNext.Testing.Sdk/Resources/DaprComponents/` and `src/VNext.Testing.Template/content/VNextIntegrationTestTemplate/Infrastructure/DaprComponents/` must be updated in sync (all three roles: `orchestration/`, `execution/`, `db-migrator/`)
- The MorphTouch POC project must be built and verified after every SDK change (`dotnet build MorphTouch.IntegrationTests.sln`)
