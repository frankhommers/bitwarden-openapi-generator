using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Writers;

namespace Bitwarden.OpenApi.Generator.Patching;

public static class SpecPatcher
{
  public static void PatchAll(string outputDir, string? version = null)
  {
    string vaultPath = Path.Combine(outputDir, "bitwarden-vault.json");
    string identityPath = Path.Combine(outputDir, "bitwarden-identity.json");
    string publicPath = Path.Combine(outputDir, "bitwarden-public.json");

    if (version != null)
    {
      foreach (string path in new[] { vaultPath, identityPath, publicPath })
      {
        if (!File.Exists(path)) continue;
        OpenApiDocument doc = LoadSpec(path);
        doc.Info.Version = version;
        SaveSpec(path, doc);
      }
      Console.WriteLine($"  All: info.version set to {version}");
    }

    if (File.Exists(vaultPath))
    {
      OpenApiDocument vault = LoadSpec(vaultPath);
      int dupes = VaultPatches.MergeDuplicatePaths(vault);
      int summaries = VaultPatches.FixMultilineSummaries(vault);
      int dataTypes = VaultPatches.FixCipherDataType(vault);
      SaveSpec(vaultPath, vault);
      Console.WriteLine($"  Vault: {dupes} duplicate paths merged, {summaries} summaries fixed, {dataTypes} data types fixed");
    }

    if (File.Exists(identityPath))
    {
      OpenApiDocument identity = LoadSpec(identityPath);
      int token = IdentityPatches.AddConnectToken(identity);
      SaveSpec(identityPath, identity);
      Console.WriteLine($"  Identity: {token} endpoints added");
    }
  }

  public static OpenApiDocument LoadSpec(string path)
  {
    using FileStream stream = File.OpenRead(path);
    return new OpenApiStreamReader().Read(stream, out OpenApiDiagnostic _);
  }

  public static void SaveSpec(string path, OpenApiDocument doc)
  {
    using FileStream stream = File.Create(path);
    using StreamWriter writer = new(stream);
    doc.SerializeAsV3(new OpenApiJsonWriter(writer));
  }
}
