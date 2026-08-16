# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repository is

Two NuGet packages that let vNext domain teams write integration tests against a real,
containerised vNext stack:

| Project | Package | Purpose |
|---|---|---|
| `src/VNext.Testing.Sdk/` | `VNext.Testing.Sdk` | The library: HTTP client, Testcontainers stack, domain publisher, base classes |
| `src/VNext.Testing.Template/` | `VNext.Testing.Template` | `dotnet new vnext-integration-test` scaffold |
| `tests/MyDomain.IntegrationTests/` | — | Reference implementation and regression safeguard; not packed |

`tests/MyDomain.IntegrationTests/` references the SDK by `ProjectReference`, so SDK changes are
picked up without re-packing. It is *also* the source of the template's content — see the
sync rules below.

Deeper documentation: [README.md](./README.md) (platform team, SDK internals),
[GETTING_STARTED.md](./GETTING_STARTED.md) (domain teams consuming the packages).

## Commands

```bash
dotnet build ./VNext.Testing.slnx -c Release
```

```bash
dotnet pack ./src/VNext.Testing.Sdk/VNext.Testing.Sdk.csproj -c Release -o ./artifacts
```

Running the integration tests needs Docker and pulls the full vNext image set, so it is slow
and usually not what you want mid-change. Build first; only run tests when the change affects
container orchestration:

```bash
dotnet test ./tests/MyDomain.IntegrationTests/MyDomain.IntegrationTests.csproj
```

To run against an already-running environment instead of Testcontainers, set `VNEXT_BASE_URL`
(env var, or an element in `test.runsettings.local`).

## The three-way sync rule

The single most common way to break this repo is to change a resource in one place and not the
others. These trees must stay identical:

| Resource | Locations |
|---|---|
| Dapr component YAML | `src/VNext.Testing.Sdk/Resources/DaprComponents/{role}/`<br>`src/VNext.Testing.Template/content/VNextIntegrationTestTemplate/Infrastructure/DaprComponents/{role}/`<br>`tests/MyDomain.IntegrationTests/Infrastructure/DaprComponents/{role}/` |
| `appsettings.{role}.json` | `src/VNext.Testing.Template/content/VNextIntegrationTestTemplate/Config/`<br>`tests/MyDomain.IntegrationTests/Config/` |
| Test project scaffolding (`Infrastructure/`, `Helpers/`, `Tests/`, `.csproj`, `test.runsettings`) | template `content/` and `tests/MyDomain.IntegrationTests/` |

Roles: `orchestration`, `execution`, `inbox`, `outbox`, `db-migrator`.

Verify with `diff` before finishing, e.g.:

```bash
diff -r src/VNext.Testing.Template/content/VNextIntegrationTestTemplate/Config tests/MyDomain.IntegrationTests/Config
```

In template content, `MyDomain` / `mydomain` / `ghcr.io/burgan-tech/vnext` / `1.0.0-sdk` are
`dotnet new` substitution tokens (see `.template.config/template.json`). Do not "fix" them to
something more descriptive.

## Placeholders

Neither the YAML nor the JSON is templated by a real engine — `VNextTestEnvironment` does plain
string replacement at startup. Never hardcode a resolved value in a config file.

| Placeholder | Files | Resolves to |
|---|---|---|
| `APP_DOMAIN` | YAML | `Domain` property |
| `POSTGRES_HOST` | appsettings | `test-postgres` |
| `POSTGRES_DB` | appsettings | `DatabaseName` property |
| `REDIS_HOST` | appsettings + YAML | `test-redis` |
| `VAULT_HOST` | YAML | `test-vault` |
| `MOCKLAB_HOST` | appsettings + YAML | `MocklabHost` property → `test-mocklab` |
| `MOCKOON_HOST` | YAML | legacy alias for `MOCKLAB_HOST` |

Only the host is substituted — write the port yourself. Mocklab serves on `:5000`.

Because substitution is a bare `string.Replace`, a placeholder name must never be a substring of
real config content. Keep them SCREAMING_SNAKE and distinctive.

## SDK conventions

- Every extension point is `protected virtual`; every API operation is `public virtual`.
  A new configuration knob that domain teams might need is a `protected virtual` property or
  method, not a constructor argument or a field.
- Container fields added to `VNextTestEnvironment` **must** be added to the teardown array in
  `DisposeAsync`, sidecars before their apps. Forgetting this leaks containers between runs.
- Each vNext service follows the same shape: app container on port 5000 with a `/health` wait
  strategy, then a `daprd` sidecar sharing its network namespace via
  `HostConfig.NetworkMode = "container:{id}"`. Copy an existing `Start*Async` when adding a role
  and rename *all* of the copied locals and log lines.
- Dapr HTTP/gRPC port pairs are unique per role: orchestrator 42110/42111, execution 43110/43111,
  inbox 44110/44111, outbox 45110/45111, db-migrator 44210/44211.
- A `DAPR_*_STORE_NAME` env var in `Get{Role}Environment()` must match a `metadata.name` in that
  role's YAML folder. Nothing validates this at startup; it fails at runtime as an unresolved
  component.
- A `targets.apps` key in a `Resiliency` YAML is a Dapr **app-id**, not a service name, so it must
  carry the domain suffix — write `vnext-execution-app-APP_DOMAIN`, not `vnext-execution-app`.
  A key that matches no app is ignored silently, so cross-check every target against the callee's
  `DAPR_APP_ID` in `Get{Role}Environment()`.

## Embedded resources

Only the SDK embeds YAML, via the wildcard in `VNext.Testing.Sdk.csproj`:

```xml
<EmbeddedResource Include="Resources\DaprComponents\**\*.yaml">
  <LogicalName>%(RecursiveDir)%(Filename)%(Extension)</LogicalName>
</EmbeddedResource>
```

New YAML under `Resources/DaprComponents/` therefore needs no csproj change.
`ExtractEmbeddedDaprComponentsAsync` reads `typeof(VNextTestEnvironment).Assembly` — always the
SDK — and matches on a `{role}/` prefix. Consequences:

- Adding `<EmbeddedResource>` items to the template or test project does nothing. They reach
  their YAML from disk through `Content` + `CopyToOutputDirectory`.
- `%(RecursiveDir)` uses the build OS separator, so the SDK must be packed on Linux/macOS.
  CI runs on `ubuntu-latest`; keep it that way.

Local override resolution is **per role and all-or-nothing**: if
`Infrastructure/DaprComponents/{role}/` exists in the test project, only its files are used for
that role. There is no per-file merge with the SDK defaults.

## Release

`.github/workflows/publish-nuget.yml` runs on pushes to `release-v*`. It derives the version from
the branch name, stamps `common.props` and `template.json`, packs, publishes, and tags.

NuGet.org authentication uses **Trusted Publishing (OIDC)** — `NuGet/login@v1` exchanges the job's
GitHub OIDC token for a short-lived key. There is no `NUGET_API_KEY` secret. This depends on
`id-token: write` in the workflow permissions, a trusted-publishing policy on NuGet.org bound to
this repo and workflow, and `NUGET_USER` naming the NuGet.org account that owns the policy. The
workflow resolves it as `vars.NUGET_USER || secrets.NUGET_USER` — `vars` and `secrets` are
separate stores, and this repo has historically had it in the Secrets tab. Do not reintroduce an
API-key secret.

## Conventions for changes

- Match the surrounding style: XML doc comments on public/protected members, `[TestEnv]`-prefixed
  `Console.WriteLine` progress logs, section banner comments (`// ====== Name ======`).
- Docs are part of the change. A new virtual member or role belongs in README.md's reference
  tables; anything a domain team must do belongs in GETTING_STARTED.md.
- Build the solution before claiming a change works. Do not claim the Docker stack works unless
  you actually ran the integration tests.
