using System.Xml.Linq;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using VNext.Testing.Sdk.Client;
using Xunit;

namespace VNext.Testing.Sdk.Infrastructure;

/// <summary>
/// Manages the full vNext runtime stack as Docker containers for integration testing.
/// Lifecycle: network -> infra (PG, Redis, Vault) -> Dapr (placement, scheduler) ->
/// vNext apps (orchestrator + dapr, execution + dapr) -> local domain publish.
///
/// Override the virtual methods to customise infrastructure (images, secrets, components, etc.)
/// without rewriting the entire orchestration logic.
/// </summary>
public class VNextTestEnvironment : IAsyncLifetime
{
    protected INetwork? _network;

    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;
    private IContainer? _vault;
    private IContainer? _daprPlacement;
    private IContainer? _daprScheduler;
    private IContainer? _vnextApp;
    private IContainer? _vnextAppDapr;
    private IContainer? _vnextExecution;
    private IContainer? _vnextExecutionDapr;
    private IContainer? _vnextInbox;
    private IContainer? _vnextInboxDapr;
    private IContainer? _vnextOutbox;
    private IContainer? _vnextOutboxDapr;
    private IContainer? _mocklab;
    private IContainer? _mocklabDapr;

    private string? _orchestrationUrl;
    private bool _useExternalEnvironment;

    public string OrchestratorBaseUrl => _orchestrationUrl
                                         ?? throw new InvalidOperationException(
                                             "Environment not initialized. Call InitializeAsync first.");

    // ========================================================================
    // Aliases (fixed, used internally for container networking)
    // ========================================================================

    private const string RedisAlias = "test-redis";
    private const string PostgresAlias = "test-postgres";
    private const string VaultAlias = "test-vault";
    private const string PlacementAlias = "test-dapr-placement";
    private const string SchedulerAlias = "test-dapr-scheduler";
    private const string OrchestratorAlias = "test-vnext-app";
    private const string ExecutionAlias = "test-vnext-execution";
    private const string MigratorAlias = "test-vnext-db-migrator";
    private const string InboxAlias = "test-vnext-inbox";
    private const string OutboxAlias = "test-vnext-outbox";
    private const string MocklabAlias = "test-mocklab";

    // ========================================================================
    // Virtual configuration properties — override in your test project
    // ========================================================================

    /// <summary>Domain name used in container env vars and domain publish step.</summary>
    protected virtual string Domain => "touch";

    /// <summary>PostgreSQL database name created for tests.</summary>
    protected virtual string DatabaseName => $"vNext_{Domain}_Test";

    /// <summary>Base image for vNext orchestrator and execution services (without the role suffix).</summary>
    protected virtual string VNextImage => "ghcr.io/burgan-tech/vnext";

    /// <summary>
    /// Image tag (version) for vNext orchestrator and execution containers.
    /// Override to pin a specific release, e.g. <c>"1.4.2"</c> or <c>"sha-abc1234"</c>.
    /// Defaults to <c>"latest"</c>.
    /// </summary>
    protected virtual string VNextImageVersion => "latest";

    /// <summary>PostgreSQL image tag.</summary>
    protected virtual string PostgresImage => "postgres:latest";

    /// <summary>Redis image tag.</summary>
    protected virtual string RedisImage => "redis:7.0-alpine";

    /// <summary>HashiCorp Vault image tag.</summary>
    protected virtual string VaultImage => "vault:1.13.3";

    /// <summary>Dapr placement image.</summary>
    protected virtual string DaprPlacementImage => "daprio/dapr:latest";

    /// <summary>Dapr scheduler image.</summary>
    protected virtual string DaprSchedulerImage => "daprio/scheduler:latest";

    /// <summary>Dapr sidecar (daprd) image.</summary>
    protected virtual string DaprSidecarImage => "daprio/daprd:latest";

    /// <summary>
    /// Full image reference for the db-migrator worker (including tag).
    /// Defaults to <c>{VNextImage}/db-migrator:{VNextImageVersion}</c>.
    /// Override to use a pinned version or a different registry path.
    /// </summary>
    protected virtual string DbMigratorImage => $"{VNextImage}/db-migrator:{VNextImageVersion}";
    
    /// <summary>Mocklab container image. Override to pin a specific version.</summary>
    protected virtual string MocklabImage => "ghcr.io/burgan-tech/mocklab:latest";

    /// <summary>
    /// Host-side directory bind-mounted to /app/seed inside the Mocklab container.
    /// Resolved relative to the test project's output directory.
    /// Default: <c>Infrastructure/MocklabSeed/</c>
    /// Override to point to a different seed data location.
    /// </summary>
    protected virtual string MocklabSeedDirectory =>
        Path.Combine(Directory.GetCurrentDirectory(), "Infrastructure", "MocklabSeed");

    /// <summary>
    /// Whether to start the Mocklab container and its Dapr sidecar.
    /// Set to <c>false</c> to skip Mocklab when your tests don't need a mock HTTP service.
    /// Default: <c>true</c>.
    /// </summary>
    protected virtual bool EnableMocklab => true;

    /// <summary>
    /// Host name substituted for the <c>MOCKLAB_HOST</c> placeholder in <c>appsettings.*.json</c>
    /// and for <c>MOCKLAB_HOST</c> / <c>MOCKOON_HOST</c> in Dapr component YAML.
    /// Defaults to the built-in Mocklab container's network alias (<c>test-mocklab</c>),
    /// which serves HTTP on port 5000 inside the test network.
    /// Override when your tests point at a differently-hosted mock server.
    /// </summary>
    protected virtual string MocklabHost => MocklabAlias;

    /// <summary>
    /// Whether to run LocalDomainPublisher after the environment is ready.
    /// Set to <c>false</c> to skip domain publish when your tests supply definitions
    /// differently or don't need any.
    /// Default: <c>true</c>.
    /// </summary>
    protected virtual bool EnableDomainPublish => true;

    // ========================================================================
    // IAsyncLifetime
    // ========================================================================

    public async Task InitializeAsync()
    {
        var externalUrl = ResolveEnvironmentVariable("VNEXT_BASE_URL");
        if (!string.IsNullOrEmpty(externalUrl))
        {
            Console.WriteLine($"[TestEnv] Using external vNext environment at {externalUrl}");
            _orchestrationUrl = externalUrl.TrimEnd('/');
            _useExternalEnvironment = true;

            if (EnableDomainPublish)
            {
                Console.WriteLine("[TestEnv] Publishing domain from local files...");
                await LocalDomainPublisher.PublishAsync(OrchestratorBaseUrl, Domain);
            }

            return;
        }

        _network = new NetworkBuilder()
            .WithName($"vnext-test-{Guid.NewGuid():N}")
            .Build();

        Console.WriteLine("[TestEnv] Creating Docker network...");
        await _network.CreateAsync();

        Console.WriteLine("[TestEnv] Starting infrastructure containers...");
        await StartInfrastructureAsync();

        Console.WriteLine("[TestEnv] Seeding Vault secrets...");
        await SeedVaultAsync();

        Console.WriteLine("[TestEnv] Creating test database...");
        await CreateDatabaseAsync();

        Console.WriteLine("[TestEnv] Preparing Dapr components...");
        var orchestrationComponentsDir = await PrepareDaprComponentsAsync("orchestration");
        var executionComponentsDir = await PrepareDaprComponentsAsync("execution");
        var migratorComponentsDir = await PrepareDaprComponentsAsync("db-migrator");
        var inboxComponentsDir = await PrepareDaprComponentsAsync("inbox");
        var outboxComponentsDir = await PrepareDaprComponentsAsync("outbox");

        Console.WriteLine("[TestEnv] Preparing appsettings...");
        var orchSettings = await PrepareAppSettingsAsync("appsettings.orchestration.json");
        var execSettings = await PrepareAppSettingsAsync("appsettings.execution.json");
        var migratorSettings = await PrepareAppSettingsAsync("appsettings.db-migrator.json");
        var inboxSettings = await PrepareAppSettingsAsync("appsettings.inbox.json");
        var outboxSettings = await PrepareAppSettingsAsync("appsettings.outbox.json");

        Console.WriteLine("[TestEnv] Starting Dapr infrastructure...");
        await StartDaprInfraAsync();

        Console.WriteLine("[TestEnv] Running db-migrator...");
        await RunDbMigratorAsync(migratorComponentsDir, migratorSettings);

        Console.WriteLine("[TestEnv] Starting vNext orchestrator...");
        await StartOrchestratorAsync(orchestrationComponentsDir, orchSettings);

        Console.WriteLine("[TestEnv] Starting vNext execution...");
        await StartExecutionAsync(executionComponentsDir, execSettings);
        
        Console.WriteLine("[TestEnv] Starting vNext inbox...");
        await StartInboxAsync(inboxComponentsDir, inboxSettings);
        
        Console.WriteLine("[TestEnv] Starting vNext outbox...");
        await StartOutboxAsync(outboxComponentsDir, outboxSettings);

        if (EnableMocklab)
        {
            Console.WriteLine("[TestEnv] Starting Mocklab...");
            await StartMocklabAsync();
        }

        if (EnableDomainPublish)
        {
            Console.WriteLine("[TestEnv] Publishing domain from local files...");
            await LocalDomainPublisher.PublishAsync(OrchestratorBaseUrl, Domain);
        }

        Console.WriteLine($"[TestEnv] Environment ready! API: {OrchestratorBaseUrl}");

        Console.WriteLine("[TestEnv] Running OnAfterEnvironmentReadyAsync...");
        await OnAfterEnvironmentReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_useExternalEnvironment)
        {
            Console.WriteLine("[TestEnv] External environment — nothing to dispose.");
            return;
        }

        Console.WriteLine("[TestEnv] Disposing containers...");
        var containers = new IContainer?[]
        {
            _mocklabDapr, _mocklab,
            _vnextOutboxDapr, _vnextOutbox,
            _vnextInboxDapr, _vnextInbox,
            _vnextExecutionDapr, _vnextExecution,
            _vnextAppDapr, _vnextApp,
            _daprScheduler, _daprPlacement,
            _vault
        };

        foreach (var c in containers)
        {
            if (c != null)
            {
                try
                {
                    await c.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TestEnv] Dispose warning: {ex.Message}");
                }
            }
        }

        if (_redis != null) await _redis.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();

        if (_network != null)
        {
            try
            {
                await _network.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestEnv] Network dispose warning: {ex.Message}");
            }
        }
    }

    // ========================================================================
    // Virtual hook: Infrastructure startup
    // ========================================================================

    /// <summary>
    /// Starts PostgreSQL, Redis and Vault containers in parallel.
    /// Override to add/remove infrastructure containers or change configuration.
    /// </summary>
    protected virtual async Task StartInfrastructureAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithName("vnext-postgres")
            .WithImage(PostgresImage)
            .WithNetwork(_network)
            .WithNetworkAliases(PostgresAlias)
            .WithDatabase(DatabaseName)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        _redis = new RedisBuilder()
            .WithName("vnext-redis")
            .WithImage(RedisImage)
            .WithNetwork(_network)
            .WithNetworkAliases(RedisAlias)
            .WithCommand("redis-server", "--protected-mode", "no")
            .Build();

        _vault = new ContainerBuilder()
            .WithName("vnext-vault")
            .WithImage(VaultImage)
            .WithNetwork(_network)
            .WithNetworkAliases(VaultAlias)
            .WithPortBinding(8200, true)
            .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID", "admin")
            .WithEnvironment("VAULT_TOKEN", "admin")
            .WithEnvironment("VAULT_ADDR", "http://0.0.0.0:8200")
            .WithCommand("server", "-dev", "-dev-root-token-id=admin")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8200))
            .Build();

        await Task.WhenAll(
            _postgres.StartAsync(),
            _redis.StartAsync(),
            _vault.StartAsync()
        );

        Console.WriteLine($"[TestEnv] PostgreSQL: {_postgres.GetConnectionString()}");
        Console.WriteLine($"[TestEnv] Redis running");
        Console.WriteLine($"[TestEnv] Vault: http://localhost:{_vault.GetMappedPublicPort(8200)}");
    }

    // ========================================================================
    // Virtual hook: Vault seeding
    // ========================================================================

    /// <summary>
    /// Seeds secrets into Vault after startup.
    /// Override to seed domain-specific secrets.
    /// </summary>
    protected virtual async Task SeedVaultAsync()
    {
        await Task.Delay(2000);

        var vaultPort = _vault!.GetMappedPublicPort(8200);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{vaultPort}") };
        http.DefaultRequestHeaders.Add("X-Vault-Token", "admin");

        var secrets = GetVaultSecrets();
        foreach (var (path, data) in secrets)
        {
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { data }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await http.PostAsync($"/v1/secret/data/{path}", content);
            Console.WriteLine($"[TestEnv] Vault seed '{path}': {response.StatusCode}");
        }
    }

    /// <summary>
    /// Returns the Vault secret paths and their key-value data to seed.
    /// Override to add domain-specific secrets.
    /// Default: seeds workflow-secret/ApiSecret used by the orchestrator.
    /// </summary>
    protected virtual Dictionary<string, Dictionary<string, string>> GetVaultSecrets()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["workflow-secret"] = new Dictionary<string, string>
            {
                ["ApiSecret"] = "TEST-SECRET-FOR-INTEGRATION"
            }
        };
    }

    // ========================================================================
    // Virtual hook: Database provisioning
    // ========================================================================

    /// <summary>
    /// Override to add more extensions, create extra databases, or seed initial data.
    /// </summary>
    protected virtual Task CreateDatabaseAsync() => Task.CompletedTask;

    // ========================================================================
    // Virtual hook: Dapr components
    // ========================================================================

    /// <summary>
    /// Prepares (copies and substitutes placeholders in) Dapr component YAML files
    /// for the given role ("orchestration" or "execution").
    ///
    /// Resolution order:
    ///   1. Project-local directory: Infrastructure/DaprComponents/{role}/
    ///   2. SDK embedded resources
    ///
    /// Override to implement a completely different strategy.
    /// </summary>
    protected virtual async Task<string> PrepareDaprComponentsAsync(string role)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dapr-{role}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Project-local override wins over SDK defaults
        var localDir = Path.Combine(Directory.GetCurrentDirectory(), "Infrastructure", "DaprComponents", role);
        if (Directory.Exists(localDir))
        {
            foreach (var file in Directory.GetFiles(localDir, "*.yaml"))
            {
                var content = await File.ReadAllTextAsync(file);
                content = ApplyDaprComponentSubstitutions(content, role);
                await File.WriteAllTextAsync(Path.Combine(tempDir, Path.GetFileName(file)), content);
            }

            Console.WriteLine($"[TestEnv] Using local Dapr components for '{role}' from {localDir}");
            return tempDir;
        }

        // Fall back to SDK embedded resources
        await ExtractEmbeddedDaprComponentsAsync(role, tempDir);
        Console.WriteLine($"[TestEnv] Using embedded SDK Dapr components for '{role}'");
        return tempDir;
    }

    /// <summary>
    /// Replaces well-known placeholders in Dapr component YAML.
    ///
    /// <para>
    /// <c>APP_DOMAIN</c> resolves to <see cref="Domain"/>. Dapr <c>Resiliency</c> policies address
    /// their targets by app-id, and every vNext app-id carries the domain as a suffix
    /// (<c>vnext-app-{domain}</c>, <c>vnext-execution-app-{domain}</c>, …), so a
    /// <c>targets.apps</c> key must be written as e.g. <c>vnext-execution-app-APP_DOMAIN</c>.
    /// A key without the suffix silently matches nothing.
    /// </para>
    ///
    /// Override to add domain-specific placeholder substitutions.
    /// </summary>
    protected virtual string ApplyDaprComponentSubstitutions(string content, string role)
    {
        return content
            .Replace("APP_DOMAIN", Domain)
            .Replace("REDIS_HOST", RedisAlias)
            .Replace("VAULT_HOST", VaultAlias)
            .Replace("MOCKLAB_HOST", GetMockoonAlias(role))
            .Replace("MOCKOON_HOST", GetMockoonAlias(role));
    }

    /// <summary>
    /// Returns the network alias for the mock HTTP server used by Dapr components of a role.
    /// Defaults to <see cref="MocklabHost"/> (the built-in Mocklab container alias).
    /// Override if a specific role should target a differently-configured mock server.
    /// </summary>
    protected virtual string GetMockoonAlias(string role) => MocklabHost;

    // ========================================================================
    // Virtual hook: appsettings
    // ========================================================================

    /// <summary>
    /// Prepares the appsettings JSON file for a given role, applying placeholder substitutions.
    /// Looks for the file under Config/{fileName} in the current directory.
    /// Override to supply settings from a different source.
    /// </summary>
    protected virtual async Task<string> PrepareAppSettingsAsync(string fileName)
    {
        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "Config", fileName);
        var content = await File.ReadAllTextAsync(sourcePath);
        content = ApplyAppSettingsSubstitutions(content);

        var tempPath = Path.Combine(Path.GetTempPath(), $"vnext-{Guid.NewGuid():N}-{fileName}");
        await File.WriteAllTextAsync(tempPath, content);

        Console.WriteLine($"[TestEnv] Prepared {fileName} at {tempPath}");
        return tempPath;
    }

    /// <summary>
    /// Replaces well-known placeholders in appsettings JSON.
    /// Override to add domain-specific substitutions.
    /// </summary>
    protected virtual string ApplyAppSettingsSubstitutions(string content)
    {
        return content
            .Replace("POSTGRES_HOST", PostgresAlias)
            .Replace("POSTGRES_DB", DatabaseName)
            .Replace("REDIS_HOST", RedisAlias)
            .Replace("MOCKLAB_HOST", MocklabHost);
    }

    // ========================================================================
    // Virtual hook: vNext environment variables
    // ========================================================================

    /// <summary>
    /// Returns the environment variables injected into the orchestrator container.
    /// Override to add or replace variables (e.g. custom Dapr store names).
    /// </summary>
    protected virtual Dictionary<string, string> GetOrchestratorEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["APP_DOMAIN"] = Domain,
            ["DAPR_APP_ID"] = $"vnext-app-{Domain}",
            ["DAPR_HTTP_PORT"] = "42110",
            ["DAPR_GRPC_PORT"] = "42111",
            ["DAPR_PLACEMENT_HOST"] = $"{PlacementAlias}:50005",
            ["DAPR_STATE_STORE_NAME"] = "vnext-state",
            ["DAPR_SECRET_STORE_NAME"] = "vnext-secret",
            ["DAPR_LOCK_STORE_NAME"] = "vnext-lock",
            ["DAPR_PUBSUB_STORE_NAME"] = "vnext-pubsub",
            ["DAPR_PUBSUB_BROADCAST_STORE_NAME"] = "vnext-pubsub-broadcast"
        };
    }

    /// <summary>
    /// Returns the environment variables injected into the execution container.
    /// Override to add or replace variables.
    /// </summary>
    protected virtual Dictionary<string, string> GetExecutionEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["APP_DOMAIN"] = Domain,
            ["DAPR_APP_ID"] = $"vnext-execution-app-{Domain}",
            ["DAPR_HTTP_PORT"] = "43110",
            ["DAPR_GRPC_PORT"] = "43111",
            ["DAPR_PLACEMENT_HOST"] = $"{PlacementAlias}:50005",
            ["DAPR_STATE_STORE_NAME"] = "vnext-state",
            ["DAPR_SECRET_STORE_NAME"] = "vnext-secret",
            ["DAPR_LOCK_STORE_NAME"] = "vnext-lock",
            ["DAPR_PUBSUB_STORE_NAME"] = "vnext-pubsub",
            ["DAPR_PUBSUB_BROADCAST_STORE_NAME"] = "vnext-pubsub-broadcast"
        };
    }

    // ========================================================================
    // Dapr infrastructure
    // ========================================================================

    private async Task StartDaprInfraAsync()
    {
        _daprPlacement = new ContainerBuilder()
            .WithName("dapr-placement")
            .WithImage(DaprPlacementImage)
            .WithNetwork(_network)
            .WithNetworkAliases(PlacementAlias)
            .WithPortBinding(50005, true)
            .WithCommand("./placement")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Placement service started on port"))
            .Build();

        _daprScheduler = new ContainerBuilder()
            .WithName("dapr-scheduler")
            .WithImage(DaprSchedulerImage)
            .WithNetwork(_network)
            .WithNetworkAliases(SchedulerAlias)
            .WithPortBinding(50007, true)
            .WithCommand("./scheduler", "--port", "50007", "--etcd-data-dir", "/tmp/scheduler-data")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Dapr Scheduler listening on"))
            .Build();

        await Task.WhenAll(
            _daprPlacement.StartAsync(),
            _daprScheduler.StartAsync()
        );

        Console.WriteLine($"[TestEnv] Dapr placement + scheduler started.");
    }
    
    /// <summary>
    /// Returns the environment variables injected into the inbox container.
    /// Override to add or replace variables (e.g. custom Dapr store names).
    /// </summary>
    protected virtual Dictionary<string, string> GetInboxEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["APP_DOMAIN"] = Domain,
            ["DAPR_APP_ID"] = $"vnext-inbox-{Domain}",
            ["DAPR_HTTP_PORT"] = "44110",
            ["DAPR_GRPC_PORT"] = "44111",
            ["DAPR_PLACEMENT_HOST"] = $"{PlacementAlias}:50005",
            ["DAPR_STATE_STORE_NAME"] = "vnext-state",
            ["DAPR_SECRET_STORE_NAME"] = "vnext-secret",
            ["DAPR_LOCK_STORE_NAME"] = "vnext-lock",
            ["DAPR_PUBSUB_STORE_NAME"] = "vnext-pubsub",
            ["DAPR_PUBSUB_BROADCAST_STORE_NAME"] = "vnext-pubsub-broadcast"
        };
    }
    
    /// <summary>
    /// Returns the environment variables injected into the outbox container.
    /// Override to add or replace variables (e.g. custom Dapr store names).
    ///
    /// <para>
    /// Unlike the other roles this sets no <c>DAPR_PUBSUB_BROADCAST_STORE_NAME</c>: the outbox
    /// only drains the <c>sys_queues</c> outbox table to the regular pubsub, so the
    /// <c>outbox/</c> component folder ships no <c>pubsub-broadcast.yaml</c>. Adding the variable
    /// back without also adding the component would name a component that does not exist.
    /// </para>
    /// </summary>
    protected virtual Dictionary<string, string> GetOutboxEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["APP_DOMAIN"] = Domain,
            ["DAPR_APP_ID"] = $"vnext-outbox-{Domain}",
            ["DAPR_HTTP_PORT"] = "45110",
            ["DAPR_GRPC_PORT"] = "45111",
            ["DAPR_PLACEMENT_HOST"] = $"{PlacementAlias}:50005",
            ["DAPR_STATE_STORE_NAME"] = "vnext-state",
            ["DAPR_SECRET_STORE_NAME"] = "vnext-secret",
            ["DAPR_LOCK_STORE_NAME"] = "vnext-lock",
            ["DAPR_PUBSUB_STORE_NAME"] = "vnext-pubsub"
        };
    }

    // ========================================================================
    // vNext Orchestrator
    // ========================================================================

    private async Task StartOrchestratorAsync(string componentsDir, string settingsPath)
    {
        var orchBuilder = new ContainerBuilder()
            .WithName("vnext-app")
            .WithImage($"{VNextImage}/orchestrator:{VNextImageVersion}")
            .WithNetwork(_network)
            .WithNetworkAliases(OrchestratorAlias)
            .WithPortBinding(5000, true)
            .WithBindMount(settingsPath, "/app/appsettings.Development.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(5000)));

        foreach (var (key, value) in GetOrchestratorEnvironment())
            orchBuilder = orchBuilder.WithEnvironment(key, value);

        _vnextApp = orchBuilder.Build();
        await _vnextApp.StartAsync();

        _orchestrationUrl = $"http://localhost:{_vnextApp.GetMappedPublicPort(5000)}";
        Console.WriteLine($"[TestEnv] Orchestrator healthy at {_orchestrationUrl}");

        _vnextAppDapr = new ContainerBuilder()
            .WithName("vnext-app-dapr")
            .WithImage(DaprSidecarImage)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{_vnextApp.Id}";
            })
            .WithBindMount(componentsDir, "/components")
            .DependsOn(_vnextApp)
            .WithCommand(
                "./daprd",
                "--app-id", $"vnext-app-{Domain}",
                "--app-port", "5000",
                "--resources-path", "/components",
                "--dapr-grpc-port", "42111",
                "--dapr-http-port", "42110",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "warn")
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await _vnextAppDapr.StartAsync();
        Console.WriteLine("[TestEnv] Orchestrator Dapr sidecar started");
    }

    // ========================================================================
    // vNext Execution
    // ========================================================================

    private async Task StartExecutionAsync(string componentsDir, string settingsPath)
    {
        var execBuilder = new ContainerBuilder()
            .WithName("vnext-execution")
            .WithImage($"{VNextImage}/execution:{VNextImageVersion}")
            .WithNetwork(_network)
            .WithNetworkAliases(ExecutionAlias)
            .WithPortBinding(5000, true)
            .WithBindMount(settingsPath, "/app/appsettings.Development.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(5000)));

        foreach (var (key, value) in GetExecutionEnvironment())
            execBuilder = execBuilder.WithEnvironment(key, value);

        _vnextExecution = execBuilder.Build();
        await _vnextExecution.StartAsync();
        Console.WriteLine("[TestEnv] Execution healthy");

        _vnextExecutionDapr = new ContainerBuilder()
            .WithName("vnext-execution-dapr")
            .WithImage(DaprSidecarImage)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{_vnextExecution.Id}";
            })
            .WithBindMount(componentsDir, "/components")
            .WithCommand(
                "./daprd",
                "--app-id", $"vnext-execution-app-{Domain}",
                "--app-port", "5000",
                "--resources-path", "/components",
                "--dapr-grpc-port", "43111",
                "--dapr-http-port", "43110",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "warn")
            .DependsOn(_vnextExecution)
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await _vnextExecutionDapr.StartAsync();
        Console.WriteLine("[TestEnv] Execution Dapr sidecar started");
    }

    // ========================================================================
    // vNext Inbox
    // ========================================================================

    private async Task StartInboxAsync(string componentsDir, string settingsPath)
    {
        var inboxBuilder = new ContainerBuilder()
            .WithName("vnext-inbox")
            .WithImage($"{VNextImage}/inbox:{VNextImageVersion}")
            .WithNetwork(_network)
            .WithNetworkAliases(InboxAlias)
            .WithPortBinding(5000, true)
            .WithBindMount(settingsPath, "/app/appsettings.Development.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(5000)));

        foreach (var (key, value) in GetInboxEnvironment())
            inboxBuilder = inboxBuilder.WithEnvironment(key, value);

        _vnextInbox = inboxBuilder.Build();
        await _vnextInbox.StartAsync();
        Console.WriteLine("[TestEnv] Inbox healthy");

        _vnextInboxDapr = new ContainerBuilder()
            .WithName("vnext-inbox-dapr")
            .WithImage(DaprSidecarImage)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{_vnextInbox.Id}";
            })
            .WithBindMount(componentsDir, "/components")
            .WithCommand(
                "./daprd",
                "--app-id", $"vnext-inbox-{Domain}",
                "--app-port", "5000",
                "--resources-path", "/components",
                "--dapr-grpc-port", "44111",
                "--dapr-http-port", "44110",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "warn")
            .DependsOn(_vnextInbox)
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await _vnextInboxDapr.StartAsync();
        Console.WriteLine("[TestEnv] Inbox Dapr sidecar started");
    }
    
    // ========================================================================
    // vNext Outbox
    // ========================================================================

    private async Task StartOutboxAsync(string componentsDir, string settingsPath)
    {
        var outboxBuilder = new ContainerBuilder()
            .WithName("vnext-outbox")
            .WithImage($"{VNextImage}/outbox:{VNextImageVersion}")
            .WithNetwork(_network)
            .WithNetworkAliases(OutboxAlias)
            .WithPortBinding(5000, true)
            .WithBindMount(settingsPath, "/app/appsettings.Development.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(5000)));

        foreach (var (key, value) in GetOutboxEnvironment())
            outboxBuilder = outboxBuilder.WithEnvironment(key, value);

        _vnextOutbox = outboxBuilder.Build();
        await _vnextOutbox.StartAsync();
        Console.WriteLine("[TestEnv] Outbox healthy");

        _vnextOutboxDapr = new ContainerBuilder()
            .WithName("vnext-outbox-dapr")
            .WithImage(DaprSidecarImage)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{_vnextOutbox.Id}";
            })
            .WithBindMount(componentsDir, "/components")
            .WithCommand(
                "./daprd",
                "--app-id", $"vnext-outbox-{Domain}",
                "--app-port", "5000",
                "--resources-path", "/components",
                "--dapr-grpc-port", "45111",
                "--dapr-http-port", "45110",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "warn")
            .DependsOn(_vnextOutbox)
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await _vnextOutboxDapr.StartAsync();
        Console.WriteLine("[TestEnv] Outbox Dapr sidecar started");
    }
    
    // ========================================================================
    // Mocklab (mock HTTP service + Dapr sidecar — long-lived, disposed with the stack)
    // ========================================================================

    /// <summary>
    /// Starts the Mocklab mock service and its Dapr sidecar.
    /// Seed files are bind-mounted from <see cref="MocklabSeedDirectory"/> into the container.
    /// Override to change startup behaviour, health check path, or sidecar flags.
    /// </summary>
    protected virtual async Task StartMocklabAsync()
    {
        var seedDir = MocklabSeedDirectory;
        if (!Directory.Exists(seedDir))
        {
            Directory.CreateDirectory(seedDir);
            Console.WriteLine($"[TestEnv] Mocklab seed directory created at {seedDir}");
        }

        _mocklab = new ContainerBuilder()
            .WithName("mocklab")
            .WithImage(MocklabImage)
            .WithNetwork(_network)
            .WithNetworkAliases(MocklabAlias)
            .WithPortBinding(5000, true)
            .WithBindMount(seedDir, "/app/seed")
            .WithEnvironment("Mocklab__SeedDirectory", "/app/seed")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(5000)))
            .Build();

        await _mocklab.StartAsync();
        Console.WriteLine("[TestEnv] Mocklab healthy");

        _mocklabDapr = new ContainerBuilder()
            .WithName("mocklab-dapr")
            .WithImage(DaprSidecarImage)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{_mocklab.Id}";
            })
            .WithCommand(
                "./daprd",
                "--app-id", "mocklab",
                "--app-port", "5000",
                "--resources-path", "/components",
                "--dapr-http-port", "3500",
                "--dapr-grpc-port", "50001",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "warn")
            .DependsOn(_mocklab)
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await _mocklabDapr.StartAsync();
        Console.WriteLine("[TestEnv] Mocklab Dapr sidecar started");
    }

    // ========================================================================
    // Extension point — runs after the full stack is ready
    // ========================================================================

    /// <summary>
    /// Called after the full vNext stack (infra, db-migrator, Dapr, orchestrator,
    /// execution, Mocklab, and domain publish) is up and healthy.
    ///
    /// Override in your test project to start additional services — for example a custom
    /// microservice, a second mock server, a message broker, or any other container
    /// your integration tests depend on.
    ///
    /// <para>
    /// Containers started here are not tracked by the base class.
    /// You are responsible for disposing them by overriding <see cref="DisposeAsync"/>.
    /// </para>
    ///
    /// Default implementation does nothing.
    /// </summary>
    protected virtual Task OnAfterEnvironmentReadyAsync() => Task.CompletedTask;

    // ========================================================================
    // db-migrator (run-to-completion worker)
    // ========================================================================

    /// <summary>
    /// Returns the environment variables injected into the db-migrator container.
    /// Override to add or replace variables.
    /// </summary>
    protected virtual Dictionary<string, string> GetDbMigratorEnvironment()
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["APP_DOMAIN"] = Domain,
            ["DAPR_APP_ID"] = $"vnext-db-migrator-{Domain}",
            ["DAPR_HTTP_PORT"] = "44210",
            ["DAPR_GRPC_PORT"] = "44211",
            ["DAPR_PLACEMENT_HOST"] = $"{PlacementAlias}:50005",
            ["DAPR_SECRET_STORE_NAME"] = "vnext-secret",
            ["DAPR_LOCK_STORE_NAME"] = "vnext-lock"
        };
    }

    /// <summary>
    /// Starts the db-migrator worker (a <c>Microsoft.NET.Sdk.Worker</c> — no HTTP port, runs to
    /// completion) together with its Dapr sidecar, waits for the migration to finish, then disposes
    /// both containers.
    ///
    /// <para>
    /// Mirrors the docker-compose pattern where the sidecar uses
    /// <c>network_mode: "service:vnext-db-migrator"</c> to share the app container's network
    /// namespace. In Testcontainers this is achieved by starting the migrator first (without a
    /// wait strategy), then starting the sidecar with
    /// <c>NetworkMode = "container:&lt;migrator-id&gt;"</c>. Both containers then share
    /// <c>localhost</c>, so <c>DAPR_HTTP_PORT</c> / <c>DAPR_GRPC_PORT</c> work as-is.
    /// </para>
    ///
    /// Override to customise startup flags, ports, or sidecar configuration.
    /// </summary>
    protected virtual async Task RunDbMigratorAsync(string componentsDir, string settingsPath)
    {
        // 1. Start the migrator container first — we need its container ID to attach the sidecar.
        //    No wait strategy: the app will be ready to accept Dapr as soon as the process starts.
        var migratorBuilder = new ContainerBuilder()
            .WithName("vnext-db-migrator")
            .WithImage(DbMigratorImage)
            .WithNetwork(_network)
            .WithNetworkAliases(MigratorAlias)
            .WithBindMount(settingsPath, "/app/appsettings.json")
            .WithWaitStrategy(Wait.ForUnixContainer());

        foreach (var (key, value) in GetDbMigratorEnvironment())
            migratorBuilder = migratorBuilder.WithEnvironment(key, value);

        await using var migrator = migratorBuilder.Build();

        // Register before StartAsync — GetExitCodeAsync blocks until the container process exits.
        Task<long>? exitCodeTask = null;
        migrator.Starting += (_, _) => { exitCodeTask = migrator.GetExitCodeAsync(); };

        try
        {
            await migrator.StartAsync();
        }
        catch (Exception ex) when (ex.Message.Contains("not running") || ex.Message.Contains("exited"))
        {
            Console.WriteLine("[TestEnv] db-migrator exited during startup check (expected for workers).");
        }

        Console.WriteLine("[TestEnv] db-migrator container started");

        // 2. Start the Dapr sidecar sharing the migrator's network namespace —
        //    equivalent to docker-compose `network_mode: "service:vnext-db-migrator"`.
        //    The sidecar and app now share localhost, so DAPR_HTTP_PORT/DAPR_GRPC_PORT work as-is.
        await using var migratorDapr = new ContainerBuilder()
            .WithName("vnext-db-migrator-dapr")
            .WithImage(DaprSidecarImage)
            .WithBindMount(componentsDir, "/components")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.NetworkMode = $"container:{migrator.Id}";
            })
            .DependsOn(migrator)
            .WithCommand(
                "./daprd",
                "--app-id", $"vnext-db-migrator-{Domain}",
                "--resources-path", "/components",
                "--dapr-http-port", "44210",
                "--dapr-grpc-port", "44211",
                "--scheduler-host-address", $"{SchedulerAlias}:50007",
                "--placement-host-address", $"{PlacementAlias}:50005",
                "--log-level", "info")
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await migratorDapr.StartAsync();
        Console.WriteLine("[TestEnv] db-migrator Dapr sidecar ready");
        Console.WriteLine("[TestEnv] Waiting for db-migrator to finish...");

        // 3. Wait for exit and verify success.
        var exitCode = await (exitCodeTask ?? migrator.GetExitCodeAsync());
        if (exitCode != 0)
        {
            var (stdout, stderr) = await migrator.GetLogsAsync();
            Console.WriteLine($"[TestEnv] db-migrator STDOUT:\n{stdout}");
            Console.WriteLine($"[TestEnv] db-migrator STDERR:\n{stderr}");
            throw new InvalidOperationException(
                $"[TestEnv] db-migrator exited with code {exitCode}.");
        }
        else
        {
            var (stdout, stderr) = await migrator.GetLogsAsync();
            Console.WriteLine($"[TestEnv] db-migrator STDOUT:\n{stdout}");
            Console.WriteLine($"[TestEnv] db-migrator STDERR:\n{stderr}");
        }

        Console.WriteLine("[TestEnv] db-migrator completed, removing containers.");
    }

    // ========================================================================
    // Embedded resource extraction
    // ========================================================================

    private static async Task ExtractEmbeddedDaprComponentsAsync(string role, string targetDir)
    {
        var assembly = typeof(VNextTestEnvironment).Assembly;
        var prefix = $"{role}/";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(name);
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            await File.WriteAllTextAsync(Path.Combine(targetDir, fileName), content);
        }
    }

    // ========================================================================
    // .runsettings fallback — xUnit v3 does not inject <EnvironmentVariables>
    // from .runsettings into the process, so we parse the file ourselves.
    // ========================================================================

    private static Dictionary<string, string>? _runSettingsCache;

    /// <summary>
    /// Reads an environment variable by name. Falls back to values defined in the
    /// nearest <c>test.runsettings.local</c> or <c>test.runsettings</c> file when
    /// the variable is not present in the actual process environment.
    /// </summary>
    private static string? ResolveEnvironmentVariable(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            return value;

        _runSettingsCache ??= LoadRunSettingsVariables();
        _runSettingsCache.TryGetValue(name, out var fallback);
        return fallback;
    }

    private static Dictionary<string, string> LoadRunSettingsVariables()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var baseDir = Directory.GetCurrentDirectory();
        var candidates = new[] { "test.runsettings.local", "test.runsettings" };

        string? filePath = null;
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(baseDir, candidate);
            if (File.Exists(path))
            {
                filePath = path;
                break;
            }
        }

        if (filePath == null)
        {
            Console.WriteLine("[TestEnv] No .runsettings file found — relying on process environment only.");
            return result;
        }

        try
        {
            var doc = XDocument.Load(filePath);
            var envVars = doc.Descendants("EnvironmentVariables").FirstOrDefault();
            if (envVars == null)
            {
                Console.WriteLine($"[TestEnv] .runsettings loaded from {filePath} but no <EnvironmentVariables> section found.");
                return result;
            }

            foreach (var element in envVars.Elements())
            {
                var varName = element.Name.LocalName;
                var varValue = element.Value;
                if (!string.IsNullOrEmpty(varValue))
                    result[varName] = varValue;
            }

            Console.WriteLine($"[TestEnv] Loaded {result.Count} variable(s) from {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestEnv] Warning: failed to parse {filePath}: {ex.Message}");
        }

        return result;
    }
}