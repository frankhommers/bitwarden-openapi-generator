using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace Bitwarden.OpenApi.Generator.Vaultwarden;

public record VaultwardenRoute(string Method, string Path, string File);

public record VaultwardenAnalysis(
  string Version,
  string BitwardenCompatVersion,
  List<VaultwardenRoute> ApiRoutes,
  List<VaultwardenRoute> IdentityRoutes,
  List<VaultwardenRoute> PublicRoutes,
  List<VaultwardenRoute> AdminRoutes,
  List<VaultwardenRoute> OtherRoutes);

/// <summary>
/// Parses Rocket route attributes from Vaultwarden source code.
/// </summary>
public static partial class RouteParser
{
  private const string RepoUrl = "https://github.com/dani-garcia/vaultwarden.git";

  // Matches: #[get("/path")] or #[post("/path", format = "...")] etc.
  [GeneratedRegex(@"#\[(get|post|put|delete|patch|head)\(""([^""]+)""")]
  private static partial Regex RouteAttributeRegex();

  // Matches: "version": "2025.12.0" in the config endpoint
  [GeneratedRegex(@"""version"":\s*""(\d{4}\.\d+\.\d+)""")]
  private static partial Regex VersionRegex();

  /// <summary>
  /// Clone Vaultwarden source and parse all routes.
  /// </summary>
  public static async Task<VaultwardenAnalysis> AnalyzeAsync(
    string version = "latest",
    string? existingPath = null,
    CancellationToken ct = default)
  {
    string sourcePath;
    bool shouldCleanup = false;

    if (existingPath != null)
    {
      sourcePath = existingPath;
    }
    else
    {
      sourcePath = Path.Combine(Path.GetTempPath(), $"vaultwarden-{version}");
      shouldCleanup = true;

      if (!Directory.Exists(sourcePath))
      {
        Console.WriteLine($"Cloning Vaultwarden {version}...");

        if (version == "latest")
        {
          await Cli.Wrap("git")
            .WithArguments(["clone", "--depth", "1", RepoUrl, sourcePath])
            .ExecuteAsync(ct);
        }
        else
        {
          // Clone full repo and checkout tag
          await Cli.Wrap("git")
            .WithArguments(["clone", RepoUrl, sourcePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
          await Cli.Wrap("git")
            .WithArguments(["-c", "advice.detachedHead=false", "checkout", version])
            .WithWorkingDirectory(sourcePath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

          // Verify the checkout worked
          string cargoPath = Path.Combine(sourcePath, "Cargo.toml");
          if (!File.Exists(cargoPath))
            throw new InvalidOperationException($"Failed to clone/checkout Vaultwarden {version}");
        }
      }
    }

    try
    {
      string apiDir = Path.Combine(sourcePath, "src", "api");
      if (!Directory.Exists(apiDir))
        throw new DirectoryNotFoundException($"Vaultwarden API source not found at {apiDir}");

      // Detect Vaultwarden version from Cargo.toml
      string vwVersion = DetectVersion(sourcePath);

      // Detect Bitwarden compat version from config endpoint
      string bwCompat = DetectBitwardenCompat(apiDir);

      Console.WriteLine($"  Vaultwarden version: {vwVersion}");
      Console.WriteLine($"  Bitwarden compat:    {bwCompat}");

      // Parse all routes
      List<VaultwardenRoute> allRoutes = ParseRoutes(apiDir);
      Console.WriteLine($"  Total routes parsed:  {allRoutes.Count}");

      // Categorize by mount prefix (from main.rs mount points)
      // /api       -> core/ (except public.rs) + accounts, ciphers, folders, etc.
      // /identity  -> identity.rs
      // /admin     -> admin.rs
      // /events    -> events.rs (mounted separately but maps to vault spec)
      List<VaultwardenRoute> identityRoutes = allRoutes.Where(r => r.File.Contains("identity.rs")).ToList();
      List<VaultwardenRoute> adminRoutes = allRoutes.Where(r => r.File.Contains("admin.rs")).ToList();
      List<VaultwardenRoute> publicRoutes = allRoutes.Where(r => r.File.Contains("public.rs")).ToList();
      List<VaultwardenRoute> otherRoutes = allRoutes.Where(r =>
        r.File.Contains("icons.rs") || r.File.Contains("web.rs") ||
        r.File.Contains("notifications.rs") || r.File.Contains("push.rs")).ToList();
      List<VaultwardenRoute> apiRoutes = allRoutes
        .Except(identityRoutes)
        .Except(adminRoutes)
        .Except(publicRoutes)
        .Except(otherRoutes)
        .ToList();

      return new VaultwardenAnalysis(
        vwVersion, bwCompat, apiRoutes, identityRoutes, publicRoutes, adminRoutes, otherRoutes);
    }
    finally
    {
      if (shouldCleanup && Directory.Exists(sourcePath))
        Directory.Delete(sourcePath, true);
    }
  }

  private static List<VaultwardenRoute> ParseRoutes(string apiDir)
  {
    List<VaultwardenRoute> routes = [];
    Regex regex = RouteAttributeRegex();

    foreach (string file in Directory.GetFiles(apiDir, "*.rs", SearchOption.AllDirectories))
    {
      string relativePath = Path.GetRelativePath(apiDir, file);
      string content = File.ReadAllText(file);

      foreach (Match match in regex.Matches(content))
      {
        string method = match.Groups[1].Value.ToUpperInvariant();
        string path = match.Groups[2].Value;

        // Strip query parameters (Rocket uses ?<param> syntax)
        int queryIdx = path.IndexOf('?');
        if (queryIdx >= 0)
          path = path[..queryIdx];

        // Convert Rocket params <param> to OpenAPI {param}
        path = path.Replace('<', '{').Replace('>', '}');

        // Remove Rocket catch-all syntax like {p..}
        path = path.Replace("{p..}", "");

        // Skip empty/root paths and static file routes
        if (string.IsNullOrWhiteSpace(path) || path == "/") continue;

        routes.Add(new VaultwardenRoute(method, path, relativePath));
      }
    }

    return routes.Distinct().ToList();
  }

  private static string DetectVersion(string sourcePath)
  {
    string cargoPath = Path.Combine(sourcePath, "Cargo.toml");
    if (!File.Exists(cargoPath)) return "unknown";

    // Only look at the [package] section for the version
    bool inPackageSection = false;
    foreach (string line in File.ReadLines(cargoPath))
    {
      string trimmed = line.Trim();
      if (trimmed == "[package]")
      {
        inPackageSection = true;
        continue;
      }

      if (trimmed.StartsWith('[') && inPackageSection) break; // next section

      if (inPackageSection && trimmed.StartsWith("version") && trimmed.Contains('"'))
      {
        int first = trimmed.IndexOf('"') + 1;
        int last = trimmed.IndexOf('"', first);
        if (last > first) return trimmed[first..last];
      }
    }

    return "unknown";
  }

  private static string DetectBitwardenCompat(string apiDir)
  {
    string modPath = Path.Combine(apiDir, "core", "mod.rs");
    if (!File.Exists(modPath)) return "unknown";

    string content = File.ReadAllText(modPath);
    Match match = VersionRegex().Match(content);
    return match.Success ? match.Groups[1].Value : "unknown";
  }
}