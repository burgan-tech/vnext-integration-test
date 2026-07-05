# Resilience.IntegrationTests

Container-based integration test suite for the **`feature/resilience-hardening`** branch of
[`burgan-tech/vnext`](https://github.com/burgan-tech/vnext). It spins up the full vNext stack
(PostgreSQL, Redis, Vault, Dapr placement/scheduler, db-migrator, orchestrator + execution with
Dapr sidecars, MockLab) via `VNext.Testing.Sdk` / Testcontainers, publishes a purpose-built
`resilience` domain, and exercises the branch's new behaviours end to end.

## What the suite validates

| Test class | Branch feature under test |
|---|---|
| `SmokeTests` | Stack boots, API healthy, domain published |
| `LifecycleTests` | Full transition pipeline incl. HTTP task via Execution + Dapr (path covered by the resiliency spec and the retry/timeout budget hierarchy) |
| `TransientFaultRetryTests` | Transient 503 from a task → clean instance fault (no transport-layer retry of app 5xx) → `/retry` drives re-run to success; durable-progress checkpoint means no stuck-Busy |
| `ChainLockTests` | Postgres lock lease store (`WorkflowExecution:LockProvider=Postgres`): exactly one winner under a concurrent transition storm, consistent settle state |
| `StateFunctionEtagTests` | Early-304 change-token fast path on the state function; ETag rotates after a transition |
| `InstanceDataTests` | Latest-only instance loading (`LatestOnlyInstanceLoading=true`): start attributes + task outputs stay visible across data versions |

Feature flags are enabled explicitly in `Config/appsettings.orchestration.json`
(`WorkflowExecution`: `LockProvider=Postgres`, `EnableLockLeaseExtension=true`,
`LatestOnlyInstanceLoading=true`). The Dapr resiliency spec is in
`Infrastructure/DaprComponents/orchestration/resiliency.yaml` with its target adjusted to this
stack's execution app-id (`vnext-execution-app-resilience`).

## Prerequisites

- Docker (daemon running, ~6 GB free for images)
- .NET 10 SDK (`10.0.2xx` band)
- A local clone of `burgan-tech/vnext` on `feature/resilience-hardening`

## Step 1 — Build branch images

The branch has no published images (CI only publishes on `release-v*` / manual dispatch), so
build them locally:

```bash
# from the vnext-integration-test repo root
./scripts/build-vnext-branch-images.sh ~/src/vnext resilience-local feature/resilience-hardening
```

This produces `ghcr.io/burgan-tech/vnext/{orchestrator,execution,db-migrator}:resilience-local`
— the default tag this suite uses.

> Alternative: trigger the `Build and Publish Docker Images` workflow in `burgan-tech/vnext`
> via *workflow_dispatch* on the branch with `prerelease_type=alpha` and a `version` input;
> then `export VNEXT_IMAGE_VERSION=<version>-alpha.<n>` instead of building locally.

## Step 2 — Run the suite

```bash
dotnet test tests/Resilience.IntegrationTests
```

Useful variants:

```bash
# pin a different image tag
VNEXT_IMAGE_VERSION=0.0.69-alpha.1 dotnet test tests/Resilience.IntegrationTests

# run against an already-running stack (skips Testcontainers provisioning)
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Resilience.IntegrationTests

# single class
dotnet test tests/Resilience.IntegrationTests --filter "FullyQualifiedName~ChainLockTests"
```

The first run pulls the infra images (postgres, redis, vault, daprio/*, mocklab) and takes a few
minutes; the stack is shared across all test classes through one xUnit collection fixture.

## Layout

```
Resilience.IntegrationTests/
├── vnext.config.json              # domain manifest — LocalDomainPublisher reads this
├── Domain/                        # the "resilience" domain published to the runtime
│   ├── Workflows/resilience-check/    # F-type flow: ready → reviewing → completed
│   │   └── src/*.csx                  # task mappings (also embedded NAT in the JSON)
│   ├── Tasks/ok-call.json             # HTTP task → MockLab, always 200
│   ├── Tasks/flaky-call.json          # HTTP task → MockLab, sequential 503/503/200
│   └── Schemas/resilience-check-master.json
├── Config/appsettings.*.json      # per-role settings incl. WorkflowExecution flags
├── Infrastructure/
│   ├── VNextTestEnvironment.cs    # domain + image tag overrides
│   ├── IntegrationTestBase.cs     # shared helpers (start, retry, poll, state function)
│   ├── DaprComponents/            # component overrides + orchestration/resiliency.yaml
│   └── MocklabSeed/resilience-collection.json
└── Tests/                         # the six test classes listed above
```

## Troubleshooting

- **`manifest unknown` on vnext images** — the branch images aren't built/tagged; rerun Step 1
  or set `VNEXT_IMAGE_VERSION` to an existing tag.
- **Flaky-step completes on first try** — MockLab's sequential counter for
  `api/resilience/check/flaky` persists per container run; the sequence repeats every 3 calls,
  so a previous test may have left it on the 200 slot. The retry test tolerates this but asserts
  at least one transient fault occurred; rerun with a fresh stack if it trips.
- **Ports/containers left behind after an aborted run** — Testcontainers' Ryuk reaper cleans up
  automatically; if you killed the process hard, `docker ps -a | grep vnext` and remove leftovers.
