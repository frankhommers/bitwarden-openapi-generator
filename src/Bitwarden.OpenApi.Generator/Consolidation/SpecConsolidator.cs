using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Bitwarden.OpenApi.Generator.Consolidation;

public record VersionInfo(
  string Version,
  string SpecGroup,
  bool SameAsPrevious,
  int VaultPaths,
  Dictionary<string, string> Hashes);

public static class SpecConsolidator
{
  private static readonly string[] SpecFiles =
  [
    "bitwarden-vault.json",
    "bitwarden-public.json",
    "bitwarden-identity.json"
  ];

  /// <summary>
  /// Analyze all generated versions, remove duplicates (keep first of each group), write versions.json.
  /// </summary>
  public static List<VersionInfo> Consolidate(string specsDir)
  {
    List<string> versionDirs = Directory.GetDirectories(specsDir)
      .Select(Path.GetFileName)
      .Where(d => d != null && char.IsDigit(d[0]))
      .Cast<string>()
      .OrderBy(ParseVersionKey)
      .ToList();

    List<VersionInfo> versions = [];
    string? prevCombinedHash = null;
    string? groupStart = null;
    List<string> toDelete = [];

    foreach (string version in versionDirs)
    {
      string versionDir = Path.Combine(specsDir, version);
      Dictionary<string, string> hashes = [];

      foreach (string specFile in SpecFiles)
      {
        string path = Path.Combine(versionDir, specFile);
        hashes[specFile] = File.Exists(path) ? HashSpec(path) : "missing";
      }

      string combinedHash = string.Join("-", SpecFiles.Select(f => hashes.GetValueOrDefault(f, "missing")));

      if (combinedHash != prevCombinedHash)
      {
        groupStart = version;
        prevCombinedHash = combinedHash;
      }

      bool isDuplicate = version != groupStart;
      int vaultPaths = CountPaths(Path.Combine(versionDir, "bitwarden-vault.json"));
      versions.Add(new VersionInfo(version, groupStart!, isDuplicate, vaultPaths, hashes));

      if (isDuplicate)
        toDelete.Add(versionDir);
    }

    // Delete duplicate version directories (first of each group survives)
    foreach (string dir in toDelete)
    {
      Directory.Delete(dir, true);
      Console.WriteLine($"  Removed duplicate: {Path.GetFileName(dir)}");
    }

    // Write versions.json (includes all versions for reference, even deleted ones)
    string outputPath = Path.Combine(specsDir, "versions.json");
    JsonSerializerOptions options = new() { WriteIndented = true };
    File.WriteAllText(outputPath, JsonSerializer.Serialize(versions, options));

    return versions;
  }

  public static void PrintSummary(List<VersionInfo> versions)
  {
    var groups = versions.GroupBy(v => v.SpecGroup).ToList();

    Console.WriteLine($"  {versions.Count} versions -> {groups.Count} unique API surfaces");
    Console.WriteLine();

    foreach (var group in groups)
    {
      string leader = group.Key;
      int paths = group.First(v => v.Version == leader).VaultPaths;
      List<string> members = group.Select(v => v.Version).ToList();

      if (members.Count == 1)
        Console.WriteLine($"  {leader} ({paths} vault paths)");
      else
        Console.WriteLine($"  {leader} ({paths} vault paths) = {string.Join(", ", members)}");
    }
  }

  /// <summary>
  /// Hash a spec file ignoring the info block (which contains the version we set).
  /// </summary>
  private static string HashSpec(string path)
  {
    try
    {
      var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
      if (node is System.Text.Json.Nodes.JsonObject obj)
        obj.Remove("info");

      string content = node?.ToJsonString() ?? "";
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
      return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
    catch
    {
      return "error";
    }
  }

  private static int CountPaths(string path)
  {
    try
    {
      using FileStream stream = File.OpenRead(path);
      OpenApiDocument doc = new OpenApiStreamReader().Read(stream, out OpenApiDiagnostic _);
      return doc.Paths.Count;
    }
    catch
    {
      return 0;
    }
  }

  private static long ParseVersionKey(string version) =>
    version.Split('.').Aggregate(0L, (acc, part) => acc * 10000 + (long.TryParse(part, out long n) ? n : 0));
}
