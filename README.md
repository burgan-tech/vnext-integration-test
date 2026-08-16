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
│   │           ├── inbox/              # Embedded YAML — inbox worker sidecar
│   │           ├── outbox/             # Embedded YAML — outbox worker sidecar
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

All operation methods return a `VNextApiResponse` (`StatusCode`, `Headers`, `Body`, `RawBody`, `IsSuccessStatusCode`) and accept an optional `headers` dictionary for per-request headers.

| Method | HTTP | URL Pattern |
|---|---|---|
| `StartInstanceAsync(workflow, body)` | POST | `/api/v{v}/{domain}/workflows/{wf}/instances/start?sync=true` |
| `RunTransitionAsync(workflow, id, transition, body?)` | PATCH | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}/transitions/{t}?sync=true` |
| `GetInstanceAsync(workflow, id)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}` |
| `ListInstancesAsync(workflow, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances` |
| `GetInstanceTransitionsAsync(workflow, id, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}/transitions` |
| `RetryInstanceAsync(workflow, id, body?)` | POST | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}/retry?sync=true` |
| `CallFunctionAsync(fn, params?)` | GET | `/api/v{v}/{domain}/functions/{fn}` |
| `CallWorkflowFunctionAsync(wf, fn, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/functions/{fn}` |
| `CallInstanceFunctionAsync(wf, id, fn, params?)` | GET | `/api/v{v}/{domain}/workflows/{wf}/instances/{id}/functions/{fn}` |
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
                                          └─ vNext inbox + daprd sidecar
                                               └─ vNext outbox + daprd sidecar
                                                    └─ Mocklab + daprd sidecar (long-lived mock HTTP service)
                                                         └─ LocalDomainPublisher (uploads domain definitions)
                                                              └─ OnAfterEnvironmentReadyAsync
```

When the **`VNEXT_BASE_URL` environment variable** is set, Docker containers are skipped and only the domain publish step runs.

**Container roles, aliases and Dapr ports:**

| Role | Image | Network alias | Dapr app-id | Dapr HTTP / gRPC |
|---|---|---|---|---|
| Orchestrator | `{VNextImage}/orchestrator:{tag}` | `test-vnext-app` | `vnext-app-{domain}` | 42110 / 42111 |
| Execution | `{VNextImage}/execution:{tag}` | `test-vnext-execution` | `vnext-execution-app-{domain}` | 43110 / 43111 |
| Inbox | `{VNextImage}/inbox:{tag}` | `test-vnext-inbox` | `vnext-inbox-{domain}` | 44110 / 44111 |
| Outbox | `{VNextImage}/outbox:{tag}` | `test-vnext-outbox` | `vnext-outbox-{domain}` | 45110 / 45111 |
| db-migrator | `{DbMigratorImage}` | `test-vnext-db-migrator` | `vnext-db-migrator-{domain}` | 44210 / 44211 |
| Mocklab | `{MocklabImage}` | `test-mocklab` | `mocklab` | 3500 / 50001 |

Every vNext service container listens on port **5000** inside the network and is health-checked at `/health`. Only the orchestrator's port is mapped to the host and exposed as `OrchestratorBaseUrl`.

**Inbox / Outbox (Aether queues):** the inbox and outbox are `sys_queues`-backed workers that carry vNext's at-least-once messaging. The inbox forwards queued messages into the orchestrator (subflow lifecycle, transition continuations); the outbox drains the orchestrator's outgoing messages. They start after the execution service and are torn down with the rest of the stack.

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
| `MocklabHost` | `"test-mocklab"` | Host substituted for the `MOCKLAB_HOST` / `MOCKOON_HOST` placeholders |
| `EnableMocklab` | `true` | Start the Mocklab container and its sidecar |
| `EnableDomainPublish` | `true` | Run `LocalDomainPublisher` once the stack is ready |

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
| `ApplyAppSettingsSubstitutions(content)` | Replace additional JSON placeholders (`POSTGRES_HOST`, `POSTGRES_DB`, `REDIS_HOST`, `MOCKLAB_HOST`) |
| `GetOrchestratorEnvironment()` | Extend orchestrator container env vars |
| `GetExecutionEnvironment()` | Extend execution container env vars |
| `GetInboxEnvironment()` | Extend inbox container env vars |
| `GetOutboxEnvironment()` | Extend outbox container env vars |
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

1. Scans all JSON component files under `paths.componentsRoot` (skipping `.meta/` directories and `*.diagram.json` files)
2. Applies the version transform `artifactVersion-pkg.{packageVersion}+{domain}` to `version` fields
3. Replaces all `domain` fields with the `appDomain` value (skipping `config` and `process` subtrees)
4. POSTs each component to `/api/v1/definitions/publish`

Supported component types: `schemas`, `workflows`, `tasks`, `functions`, `views`, `extensions`, `mappings`

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
    "extensions": "extensions",
    "mappings": "mappings"
  }
}
```

A component type whose path is missing from `paths` (or whose directory does not exist) is skipped silently, so existing configs without `mappings` keep working.

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

Note that resolution is **per role and all-or-nothing**: as soon as `Infrastructure/DaprComponents/{role}/` exists, *only* the files in that folder are used for that role — the SDK embedded resources for that role are not merged in. To customise a single component, copy the whole role folder and edit the one file.

**Embedded YAML files per role:**

| File | orchestration | execution |            inbox            |      outbox       | db-migrator |
|---|:-:|:-:|:---------------------------:|:-----------------:|:-:|
| `config.yaml` (`vnext-config`) | ✔ | ✔ |             ✔              |        ✔         | ✔ |
| `lock.yaml` (`vnext-lock`) | ✔ | ✔ |             ✔              |        ✔         | ✔ |
| `secretstore.yaml` (`vnext-secret`) | ✔ | ✔ |             ✔              |        ✔         | ✔ |
| `state.yaml` (`vnext-state`) | ✔ | ✔ |             ✔              |        ✔         | — |
| `pubsub.yaml` | ✔ `vnext-pubsub` | ✔ `vnext-pubsub` |      ✔ `vnext-pubsub`      | ✔ `vnext-pubsub` | — |
| `pubsub-broadcast.yaml` (`vnext-pubsub-broadcast`) | ✔ | ✔ |             ✔              |         —         | — |
| `resiliency.yaml` | ✔ `vnext-orchestration-resiliency` | — | ✔ `vnext-inbox-resiliency` |         —         | — |

**`resiliency.yaml`** declares a Dapr `Resiliency` policy rather than a `Component`. Both policies define a circuit breaker only — no retry and no timeout — because retry classification and per-call budgets are owned by the application layer (`ExecutionApi:InvocationTimeoutSeconds` for orchestration → execution, `OrchestrationApi:InvocationTimeoutSeconds` for inbox → orchestration). The rationale is documented inline in each file; read it before adding a retry policy.

A `Resiliency` policy addresses its targets by **Dapr app-id**, and every vNext app-id carries the domain as a suffix. The `targets.apps` keys therefore use the `APP_DOMAIN` placeholder:

| File | Target key | Resolves to (domain `touch`) | Callee |
|---|---|---|---|
| `orchestration/resiliency.yaml` | `vnext-execution-app-APP_DOMAIN` | `vnext-execution-app-touch` | execution |
| `inbox/resiliency.yaml` | `vnext-app-APP_DOMAIN` | `vnext-app-touch` | orchestrator |

A target key written without the suffix matches no app, and Dapr applies the policy to nothing — with no warning at startup. When adding a target, cross-check it against the `DAPR_APP_ID` value in the callee's `Get{Role}Environment()`.

**YAML placeholders:**

| Placeholder | Resolved To | Source |
|---|---|---|
| `APP_DOMAIN` | Value of the `Domain` property | Used in `Resiliency` app-id targets |
| `REDIS_HOST` | Docker network alias (`test-redis`) | `RedisAlias` constant |
| `VAULT_HOST` | Docker network alias (`test-vault`) | `VaultAlias` constant |
| `MOCKLAB_HOST` | Return value of `GetMockoonAlias(role)` | Default: `MocklabHost` → `test-mocklab` |
| `MOCKOON_HOST` | Return value of `GetMockoonAlias(role)` | Legacy alias for `MOCKLAB_HOST`; still substituted |

## appsettings Placeholder Strategy

`ApplyAppSettingsSubstitutions(content)` replaces the following placeholders in all `appsettings.*.json` files:

| Placeholder | Resolved To |
|---|---|
| `POSTGRES_HOST` | Docker network alias (`test-postgres`) |
| `POSTGRES_DB` | Value of the `DatabaseName` property |
| `REDIS_HOST` | Docker network alias (`test-redis`) |
| `MOCKLAB_HOST` | Value of the `MocklabHost` property (`test-mocklab`) |

Use `MOCKLAB_HOST` for any domain setting that must point at the built-in mock service. The template's `appsettings.orchestration.json` ships one such example:

```json
"Example": {
  "ApiBaseUrl": "http://MOCKLAB_HOST:5000"
}
```

Mocklab serves HTTP on port **5000** inside the test network, so the placeholder always needs an explicit `:5000` suffix.

**Config files per role:**

| File | Bind-mounted into |
|---|---|
| `appsettings.orchestration.json` | orchestrator → `/app/appsettings.Development.json` |
| `appsettings.execution.json` | execution → `/app/appsettings.Development.json` |
| `appsettings.inbox.json` | inbox → `/app/appsettings.Development.json` |
| `appsettings.outbox.json` | outbox → `/app/appsettings.Development.json` |
| `appsettings.db-migrator.json` | db-migrator → `/app/appsettings.json` |

All five files are read from `Config/` in the test project's output directory and are **required** — a missing file fails startup with a `FileNotFoundException`.

---

## Development Workflow

### Prerequisites

- .NET 10 SDK
- Docker Desktop (required to run tests)

### Build

```bash
dotnet build ./VNext.Testing.slnx -c Release
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

### Release

Publishing is automated by [`.github/workflows/publish-nuget.yml`](./.github/workflows/publish-nuget.yml), which triggers on a push to a `release-v*` branch (or via `workflow_dispatch`). The workflow:

1. Derives the version from the branch name (`release-v1.2` → next free `1.2.x` tag), or uses the `version_override` input
2. Writes it into `common.props` and stamps `symbols.SdkVersion.defaultValue` in `template.json` with `jq`
3. Builds, packs both projects, and uploads the `.nupkg` files as workflow artifacts
4. Pushes to NuGet.org and creates the `v{version}` git tag

Because the version is stamped by CI, a manual bump is only needed when building packages locally:

1. Update `<PackageVersion>` in `common.props` (applies to both `src/VNext.Testing.Sdk/` and `src/VNext.Testing.Template/`)
2. Update the `VNext.Testing.Sdk` package reference version in the template's `MyDomain.IntegrationTests.csproj`
3. Update the `SdkVersion` default value in `template.json`

#### NuGet.org authentication — Trusted Publishing

The workflow authenticates via **Trusted Publishing (OIDC)**; there is no `NUGET_API_KEY` secret. `NuGet/login@v1` exchanges the job's GitHub OIDC token for an API key that is valid only for that run.

This requires:

| Where | What |
|---|---|
| Workflow `permissions` | `id-token: write` |
| NuGet.org → Account → Trusted Publishing | A policy bound to this repository, `publish-nuget.yml`, and the `release-v*` branches |
| Repo → Settings → Secrets and variables → Actions → **Variables** | `NUGET_USER` — the NuGet.org account (user or organization) that owns the policy |

The workflow reads `NUGET_USER` from Variables first and falls back to Secrets, so it works from either tab. Prefer the **Variables** tab: an account name is not sensitive, and a secret is masked as `***` in the logs, which makes a wrong value harder to spot. Note that `vars` and `secrets` are separate stores — `${{ vars.X }}` does not see a value added under Secrets.

If `NUGET_USER` is set in neither, the workflow fails fast before packing.

---

## Contribution Guidelines

- New operation methods added to the SDK must be declared `virtual` on `VNextApiClient`
- New configuration points added to `VNextTestEnvironment` must use `protected virtual`
- Container fields added to `VNextTestEnvironment` must also be added to the teardown list in `DisposeAsync`, sidecar-first
- Embedded Dapr YAML must be kept in sync across **three** locations for all five roles (`orchestration/`, `execution/`, `inbox/`, `outbox/`, `db-migrator/`):
  - `src/VNext.Testing.Sdk/Resources/DaprComponents/`
  - `src/VNext.Testing.Template/content/VNextIntegrationTestTemplate/Infrastructure/DaprComponents/`
  - `tests/MyDomain.IntegrationTests/Infrastructure/DaprComponents/`
- The same applies to `Config/appsettings.*.json`, which exists in both the template content and `tests/MyDomain.IntegrationTests/`
- Only the **SDK** embeds YAML as resources — via the `Resources\DaprComponents\**\*.yaml` wildcard in `VNext.Testing.Sdk.csproj`, which derives the `{role}/{file}.yaml` logical name automatically, so new files need no csproj change. `ExtractEmbeddedDaprComponentsAsync` reads `typeof(VNextTestEnvironment).Assembly` (always the SDK), so adding `<EmbeddedResource>` items to the template or test project has no effect — those projects reach their YAML from disk through the `Content` + `CopyToOutputDirectory` items
- Because the SDK's logical names come from MSBuild's `%(RecursiveDir)`, pack the SDK on Linux/macOS (as CI does) — a Windows pack would embed `inbox\config.yaml` and the `{role}/` prefix match would fail
- `tests/MyDomain.IntegrationTests/` is the reference implementation and regression safeguard — build the solution after every SDK change (`dotnet build ./VNext.Testing.slnx -c Release`)
