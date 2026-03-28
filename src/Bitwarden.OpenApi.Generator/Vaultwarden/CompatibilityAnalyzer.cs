using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Writers;

namespace Bitwarden.OpenApi.Generator.Vaultwarden;

public record CompatibilityResult(
  string VaultwardenVersion,
  string BitwardenVersion,
  string SpecName,
  List<EndpointMatch> Matched,
  List<EndpointMiss> MissingInVaultwarden,
  List<EndpointMiss> ExtraInVaultwarden);

public record EndpointMatch(string Method, string Path, string VaultwardenFile);
public record EndpointMiss(string Method, string Path);

public static partial class CompatibilityAnalyzer
{
  [GeneratedRegex(@"\{[^}]+\}")]
  private static partial Regex OpenApiParamRegex();

  private static readonly Dictionary<string, OperationType> MethodToOperationType = new()
  {
    ["GET"] = OperationType.Get,
    ["POST"] = OperationType.Post,
    ["PUT"] = OperationType.Put,
    ["DELETE"] = OperationType.Delete,
    ["PATCH"] = OperationType.Patch,
    ["HEAD"] = OperationType.Head,
    ["OPTIONS"] = OperationType.Options,
    ["TRACE"] = OperationType.Trace,
  };

  public static CompatibilityResult Compare(
    string specPath,
    string specName,
    List<VaultwardenRoute> vaultwardenRoutes,
    string vaultwardenVersion,
    string bitwardenVersion)
  {
    using FileStream stream = File.OpenRead(specPath);
    OpenApiDocument doc = new OpenApiStreamReader().Read(stream, out OpenApiDiagnostic _);

    Dictionary<string, string> specEndpoints = new();
    foreach (var (path, pathItem) in doc.Paths)
    {
      foreach (OperationType opType in pathItem.Operations.Keys)
      {
        string method = opType.ToString().ToUpperInvariant();
        string key = NormalizeEndpoint(method, path);
        specEndpoints.TryAdd(key, path);
      }
    }

    Dictionary<string, VaultwardenRoute> vwEndpoints = new();
    foreach (VaultwardenRoute route in vaultwardenRoutes)
    {
      string key = NormalizeEndpoint(route.Method, route.Path);
      vwEndpoints.TryAdd(key, route);
    }

    List<EndpointMatch> matched = [];
    List<EndpointMiss> missingInVw = [];
    List<EndpointMiss> extraInVw = [];

    foreach (var (key, originalPath) in specEndpoints)
    {
      if (vwEndpoints.TryGetValue(key, out VaultwardenRoute? vwRoute))
        matched.Add(new EndpointMatch(vwRoute.Method, originalPath, vwRoute.File));
      else
        missingInVw.Add(ParseKey(key));
    }

    foreach (var (key, route) in vwEndpoints)
    {
      if (!specEndpoints.ContainsKey(key))
        extraInVw.Add(new EndpointMiss(route.Method, route.Path));
    }

    return new CompatibilityResult(
      vaultwardenVersion, bitwardenVersion, specName,
      matched.OrderBy(m => m.Path).ToList(),
      missingInVw.OrderBy(m => m.Path).ToList(),
      extraInVw.OrderBy(m => m.Path).ToList());
  }

  public static void GenerateFilteredSpec(
    string inputSpecPath,
    string outputSpecPath,
    CompatibilityResult result,
    string requestedVersion)
  {
    // Work on raw JSON for path/method filtering and schema pruning,
    // because Microsoft.OpenApi resolves $ref inline and loses reference info.
    JsonNode spec = JsonNode.Parse(File.ReadAllText(inputSpecPath))
      ?? throw new InvalidOperationException($"Failed to parse {inputSpecPath}");

    JsonObject paths = spec["paths"]!.AsObject();

    var matchedMethods = result.Matched
      .GroupBy(m => m.Path)
      .ToDictionary(g => g.Key, g => g.Select(m => m.Method.ToLowerInvariant()).ToHashSet());

    // Remove unmatched paths
    List<string> pathsToRemove = paths.Select(kv => kv.Key)
      .Where(p => !matchedMethods.ContainsKey(p))
      .ToList();
    foreach (string p in pathsToRemove)
      paths.Remove(p);

    // Remove unmatched methods within kept paths
    foreach (var (path, methods) in paths)
    {
      if (methods is not JsonObject methodsObj) continue;
      HashSet<string> allowed = matchedMethods.GetValueOrDefault(path, []);
      List<string> methodsToRemove = methodsObj.Select(m => m.Key)
        .Where(m => !allowed.Contains(m.ToLowerInvariant()))
        .ToList();
      foreach (string m in methodsToRemove)
        methodsObj.Remove(m);
    }

    // Update info
    string title = spec["info"]?["title"]?.GetValue<string>() ?? "";
    string shortName = title
      .Replace("Bitwarden ", "")
      .Replace("Internal API", "Vault")
      .Replace("API", "")
      .Trim();
    spec["info"]!["title"] = $"Vaultwarden {shortName} {requestedVersion} (API {result.BitwardenVersion})";
    spec["info"]!["version"] = requestedVersion;

    // Prune schemas using raw JSON $ref walking (accurate, no resolution issues)
    PruneUnusedSchemas(spec);

    Directory.CreateDirectory(Path.GetDirectoryName(outputSpecPath)!);
    JsonSerializerOptions options = new() { WriteIndented = true };
    File.WriteAllText(outputSpecPath, spec.ToJsonString(options));
  }

  public static void PrintReport(CompatibilityResult result)
  {
    int total = result.Matched.Count + result.MissingInVaultwarden.Count;
    double pct = total > 0 ? (double)result.Matched.Count / total * 100 : 0;

    Console.WriteLine($"  {result.SpecName}: {result.Matched.Count}/{total} endpoints ({pct:F0}% compatible)");

    if (result.MissingInVaultwarden.Count > 0)
    {
      Console.WriteLine($"    Missing in Vaultwarden ({result.MissingInVaultwarden.Count}):");
      foreach (EndpointMiss miss in result.MissingInVaultwarden.Take(10))
        Console.WriteLine($"      {miss.Method,-8} {miss.Path}");
      if (result.MissingInVaultwarden.Count > 10)
        Console.WriteLine($"      ... and {result.MissingInVaultwarden.Count - 10} more");
    }

    if (result.ExtraInVaultwarden.Count > 0)
    {
      Console.WriteLine($"    Extra in Vaultwarden ({result.ExtraInVaultwarden.Count}):");
      foreach (EndpointMiss extra in result.ExtraInVaultwarden.Take(10))
        Console.WriteLine($"      {extra.Method,-8} {extra.Path}");
      if (result.ExtraInVaultwarden.Count > 10)
        Console.WriteLine($"      ... and {result.ExtraInVaultwarden.Count - 10} more");
    }
  }

  private static string NormalizeEndpoint(string method, string path)
  {
    string normalized = OpenApiParamRegex().Replace(path.TrimEnd('/'), "{_}");
    return $"{method.ToUpperInvariant()} {normalized}";
  }

  private static EndpointMiss ParseKey(string key)
  {
    int space = key.IndexOf(' ');
    return new EndpointMiss(key[..space], key[(space + 1)..]);
  }

  /// <summary>
  /// Remove schemas not reachable from remaining paths.
  /// Uses raw JSON $ref walking — Microsoft.OpenApi resolves refs inline
  /// and loses reference identity, making it unreliable for reachability analysis.
  /// </summary>
  private static void PruneUnusedSchemas(JsonNode spec)
  {
    JsonObject? schemas = spec["components"]?["schemas"]?.AsObject();
    if (schemas == null) return;

    HashSet<string> reachable = [];
    CollectJsonRefs(spec["paths"], reachable);

    bool changed = true;
    while (changed)
    {
      changed = false;
      foreach (string schemaName in reachable.ToList())
      {
        JsonNode? schemaNode = schemas[schemaName];
        if (schemaNode == null) continue;
        int before = reachable.Count;
        CollectJsonRefs(schemaNode, reachable);
        if (reachable.Count > before)
          changed = true;
      }
    }

    List<string> toRemove = schemas.Select(kv => kv.Key)
      .Where(name => !reachable.Contains(name))
      .ToList();

    foreach (string name in toRemove)
      schemas.Remove(name);
  }

  private static void CollectJsonRefs(JsonNode? node, HashSet<string> refs)
  {
    switch (node)
    {
      case JsonObject obj:
        if (obj.TryGetPropertyValue("$ref", out JsonNode? refNode))
        {
          string? refValue = refNode?.GetValue<string>();
          if (refValue != null && refValue.StartsWith("#/components/schemas/"))
            refs.Add(refValue["#/components/schemas/".Length..]);
        }
        foreach (var (_, child) in obj)
          CollectJsonRefs(child, refs);
        break;

      case JsonArray arr:
        foreach (JsonNode? item in arr)
          CollectJsonRefs(item, refs);
        break;
    }
  }
}
