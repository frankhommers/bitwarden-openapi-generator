using Microsoft.OpenApi.Models;

namespace Bitwarden.OpenApi.Generator.Patching;

public static class VaultPatches
{
  public static int MergeDuplicatePaths(OpenApiDocument doc)
  {
    var groups = doc.Paths
      .Select(kv => (Key: kv.Key, Normalized: NormalizePath(kv.Key), Item: kv.Value))
      .GroupBy(x => x.Normalized)
      .Where(g => g.Count() > 1)
      .ToList();

    int count = 0;
    foreach (var group in groups)
    {
      string canonical = group.OrderBy(x => x.Key.Length).First().Key;
      OpenApiPathItem canonicalItem = doc.Paths[canonical];
      List<string> canonicalParams = ExtractParams(canonical);

      foreach (var (key, _, item) in group.Where(x => x.Key != canonical))
      {
        List<string> dupeParams = ExtractParams(key);

        foreach (var (opType, operation) in item.Operations)
        {
          if (canonicalItem.Operations.ContainsKey(opType)) continue;

          for (int i = 0; i < Math.Min(dupeParams.Count, canonicalParams.Count); i++)
          {
            if (dupeParams[i] == canonicalParams[i]) continue;
            foreach (OpenApiParameter param in operation.Parameters)
            {
              if (param.Name == dupeParams[i])
                param.Name = canonicalParams[i];
            }
          }

          canonicalItem.AddOperation(opType, operation);
        }

        doc.Paths.Remove(key);
        count++;
      }
    }

    return count;
  }

  public static int FixMultilineSummaries(OpenApiDocument doc)
  {
    int count = 0;
    foreach (var (_, pathItem) in doc.Paths)
    {
      foreach (var (_, operation) in pathItem.Operations)
      {
        if (operation.Summary != null && operation.Summary.Contains('\n'))
        {
          operation.Summary = string.Join(" ",
            operation.Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
              .Select(s => s.Trim()));
          count++;
        }
      }
    }
    return count;
  }

  public static int FixCipherDataType(OpenApiDocument doc)
  {
    if (doc.Components?.Schemas == null) return 0;

    string[] cipherModels =
    [
      "CipherDetailsResponseModel",
      "CipherMiniDetailsResponseModel",
      "CipherResponseModel",
      "CipherMiniResponseModel"
    ];

    int count = 0;
    foreach (string modelName in cipherModels)
    {
      if (!doc.Components.Schemas.TryGetValue(modelName, out OpenApiSchema? schema)) continue;
      if (schema.Properties == null) continue;
      if (!schema.Properties.TryGetValue("data", out OpenApiSchema? dataProp)) continue;

      if (dataProp.Type == "string")
      {
        dataProp.Type = "object";
        count++;
      }
    }
    return count;
  }

  private static string NormalizePath(string path) =>
    string.Join("/", path.Split('/').Select(s => s.StartsWith('{') ? "{}" : s));

  private static List<string> ExtractParams(string path) =>
    path.Split('/').Where(s => s.StartsWith('{') && s.EndsWith('}')).Select(s => s[1..^1]).ToList();
}
