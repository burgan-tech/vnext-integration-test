# vNext Integration Test Project — Getting Started Guide

This guide walks domain teams through creating an integration test project from scratch, configuring it, and writing their first tests.

> For the platform team's development and SDK release workflows, see [README.md](./README.md).

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Scaffolding a Project from the Template](#2-scaffolding-a-project-from-the-template)
3. [Configuration](#3-configuration)
4. [Writing Your First Test](#4-writing-your-first-test)
5. [Running Tests](#5-running-tests)
6. [Extension Scenarios](#6-extension-scenarios)

---

## 1. Prerequisites

Verify the following before proceeding:

| Requirement | Details |
|---|---|
| **.NET 10 SDK** | Verify with `dotnet --version` |
| **Docker Desktop** | Must be running so Testcontainers can start containers |
| **`vnext.config.json`** | Must exist at the repository root; `LocalDomainPublisher` searches for it by walking up the directory tree |

**Example `vnext.config.json`:**

```json
{
  "version": "1.0.0",
  "domain": "morphfx",
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

> Any component type missing from `paths` — or whose directory doesn't exist — is skipped, so you only need to list the types your domain actually uses.

---

## 2. Scaffolding a Project from the Template

### 2.1 Install the Template

```bash
dotnet new install VNext.Testing.Template::1.0.0
```

Verify the installation:

```bash
dotnet new list | grep vnext
# vNext Integration Test Project  vnext-integration-test  [C#]  Test/Integration/vNext/Dapr/xUnit
```

### 2.2 Create a New Project

```bash
dotnet new vnext-integration-test \
  --DomainName MorphFx \
  --AppDomain morphfx \
  --VNextImage ghcr.io/burgan-tech/vnext \
  -o MorphFx
```

**Parameter reference:**

| Parameter | Description | Example |
|---|---|---|
| `--DomainName` | C# namespace and project name prefix (PascalCase) | `MorphFx`, `PortfolioManager` |
| `--AppDomain` | Lowercase slug used in vNext API paths and container env vars | `morphfx`, `portfolio-manager` |
| `--VNextImage` | Base container image for the vNext orchestrator and execution services | `ghcr.io/burgan-tech/vnext` |
| `--SdkVersion` | `VNext.Testing.Sdk` NuGet package version to reference | `1.0.0` |

### 2.3 Generated Project Structure

```
MorphFx.IntegrationTests/
├── MorphFx.IntegrationTests.csproj
├── test.runsettings
├── test.runsettings.local               # Git-ignored; personal overrides (IDE settings point here)
├── .gitignore
├── .vscode/
│   └── settings.json                    # VS Code test runner runsettings path
├── Config/
│   ├── appsettings.orchestration.json   # Orchestrator container configuration
│   ├── appsettings.execution.json       # Execution container configuration
│   ├── appsettings.inbox.json           # Inbox worker configuration
│   ├── appsettings.outbox.json          # Outbox worker configuration
│   └── appsettings.db-migrator.json     # DB migrator worker configuration
├── Infrastructure/
│   ├── VNextTestEnvironment.cs          # Docker stack manager (domain overrides)
│   ├── IntegrationTestBase.cs           # xUnit base class + collection fixture
│   ├── MocklabSeed/                     # Seed JSON files mounted into the Mocklab container
│   └── DaprComponents/
│       ├── orchestration/               # Orchestrator Dapr component YAMLs
│       ├── execution/                   # Execution Dapr component YAMLs
│       ├── inbox/                       # Inbox worker Dapr component YAMLs
│       ├── outbox/                      # Outbox worker Dapr component YAMLs
│       └── db-migrator/                 # DB migrator Dapr component YAMLs (config, lock, secretstore)
├── Helpers/
│   └── TestDataBuilder.cs               # Domain-specific payload factories
└── Tests/
    └── SmokeTests.cs                    # Ready-to-run smoke test example
```

---

## 3. Configuration

After scaffolding, there are **7 areas** that must be updated to match your domain.

### 3.1 `Infrastructure/VNextTestEnvironment.cs`

Sets the domain-specific Docker stack parameters.

```csharp
public class VNextTestEnvironment : VNext.Testing.Sdk.Infrastructure.VNextTestEnvironment
{
    // REQUIRED: APP_DOMAIN value passed to vNext containers.
    // Must match the "domain" field in vnext.config.json.
    protected override string Domain => "morphfx";

    // REQUIRED: PostgreSQL database name created for tests.
    protected override string DatabaseName => "vNext_MorphFx_Test";

    // REQUIRED: Base container image address (e.g. ghcr.io/burgan-tech/vnext).
    // The SDK appends the role suffix (/orchestrator:latest, /execution:latest).
    protected override string VNextImage => "ghcr.io/burgan-tech/vnext";
}
```

> The `Domain` value must match the `domain` field in `vnext.config.json` and the `VNextApiClientOptions.Domain` value in `IntegrationTestBase.cs`.

### 3.2 `Infrastructure/IntegrationTestBase.cs`

Specifies the domain slug the HTTP client uses when building request URLs.

```csharp
protected override VNextApiClient CreateApiClient(string baseUrl) =>
    new(new VNextApiClientOptions
    {
        BaseUrl    = baseUrl,
        Domain     = "morphfx",   // Must match VNextTestEnvironment.Domain
        ApiVersion = "1"
    });
```

### 3.3 `Config/appsettings.orchestration.json`

Configuration file bind-mounted into the orchestrator container. Fields to update:

| Field | Description |
|---|---|
| `ApplicationName` | Identifies the application — appears in logs |
| `ConnectionStrings.Default` | Contains the `POSTGRES_HOST` / `POSTGRES_DB` placeholders — do not edit them |
| `Redis.Standalone.EndPoints` | Contains the `REDIS_HOST` placeholder — do not edit it |
| `ExecutionApi.AppId` | Must follow the `vnext-execution-app-{domain}` format |
| `Example.ApiBaseUrl` | Sample external API address, pointed at Mocklab via `http://MOCKLAB_HOST:5000` — rename the section to your domain's setting, keep the placeholder |
| `WorkingHours` | Update to reflect your domain's calendar hours (if applicable) |

> Do not modify the `POSTGRES_HOST`, `POSTGRES_DB`, `REDIS_HOST` and `MOCKLAB_HOST` placeholders. `VNextTestEnvironment` replaces them automatically with the real Docker network aliases at startup.

**Pointing a domain setting at the mock service**

Any setting that would otherwise hold an external HTTP address should use `MOCKLAB_HOST` so it resolves to the built-in Mocklab container instead of a real service. Mocklab listens on port **5000** inside the test network:

```json
"PaymentGateway": {
  "ApiBaseUrl": "http://MOCKLAB_HOST:5000"
}
```

At startup this becomes `http://test-mocklab:5000`. If you point tests at a mock server you host yourself, override `MocklabHost` instead of hardcoding the address:

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override string MocklabHost => "my-shared-mock";
```

> `MOCKLAB_HOST` substitutes only the host — always write the `:5000` port yourself. If you set `EnableMocklab => false`, remove or repoint these settings; the placeholder will still be substituted but nothing will be listening.

### 3.4 `Config/appsettings.execution.json`

Configuration file bind-mounted into the execution container. Fields to update:

| Field | Description |
|---|---|
| `ApplicationName` | Identifies the application |
| `OrchestrationApi.AppId` | Must follow the `vnext-app-{domain}` format |
| `Redis.Standalone.EndPoints` | Contains the `REDIS_HOST` placeholder — do not edit it |
| `TriggerRetry` | Retry policy for trigger dispatch (defaults retry on `409`) |

> Notification bindings are configured on the **orchestrator** side, under `Dapr.Notification` in `appsettings.orchestration.json` (`BindingPrefix`, `DefaultOperation`, `TimeoutSeconds`). The SDK no longer ships a default `notification-binding.yaml` — add one to `Infrastructure/DaprComponents/execution/` if your domain sends notifications (see §6.4).

### 3.5 `Config/appsettings.inbox.json`

Configuration file bind-mounted into the inbox worker container. The inbox drains the `sys_queues` inbox table and forwards each message into the orchestrator (subflow lifecycle, transition continuations), which is what gives vNext its at-least-once delivery guarantee.

| Field | Description |
|---|---|
| `ApplicationName` | Identifies the application — appears in logs |
| `ConnectionStrings.Default` | Contains the `POSTGRES_HOST` / `POSTGRES_DB` placeholders — do not edit them |
| `OrchestrationApi.AppId` | Must follow the `vnext-app-{domain}` format |
| `OrchestrationApi.InvocationTimeoutSeconds` | Per-forward budget (default `60`). Owns the timeout — the Dapr resiliency policy deliberately declares none |
| `Aether.Inbox.Schema` | Database schema holding the queue tables (default: `sys_queues`) |
| `Aether.Inbox.MaxRetryCount` / `RetryBaseDelay` | Redelivery policy before a message is dead-lettered |
| `Aether.Inbox.ProcessingBatchSize` / polling intervals | Throughput knobs — the defaults are tuned for tests and rarely need changing |

### 3.6 `Config/appsettings.outbox.json`

Configuration file bind-mounted into the outbox worker container. The outbox publishes messages the orchestrator wrote transactionally to the `sys_queues` outbox table.

| Field | Description |
|---|---|
| `ApplicationName` | Identifies the application — appears in logs |
| `ConnectionStrings.Default` | Contains the `POSTGRES_HOST` / `POSTGRES_DB` placeholders — do not edit them |
| `Aether.Outbox.Schema` | Must match `Aether.Outbox.Schema` in `appsettings.orchestration.json` (default: `sys_queues`) |
| `Aether.Outbox.BatchSize` / polling intervals | Drain throughput knobs |
| `ResultRetry` | Retry policy for transient `TransitionLocked` / `Locked` results |

> The inbox and outbox both talk to the same database as the orchestrator. If you override `DatabaseName`, leave `POSTGRES_DB` in place in all five config files — it is substituted with that value at startup.

### 3.7 `Config/appsettings.db-migrator.json`

Configuration file bind-mounted into the db-migrator worker container. It runs before the orchestrator and execution services and exits after completing the database schema migration.

| Field | Description |
|---|---|
| `ConnectionStrings.Default` | Contains `POSTGRES_HOST` and `POSTGRES_DB` placeholders — do not edit them; they are replaced automatically at startup |
| `Runtime.EnableSchemaMigration` | Set to `true` to enable schema migration on startup |
| `Redis.Standalone.EndPoints` | Contains the `REDIS_HOST` placeholder — do not edit it |

> `POSTGRES_DB` is resolved to the value of `VNextTestEnvironment.DatabaseName` (e.g. `vNext_MorphFx_Test`).

### 3.8 `Infrastructure/DaprComponents/`

The template places copies of the SDK's default YAML files in this folder across five roles:

| Role folder | Used by | Components |
|---|---|---|
| `orchestration/` | vNext orchestrator daprd sidecar | config, lock, secretstore, state, pubsub, pubsub-broadcast, resiliency |
| `execution/` | vNext execution daprd sidecar | config, lock, secretstore, state, pubsub, pubsub-broadcast |
| `inbox/` | inbox worker daprd sidecar | config, lock, secretstore, state, pubsub, pubsub-broadcast, resiliency |
| `outbox/` | outbox worker daprd sidecar | config, lock, secretstore, state, pubsub |
| `db-migrator/` | db-migrator worker | config, lock, secretstore |

You have two options per role:

**Option A — Use SDK defaults (recommended):**
Delete the role folder entirely (e.g. `Infrastructure/DaprComponents/db-migrator/`). `VNextTestEnvironment` detects its absence and automatically loads the YAML files from the SDK embedded resources.

**Option B — Customise:**
Copy the role folder and edit the files you need. Resolution is **per role and all-or-nothing** — once the folder exists, *only* its files are used for that role, and the SDK's embedded defaults for that role are not merged in. Deleting individual files from a role folder therefore removes those components entirely rather than falling back to the SDK.

> Leave the `APP_DOMAIN`, `REDIS_HOST`, `VAULT_HOST` and `MOCKLAB_HOST` placeholders in all YAML files as-is — they are substituted at runtime.

**About `resiliency.yaml`**

`orchestration/resiliency.yaml` and `inbox/resiliency.yaml` declare Dapr `Resiliency` policies (not `Component`s). Each defines a **circuit breaker only** — no retry, no timeout — and the files carry a long inline comment explaining why:

- Dapr's service-invocation retry cannot distinguish "the app ran the task and returned 500" from "the sidecar never reached the app", so a blanket retry would re-invoke side-effecting tasks.
- Retry classification therefore lives in the application layer (`RemoteInvokerService` for orchestration → execution, `DaprOrchestrationForwarder` for inbox → orchestration).
- Per-call budgets are owned by `ExecutionApi:InvocationTimeoutSeconds` and `OrchestrationApi:InvocationTimeoutSeconds`.

Read those comments before adding a retry or timeout policy — doing so causes cross-layer retry amplification.

**Target keys are app-ids, and app-ids end in your domain.** A `Resiliency` policy addresses each callee by Dapr app-id, and every vNext app-id is suffixed with the domain (`vnext-app-morphfx`, `vnext-execution-app-morphfx`, …). The shipped files use the `APP_DOMAIN` placeholder so this resolves automatically:

```yaml
  targets:
    apps:
      # becomes vnext-execution-app-morphfx when Domain => "morphfx"
      vnext-execution-app-APP_DOMAIN:
        circuitBreaker: invocationBreaker
```

| File | Target key | Callee |
|---|---|---|
| `orchestration/resiliency.yaml` | `vnext-execution-app-APP_DOMAIN` | execution service |
| `inbox/resiliency.yaml` | `vnext-app-APP_DOMAIN` | orchestrator |

> A target key without the domain suffix matches no app, so Dapr silently applies the policy to nothing — there is no startup warning. If you add a target of your own, verify it against the callee's `DAPR_APP_ID`.

If your domain uses cross-domain `DirectTrigger` targets, those live in *other* domains and cannot be derived from `APP_DOMAIN` — add one `targets.apps` entry per known remote domain, with that domain's name written out:

```yaml
      vnext-execution-app-APP_DOMAIN:      # your own domain
        circuitBreaker: invocationBreaker
      vnext-execution-app-payments:        # a remote domain you call
        circuitBreaker: invocationBreaker
```

---

## 4. Writing Your First Test

### VNextApiResponse Model

All `VNextApiClient` methods return a `VNextApiResponse` object that contains:

| Property | Type | Description |
|---|---|---|
| `StatusCode` | `HttpStatusCode` | HTTP status code (e.g. `HttpStatusCode.OK`) |
| `Headers` | `HttpResponseHeaders` | All response headers |
| `Body` | `JsonElement` | Deserialized JSON response body |
| `RawBody` | `string` | Raw response body string (useful for debugging) |
| `IsSuccessStatusCode` | `bool` | `true` when status code is 2xx |

```csharp
var response = await Api.StartInstanceAsync("my-workflow", payload);

// Access the JSON body
var id = response.Body.GetProperty("id").GetString();

// Check status code
Assert.Equal(HttpStatusCode.OK, response.StatusCode);

// Inspect response headers
var correlationId = response.Headers.GetValues("X-Correlation-Id").FirstOrDefault();
```

### Per-Request Headers

All API methods accept an optional `headers` parameter to send additional HTTP headers with a specific request without affecting the client's default headers:

```csharp
var customHeaders = new Dictionary<string, string>
{
    ["X-Correlation-Id"] = "test-correlation-123",
    ["X-Tenant-Id"]      = "tenant-abc"
};

var response = await Api.StartInstanceAsync("my-workflow", payload, headers: customHeaders);
```

### 4.1 Add Payloads to TestDataBuilder

Add factory methods to `Helpers/TestDataBuilder.cs` to represent your domain's workflows.

```csharp
// Helpers/TestDataBuilder.cs
public static class TestDataBuilder : TestDataBuilderBase
{
    // Payload for starting a new workflow instance
    public static object AppointmentRequest(string userId, string advisorId,
        string startDateTime, string endDateTime)
    {
        return BuildInstancePayload(
            key: DeterministicKey("appointment", userId, startDateTime),
            tags: ["appointment-request"],
            attributes: new { userId, advisorId, startDateTime, endDateTime }
        );
    }

    // Transition body for a cancel operation
    public static object CancelAppointment(string reason) =>
        BuildTransitionBody(new { reason });
}
```

**Helpers inherited from `TestDataBuilderBase`:**

| Helper | Description |
|---|---|
| `BuildInstancePayload(key, tags?, attributes?)` | Standard `{key, tags, attributes}` body |
| `BuildTransitionBody(attributes)` | Transition body wrapping `{attributes}` |
| `UniqueKey(prefix)` | Generates a unique `prefix-{guid}` key on every call |
| `DeterministicKey(prefix, parts[])` | Produces the same key for the same inputs |
| `BuildCustomWorkingHours(schedule)` | Converts a schedule dictionary to the vNext format |
| `DefaultWeekdaySchedule(...)` | Customisable default weekly schedule |

### 4.2 Create a Test Class

Add a new class under `Tests/`. Inherit from `IntegrationTestBase` and use the xUnit `[Fact]` attribute.

```csharp
// Tests/AppointmentTests.cs
using MorphFx.IntegrationTests.Helpers;
using MorphFx.IntegrationTests.Infrastructure;

namespace MorphFx.IntegrationTests.Tests;

public class AppointmentTests : IntegrationTestBase
{
    private const string Workflow = "appointment-request";

    public AppointmentTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task AppointmentRequest_Start_MovesToPendingState()
    {
        var payload = TestDataBuilder.AppointmentRequest(
            userId:        "user-001",
            advisorId:     "advisor-001",
            startDateTime: "2026-06-01T10:00:00Z",
            endDateTime:   "2026-06-01T10:30:00Z");

        var response = await Api.StartInstanceAsync(Workflow, payload);
        Assert.True(response.IsSuccessStatusCode);

        var id = response.Body.GetProperty("id").GetString()!;
        Assert.NotEmpty(id);

        var instance = await Api.GetInstanceAsync(Workflow, id);
        var state = GetCurrentState(instance.Body);
        Assert.Equal("pending", state);
    }

    [Fact]
    public async Task AppointmentRequest_Cancel_MovesCancelledState()
    {
        // Start an instance first
        var payload = TestDataBuilder.AppointmentRequest(
            "user-002", "advisor-001",
            "2026-06-02T09:00:00Z", "2026-06-02T09:30:00Z");

        var started = await Api.StartInstanceAsync(Workflow, payload);
        var id = started.Body.GetProperty("id").GetString()!;

        // Run the cancel transition
        await Api.RunTransitionAsync(Workflow, id, "cancel",
            TestDataBuilder.CancelAppointment("Test cancellation"));

        var instance = await Api.GetInstanceAsync(Workflow, id);
        Assert.Equal("cancelled", GetCurrentState(instance.Body));
    }
}
```

**Key points:**

- The test class constructor must accept a `VNextTestEnvironment environment` parameter — xUnit injects the collection fixture automatically.
- The `Api` object is provided by `IntegrationTestBase`; no extra declaration is needed.
- All API methods return a `VNextApiResponse` — access the JSON payload via `.Body` and the HTTP status via `.StatusCode`.
- `GetCurrentState(instance.Body)` handles both the flat `currentState` and the nested `metadata.currentState` response formats.
- Because all test classes inherit from `IntegrationTestBase` (which carries `[Collection("VNextIntegration")]`), they all share the same Docker stack — it is not restarted between tests.

### 4.3 Calling Domain Functions

To call a function directly without a workflow instance:

```csharp
// Domain-level function (scope I)
var slotsResponse = await Api.CallFunctionAsync("get-available-slots", new Dictionary<string, string>
{
    ["date"]      = "2026-06-01",
    ["advisorId"] = "advisor-001"
});
var slots = slotsResponse.Body;

// Workflow-level function (scope F)
var roomsResponse = await Api.CallWorkflowFunctionAsync(
    "chat-room", "get-chat-rooms",
    new Dictionary<string, string> { ["userId"] = "user-001" });
var rooms = roomsResponse.Body;
```

### 4.4 Instance Transitions and Retry

**Listing transitions for an instance:**

```csharp
var transitionsResponse = await Api.GetInstanceTransitionsAsync("my-workflow", instanceId);
var transitions = transitionsResponse.Body;
```

**Retrying a failed instance:**

```csharp
var retryResponse = await Api.RetryInstanceAsync("my-workflow", instanceId);
Assert.True(retryResponse.IsSuccessStatusCode);
```

---

## 5. Running Tests

### 5.1 Locally (Testcontainers)

With Docker Desktop running:

```bash
cd MorphFx.IntegrationTests
dotnet test
```

`VNextTestEnvironment` automatically starts:
1. Docker network
2. PostgreSQL, Redis, HashiCorp Vault (in parallel)
3. Vault secret seeding
4. **db-migrator** worker — runs schema migration, then exits automatically
5. Dapr placement + scheduler
6. vNext orchestrator + daprd sidecar
7. vNext execution + daprd sidecar
8. vNext inbox + daprd sidecar
9. vNext outbox + daprd sidecar
10. Mocklab + daprd sidecar *(skipped when `EnableMocklab` is `false`)*
11. Publishes domain definitions *(skipped when `EnableDomainPublish` is `false`)*

All long-running containers are cleaned up after the test run completes. The db-migrator exits on its own and is not included in the cleanup list.

The inbox and outbox pull the `{VNextImage}/inbox` and `{VNextImage}/outbox` images at the tag given by `VNextImageVersion`, so your registry must publish all four service images (`orchestrator`, `execution`, `inbox`, `outbox`) at that tag.

> On the first run, container images will be pulled and startup may take a few minutes. Subsequent runs are much faster because Docker caches the images.

### 5.2 Against an External Environment (CI / Shared Dev)

If a vNext environment is already running, skip container provisioning:

```bash
VNEXT_BASE_URL=http://vnext-orchestrator.staging.example.com dotnet test
```

In this mode:
- Docker containers are not started
- `LocalDomainPublisher` uploads domain definitions to the provided URL (unless `EnableDomainPublish` is `false`)
- Tests run directly against the external orchestrator

### 5.3 Configuring `test.runsettings`

Environment variables are declared in `test.runsettings` using the **variable name as the element name**:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <!-- Uncomment to run against an external environment
      <VNEXT_BASE_URL>http://localhost:5000</VNEXT_BASE_URL>
      -->
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

For local overrides, create a `test.runsettings.local` file (already listed in `.gitignore`):

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <VNEXT_BASE_URL>http://localhost:5000</VNEXT_BASE_URL>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

> xUnit v3 does not inject `<EnvironmentVariables>` into the test process, so the SDK parses the file itself. It looks for `test.runsettings.local` first, then `test.runsettings`, in the test output directory — and reads **element names**, not `name`/`value` attributes. A real process environment variable always wins over both files.

### 5.4 Running Specific Tests

```bash
# Run a specific class
dotnet test --filter "FullyQualifiedName~AppointmentTests"

# Run a single test
dotnet test --filter "DisplayName=AppointmentTests.AppointmentRequest_Start_MovesToPendingState"

# Run only smoke tests
dotnet test --filter "FullyQualifiedName~SmokeTests"
```

---

## 6. Extension Scenarios

All of the following scenarios are implemented by overriding SDK classes in the files generated by the template. The SDK package itself is not modified.

### 6.1 Adding Extra Vault Secrets

Some workflows may require domain-specific secrets in the test environment. Override `GetVaultSecrets` to add secrets on top of the SDK defaults:

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override Dictionary<string, Dictionary<string, string>> GetVaultSecrets()
{
    var secrets = base.GetVaultSecrets(); // preserve SDK defaults

    secrets["morphfx-secret"] = new()
    {
        ["PaymentApiKey"]    = "test-payment-key-001",
        ["SmsGatewayToken"]  = "test-sms-token"
    };

    return secrets;
}
```

### 6.2 Adding Container Environment Variables

To inject custom env vars into the orchestrator or execution container:

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override Dictionary<string, string> GetOrchestratorEnvironment()
{
    var env = base.GetOrchestratorEnvironment(); // preserve SDK defaults

    env["FEATURE_FLAG_NEW_SCHEDULER"]  = "true";
    env["MAX_CONCURRENT_WORKFLOWS"]    = "10";

    return env;
}

protected override Dictionary<string, string> GetExecutionEnvironment()
{
    var env = base.GetExecutionEnvironment();
    env["NOTIFICATION_RETRY_COUNT"] = "3";
    return env;
}
```

The inbox and outbox workers have the same hooks — `GetInboxEnvironment()` and `GetOutboxEnvironment()`:

```csharp
protected override Dictionary<string, string> GetInboxEnvironment()
{
    var env = base.GetInboxEnvironment();
    // Requires a component named vnext-pubsub-morphfx in Infrastructure/DaprComponents/inbox/
    env["DAPR_PUBSUB_STORE_NAME"] = "vnext-pubsub-morphfx";
    return env;
}
```

Each role's defaults set `APP_DOMAIN`, `DAPR_APP_ID`, the sidecar ports, and that role's Dapr store names:

| Variable | orchestration | execution | inbox | outbox | db-migrator |
|---|:-:|:-:|:-:|:-:|:-:|
| `DAPR_STATE_STORE_NAME` = `vnext-state` | ✔ | ✔ | ✔ | ✔ | — |
| `DAPR_SECRET_STORE_NAME` = `vnext-secret` | ✔ | ✔ | ✔ | ✔ | ✔ |
| `DAPR_LOCK_STORE_NAME` = `vnext-lock` | ✔ | ✔ | ✔ | ✔ | ✔ |
| `DAPR_PUBSUB_STORE_NAME` = `vnext-pubsub` | ✔ | ✔ | ✔ | ✔ | — |
| `DAPR_PUBSUB_BROADCAST_STORE_NAME` = `vnext-pubsub-broadcast` | ✔ | ✔ | ✔ | — | — |

The outbox has no broadcast store: it only drains the `sys_queues` outbox table to the regular pubsub, so `outbox/` ships no `pubsub-broadcast.yaml`.

> Every store name must match the `metadata.name` of a component YAML in that role's folder. Nothing validates this at startup — the sidecar comes up fine and the failure only surfaces when the app first reaches for the component. If you set a store name here, add or rename the matching YAML in the same commit.

### 6.3 Adding an Auth Header

If the test environment requires authentication, extend `VNextApiClient`:

```csharp
// Helpers/AuthenticatedVNextApiClient.cs
using VNext.Testing.Sdk.Client;

public class AuthenticatedVNextApiClient : VNextApiClient
{
    private readonly string _token;

    public AuthenticatedVNextApiClient(VNextApiClientOptions options, string token)
        : base(options)
    {
        _token = token;
    }

    protected override Task OnBeforeRequestAsync(HttpRequestMessage request)
    {
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return Task.CompletedTask;
    }
}
```

Then use it in `IntegrationTestBase`:

```csharp
// Infrastructure/IntegrationTestBase.cs
protected override VNextApiClient CreateApiClient(string baseUrl) =>
    new AuthenticatedVNextApiClient(
        new VNextApiClientOptions { BaseUrl = baseUrl, Domain = "morphfx" },
        token: System.Environment.GetEnvironmentVariable("TEST_API_TOKEN") ?? "dev-token"
    );
```

### 6.4 Using Custom Dapr Component YAMLs

If your domain has components with unique requirements (e.g. an extra HTTP binding, or a pubsub with a different consumer group):

1. Keep the `Infrastructure/DaprComponents/{role}/` folder for that role (do not delete it), and keep **all** of its files — the folder replaces the SDK defaults for that role wholesale.
2. Edit or add the YAML you need:

```yaml
# Infrastructure/DaprComponents/execution/notification-binding.yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: morphfx-notification-binding
spec:
  type: bindings.http
  version: v1
  metadata:
    - name: url
      value: http://MOCKLAB_HOST:5000/api/morphfx/notify
```

3. No `.csproj` change is needed — the template already copies the whole tree to the output directory, which is where the SDK reads local overrides from:

```xml
<Content Include="Infrastructure\DaprComponents\**\*.yaml">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

> Do not add `<EmbeddedResource>` items for these files. The SDK only ever scans its *own* assembly for embedded YAML, so embedding them in your test project has no effect — local overrides are always read from disk.

For a role you do **not** customise, delete its folder and let the SDK provide the defaults.

### 6.5 Customising the DB Migrator

The db-migrator worker is a run-to-completion container that starts after Vault seeding, applies schema migrations, and exits. The SDK waits for the log message `"Migration completed"` before proceeding.

**Pin a specific migrator image version:**

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override string DbMigratorImage => $"{VNextImage}/db-migrator:1.4.2";
```

**Override the wait strategy or startup flags:**

```csharp
protected override async Task RunDbMigratorAsync(string componentsDir, string settingsPath)
{
    // Example: wait for a different log message
    var builder = new ContainerBuilder()
        .WithImage(DbMigratorImage)
        .WithNetwork(_network)
        .WithBindMount(settingsPath, "/app/appsettings.Development.json")
        .WithBindMount(componentsDir, "/components")
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("Schema migration finished"));

    foreach (var (key, value) in GetDbMigratorEnvironment())
        builder = builder.WithEnvironment(key, value);

    await using var migrator = builder.Build();
    await migrator.StartAsync();
}
```

**Add domain-specific environment variables to the migrator:**

```csharp
protected override Dictionary<string, string> GetDbMigratorEnvironment()
{
    var env = base.GetDbMigratorEnvironment();
    env["MIGRATION_SEED_DATA"] = "true";
    return env;
}
```

**Skip the db-migrator entirely** (if your environment doesn't use one):

```csharp
protected override Task RunDbMigratorAsync(string componentsDir, string settingsPath)
    => Task.CompletedTask;
```

### 6.6 Mocklab (Built-in Mock HTTP Service)

Mocklab is started automatically after the outbox container and shares the Docker network, serving HTTP on port **5000** at the alias `test-mocklab`. Both the `MOCKLAB_HOST` placeholder (in `appsettings.*.json` *and* Dapr YAML) and the legacy `MOCKOON_HOST` placeholder (Dapr YAML) resolve to that alias.

**Seed data** — place your mock definition JSON files in `Infrastructure/MocklabSeed/`. They are bind-mounted into the container at `/app/seed` on startup.

**Pin a specific Mocklab version:**

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override string MocklabImage => "ghcr.io/burgan-tech/mocklab:1.2.0";
```

**Use a different seed directory:**

```csharp
protected override string MocklabSeedDirectory =>
    Path.Combine(Directory.GetCurrentDirectory(), "Config", "MockData");
```

**Point the placeholders at your own mock server:**

```csharp
protected override string MocklabHost => "my-shared-mock";
```

**Override the full startup behaviour** (e.g. different health check path):

```csharp
protected override async Task StartMocklabAsync()
{
    // custom ContainerBuilder configuration
    await base.StartMocklabAsync();
}
```

**Disable Mocklab entirely** (if your domain does not use it):

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override bool EnableMocklab => false;
```

When `EnableMocklab` is `false`, `StartMocklabAsync()` is not called and no Mocklab container is created.

### 6.7 Customising the Inbox and Outbox Workers

The inbox and outbox are ordinary vNext service containers, configured the same way as the orchestrator and execution services:

| What | How |
|---|---|
| Image tag | `VNextImageVersion` — shared by all four services |
| Registry / image path | `VNextImage` — the SDK appends `/inbox` and `/outbox` |
| Container env vars | `GetInboxEnvironment()` / `GetOutboxEnvironment()` |
| Dapr components | `Infrastructure/DaprComponents/inbox/` and `outbox/` |
| App configuration | `Config/appsettings.inbox.json` and `appsettings.outbox.json` |

**Pin the workers to a specific image tag** (they follow `VNextImageVersion`, so pin it for all services at once):

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override string VNextImageVersion => "1.4.2";
```

**Tune queue behaviour** in the config files rather than in code — `Aether.Inbox.*` and `Aether.Outbox.*` control batch size, lease duration, redelivery and cleanup. The defaults ship tuned for test runs (100-message batches, 100 ms busy polling), so a test that waits on asynchronous side effects usually needs no changes.

**A note on timing:** because both workers poll, side effects driven through the inbox/outbox are *eventually* consistent. Assert on them with a retry/poll loop rather than a single read immediately after a transition:

```csharp
// Poll instead of asserting once
var deadline = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < deadline)
{
    var instance = await Api.GetInstanceAsync(Workflow, id);
    if (GetCurrentState(instance.Body) == "completed") return;
    await Task.Delay(500);
}

Assert.Fail("Instance did not reach 'completed' within 30s");
```

### 6.8 Disabling Domain Publish

`LocalDomainPublisher` runs automatically after the environment is ready (both in local Testcontainers mode and against an external environment). If your tests supply definitions differently or don't need any, disable it:

```csharp
// Infrastructure/VNextTestEnvironment.cs
protected override bool EnableDomainPublish => false;
```

> Both `EnableMocklab` and `EnableDomainPublish` default to `true`, so existing projects are not affected.

### 6.9 Starting Additional Services (Custom Containers)

Use `OnAfterEnvironmentReadyAsync` to start any service that your tests depend on but that is not part of the standard vNext stack. This hook is called after the orchestrator, execution, inbox, outbox, Mocklab, and domain publish steps have all completed successfully.

**Example: starting a custom microservice container**

```csharp
// Infrastructure/VNextTestEnvironment.cs
private IContainer? _myService;

protected override async Task OnAfterEnvironmentReadyAsync()
{
    _myService = new ContainerBuilder()
        .WithImage("my-registry/my-service:latest")
        .WithNetwork(_network)
        .WithNetworkAliases("test-my-service")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)))
        .Build();

    await _myService.StartAsync();
    Console.WriteLine("[TestEnv] Custom service healthy");
}

public override async Task DisposeAsync()
{
    if (_myService != null)
    {
        try { await _myService.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[TestEnv] Dispose warning: {ex.Message}"); }
    }

    await base.DisposeAsync();
}
```

**Key points:**

- `_network` is accessible from subclasses because it is a `private` field — use `WithNetwork(_network)` only if `_network` is exposed. If not, start the container without a custom network or expose `_network` as `protected` in a local copy of the environment.
- Containers started in `OnAfterEnvironmentReadyAsync` are **not** tracked by the base class; you must dispose them yourself in `DisposeAsync`.
- Call `await base.DisposeAsync()` at the end of your override to ensure the standard stack is torn down.
- You can start multiple containers sequentially or in parallel with `Task.WhenAll`.

---

## Troubleshooting

| Problem | Likely Cause | Resolution |
|---|---|---|
| `vnext.config.json` not found | Config file is not at the repository root | Locate `vnext.config.json` by walking up from the test output directory; move it to the correct location |
| Container fails to start | Docker Desktop is not running | Verify with `docker ps` |
| `OrchestratorBaseUrl` is null | `InitializeAsync` was not called | Check the xUnit collection fixture configuration |
| Dapr sidecar does not start | YAML placeholder was accidentally edited | Ensure `REDIS_HOST` / `VAULT_HOST` / `MOCKLAB_HOST` are still present as-is in the YAML files |
| `appsettings.inbox.json` / `appsettings.outbox.json` not found | Project scaffolded from an older template version | All five `Config/appsettings.*.json` files are required. Copy the missing ones from the template and make sure they are set to *Copy to Output Directory* |
| Inbox or outbox container fails to pull | Registry does not publish `{VNextImage}/inbox` or `/outbox` at `VNextImageVersion` | Verify all four service images exist at that tag; pin `VNextImageVersion` to a tag that has them |
| Inbox/outbox healthy but nothing is processed | Dapr store name in the env vars doesn't match a component `metadata.name` in the role folder | Compare `DAPR_*_STORE_NAME` from `GetInboxEnvironment()` / `GetOutboxEnvironment()` against the YAML files in `Infrastructure/DaprComponents/inbox/` and `outbox/` |
| A component disappeared after customising a role folder | Role folders replace SDK defaults wholesale | Restore the full set of YAML files for that role, or delete the folder to fall back to SDK defaults |
| Assertion fails right after a transition, passes on rerun | Side effect travels through the polling inbox/outbox | Poll for the expected state instead of asserting once — see §6.7 |
| Domain setting still points at `localhost` | Placeholder missing or port omitted | Use `http://MOCKLAB_HOST:5000`; `MOCKLAB_HOST` substitutes the host only |
| Domain publish fails | Paths in `vnext.config.json` are incorrect | Check `componentsRoot` and subdirectory path values |
| db-migrator times out | Wait strategy log message doesn't match | Override `RunDbMigratorAsync` and adjust `UntilMessageIsLogged(...)` to match your migrator's actual log output |
| Mocklab fails to start | Health check path does not match | Override `StartMocklabAsync` and change `ForPath("/health")` to your Mocklab's actual health endpoint |
| Mocklab seed data not loaded | `MocklabSeedDirectory` path is incorrect | Verify that `Infrastructure/MocklabSeed/` exists in the test project output directory; override `MocklabSeedDirectory` if files are elsewhere |
| `POSTGRES_DB` not substituted | Using an old appsettings template | Ensure `appsettings.db-migrator.json` uses `POSTGRES_DB` (not a hardcoded database name) |
| Tests are slow | Container images are being pulled for the first time | Subsequent runs will be faster once the images are cached by Docker |
