using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VNext.Testing.Sdk.Infrastructure;

/// <summary>
/// Publishes domain definitions from the local component directory directly to the
/// vNext Orchestrator API, bypassing the init container and npm registry.
/// </summary>
public static class LocalDomainPublisher
{
    private static readonly string[] ComponentTypes =
        ["schemas", "workflows", "tasks", "functions", "views", "extensions"];

    /// <param name="orchestratorBaseUrl">Base URL of the vNext orchestrator API.</param>
    /// <param name="appDomain">
    /// The APP_DOMAIN the orchestrator is configured with (e.g. "touch").
    /// All "domain" fields in component JSON will be replaced with this value
    /// before uploading, matching the init service's replaceDomainInJson behavior.
    /// </param>
    /// <param name="configFileName">
    /// Name of the vNext config file to look for when walking up the directory tree.
    /// Defaults to "vnext.config.json".
    /// </param>
    public static async Task PublishAsync(
        string orchestratorBaseUrl,
        string appDomain,
        string configFileName = "vnext.config.json")
    {
        var repoRoot = FindRepoRoot(configFileName);
        if (repoRoot is null)
        {
            Console.WriteLine($"[DomainPublisher] WARNING: '{configFileName}' not found by walking up from {AppContext.BaseDirectory}. Skipping domain publish.");
            return;
        }

        Console.WriteLine($"[DomainPublisher] Repo root: {repoRoot}");

        var config = ReadVNextConfig(repoRoot, configFileName);
        Console.WriteLine($"[DomainPublisher] Config — version: {config.Version}, domain: {config.Domain}, appDomain: {appDomain}");

        var componentsRoot = Path.Combine(repoRoot, config.ComponentsRoot);
        if (!Directory.Exists(componentsRoot))
            throw new DirectoryNotFoundException($"Components root not found: {componentsRoot}");

        var publishEndpoint = $"{orchestratorBaseUrl.TrimEnd('/')}/api/v1/definitions/publish";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        int success = 0, failed = 0;

        foreach (var componentType in ComponentTypes)
        {
            var dirName = config.Paths.GetValueOrDefault(componentType);
            if (string.IsNullOrEmpty(dirName)) continue;

            var dirPath = Path.Combine(componentsRoot, dirName);
            if (!Directory.Exists(dirPath))
            {
                Console.WriteLine($"[DomainPublisher] Skipping {componentType} — directory not found: {dirPath}");
                continue;
            }

            var jsonFiles = FindJsonFiles(dirPath);
            Console.WriteLine($"[DomainPublisher] {componentType}: {jsonFiles.Count} files");

            foreach (var file in jsonFiles)
            {
                var relativePath = Path.GetRelativePath(repoRoot, file);
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var node = JsonNode.Parse(json);
                    if (node is null)
                    {
                        Console.WriteLine($"[DomainPublisher] SKIP (null parse): {relativePath}");
                        continue;
                    }

                    ApplyVersionTransform(node, config.Version, config.Domain);
                    ReplaceDomain(node, appDomain);

                    var body = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(publishEndpoint, body);

                    if (response.IsSuccessStatusCode)
                    {
                        success++;
                    }
                    else
                    {
                        failed++;
                        var responseBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[DomainPublisher] FAIL {response.StatusCode}: {relativePath} — {responseBody[..Math.Min(responseBody.Length, 500)]}");
                    }

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[DomainPublisher] ERROR: {relativePath} — {ex.Message}");
                }
            }
        }

        Console.WriteLine($"[DomainPublisher] Upload complete — success: {success}, failed: {failed}");
    }

    // ========================================================================
    // Internal helpers
    // ========================================================================

    /// <summary>
    /// Walks up from the test assembly directory until the config file is found.
    /// Returns null if the file is not found anywhere in the directory tree.
    /// </summary>
    private static string? FindRepoRoot(string configFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, configFileName)))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static VNextConfig ReadVNextConfig(string repoRoot, string configFileName)
    {
        var configPath = Path.Combine(repoRoot, configFileName);
        var json = File.ReadAllText(configPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.GetProperty("version").GetString() ?? "1.0.0";
        var domain = root.GetProperty("domain").GetString() ?? "core";
        var componentsRoot = "core";
        var paths = new Dictionary<string, string>();

        if (root.TryGetProperty("paths", out var pathsEl))
        {
            if (pathsEl.TryGetProperty("componentsRoot", out var cr))
                componentsRoot = cr.GetString() ?? componentsRoot;

            foreach (var type in ComponentTypes)
            {
                if (pathsEl.TryGetProperty(type, out var val))
                    paths[type] = val.GetString() ?? "";
            }
        }

        return new VNextConfig(version, domain, componentsRoot, paths);
    }

    /// <summary>
    /// Recursively finds all .json files, excluding .meta/ directories and .diagram.json files.
    /// </summary>
    private static List<string> FindJsonFiles(string directory)
    {
        var results = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
            {
                if (Path.GetFileName(entry) == ".meta") continue;
                results.AddRange(FindJsonFiles(entry));
            }
            else if (entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     && !entry.EndsWith(".diagram.json", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(entry);
            }
        }
        return results;
    }

    /// <summary>
    /// Replicates init service's generateVersion:
    ///   artifactVersion → artifactVersion-pkg.packageVersion+domain
    /// Applied to root-level "version" and to each item in "data[]".
    /// </summary>
    private static void ApplyVersionTransform(JsonNode node, string packageVersion, string domain)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) ApplyVersionTransform(item, packageVersion, domain);
            return;
        }

        if (node is not JsonObject obj) return;

        if (obj["version"] is JsonValue versionVal
            && versionVal.TryGetValue(out string? currentVersion)
            && !string.IsNullOrEmpty(currentVersion))
        {
            obj["version"] = $"{currentVersion}-pkg.{packageVersion}+{domain}";
        }

        if (obj["data"] is JsonArray dataArr)
        {
            foreach (var item in dataArr)
                if (item is not null) ApplyVersionTransform(item, packageVersion, domain);
        }
    }

    /// <summary>
    /// Recursively replaces all "domain" string fields in the JSON tree.
    /// Mirrors init service's replaceDomainInJson — skips "config" and "process" subtrees.
    /// </summary>
    private static void ReplaceDomain(JsonNode node, string targetDomain)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) ReplaceDomain(item, targetDomain);
            return;
        }

        if (node is not JsonObject obj) return;

        if (obj["domain"] is JsonValue domainVal && domainVal.TryGetValue(out string? _))
            obj["domain"] = targetDomain;

        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            if (key is "domain" or "config" or "process") continue;
            if (obj[key] is JsonNode child) ReplaceDomain(child, targetDomain);
        }
    }

    private sealed record VNextConfig(
        string Version,
        string Domain,
        string ComponentsRoot,
        Dictionary<string, string> Paths);
}
