using System.CommandLine;
using System.CommandLine.Parsing;
using Bitwarden.OpenApi.Generator.Docker;
using Bitwarden.OpenApi.Generator.Patching;
using Bitwarden.OpenApi.Generator.Consolidation;
using Bitwarden.OpenApi.Generator.Vaultwarden;

RootCommand rootCommand =
  new("Bitwarden OpenAPI Spec Generator — generates OpenAPI specs from the Bitwarden server source code");

// ── generate <version> ───────────────────────────────────────────────────

Argument<string> versionArg = new("version")
  { DefaultValueFactory = _ => "main", Description = "Bitwarden server version tag (e.g., 2024.1.0) or 'main'" };
Option<string> outputOption = new("--output")
  { DefaultValueFactory = _ => "specs/bitwarden", Description = "Output directory for generated specs" };
Option<bool> verboseOption = new("--verbose", "-v") { Description = "Show all Docker build output" };

Command generateCommand = new("generate", "Generate OpenAPI specs for a single Bitwarden server version")
{
  versionArg,
  outputOption,
  verboseOption
};

generateCommand.SetAction(async (parseResult, ct) =>
{
  string version = parseResult.GetValue(versionArg)!;
  string outputBase = parseResult.GetValue(outputOption)!;
  bool verbose = parseResult.GetValue(verboseOption);
  string outputDir = version == "main" ? Path.Combine(outputBase, "main") : Path.Combine(outputBase, version);

  Console.WriteLine($"Generating OpenAPI specs for Bitwarden server {version}...");
  Console.WriteLine();

  DockerSpecGenerator generator = new(verbose);
  GenerateResult result = await generator.GenerateAsync(version, outputDir, ct);

  if (!result.Success)
  {
    Console.Error.WriteLine($"Failed: {result.Error}");
    Environment.ExitCode = 1;
    return;
  }

  Console.WriteLine("Patching specs...");
  SpecPatcher.PatchAll(outputDir, version);

  Console.WriteLine();
  Console.WriteLine($"Generated {result.SpecFiles.Count} spec(s) in {outputDir}/");
  foreach (string file in result.SpecFiles)
    Console.WriteLine($"  {Path.GetFileName(file)}");
});

rootCommand.Add(generateCommand);

// ── generate-all ─────────────────────────────────────────────────────────

Option<string> fromOption = new("--from")
  { DefaultValueFactory = _ => "2024", Description = "Minimum version year or specific version" };
Option<string?> toOption = new("--to") { Description = "Maximum version (inclusive)" };

Command generateAllCommand = new("generate-all", "Generate OpenAPI specs for all Bitwarden server release tags")
{
  outputOption,
  fromOption,
  toOption,
  verboseOption
};

generateAllCommand.SetAction(async (parseResult, ct) =>
{
  string outputBase = parseResult.GetValue(outputOption)!;
  string from = parseResult.GetValue(fromOption)!;
  string? to = parseResult.GetValue(toOption);

  bool verbose = parseResult.GetValue(verboseOption);

  Console.WriteLine("Fetching Bitwarden server tags...");
  DockerSpecGenerator generator = new(verbose);
  List<string> allTags = await generator.ListTagsAsync(ct);

  // Filter tags
  List<string> tags;
  if (from.Contains('.'))
    tags = allTags.SkipWhile(t => t != from).ToList();
  else
    tags = allTags.Where(t => int.TryParse(t.Split('.')[0], out int year) && year >= int.Parse(from)).ToList();

  if (to != null)
    tags = tags.TakeWhile(t => t != to).Append(to).Where(allTags.Contains).ToList();

  Console.WriteLine($"Found {tags.Count} tags to process");
  Console.WriteLine();

  int succeeded = 0, failed = 0, skipped = 0;
  List<string> failedTags = [];

  foreach (string tag in tags)
  {
    string outputDir = Path.Combine(outputBase, tag);

    if (File.Exists(Path.Combine(outputDir, "bitwarden-vault.json")))
    {
      Console.WriteLine($"[{tag}] Already exists, skipping");
      skipped++;
      succeeded++;
      continue;
    }

    Console.Write($"[{tag}] Generating... ");

    GenerateResult result = await generator.GenerateAsync(tag, outputDir, ct);

    if (result.Success)
    {
      SpecPatcher.PatchAll(outputDir, tag);
      Console.WriteLine($"OK ({result.SpecFiles.Count} specs)");
      succeeded++;
    }
    else
    {
      Console.WriteLine($"FAILED ({result.Error?[..Math.Min(80, result.Error?.Length ?? 0)]})");
      failed++;
      failedTags.Add(tag);
      if (Directory.Exists(outputDir))
        Directory.Delete(outputDir, true);
    }
  }

  Console.WriteLine();
  Console.WriteLine($"Results: {succeeded} succeeded ({skipped} cached), {failed} failed");
  if (failedTags.Count > 0)
    Console.WriteLine($"Failed: {string.Join(", ", failedTags)}");

  Console.WriteLine();
  Console.WriteLine("Consolidating...");
  List<VersionInfo> versions = SpecConsolidator.Consolidate(outputBase);
  SpecConsolidator.PrintSummary(versions);
});

rootCommand.Add(generateAllCommand);

// ── list-tags ────────────────────────────────────────────────────────────

Command listTagsCommand = new("list-tags", "List all available Bitwarden server release tags");

listTagsCommand.SetAction(async (parseResult, ct) =>
{
  DockerSpecGenerator generator = new();
  List<string> tags = await generator.ListTagsAsync(ct);
  foreach (string tag in tags)
    Console.WriteLine(tag);
});

rootCommand.Add(listTagsCommand);

// ── consolidate ──────────────────────────────────────────────────────────

Command consolidateCommand = new("consolidate", "Analyze generated specs and produce versions.json")
{
  outputOption
};

consolidateCommand.SetAction((parseResult, ct) =>
{
  string outputBase = parseResult.GetValue(outputOption)!;
  List<VersionInfo> versions = SpecConsolidator.Consolidate(outputBase);
  SpecConsolidator.PrintSummary(versions);
  return Task.CompletedTask;
});

rootCommand.Add(consolidateCommand);

// ── vaultwarden ──────────────────────────────────────────────────────────

Argument<string> vwVersionArg = new("version")
  { DefaultValueFactory = _ => "latest", Description = "Vaultwarden version tag (e.g., 1.35.4) or 'latest'" };
Option<string> vwSpecsOption = new("--specs")
  { DefaultValueFactory = _ => "specs/bitwarden", Description = "Path to Bitwarden specs directory" };
Option<string> vwOutputOption = new("--output")
  { DefaultValueFactory = _ => "specs/vaultwarden", Description = "Output directory for filtered specs" };

Command vaultwardenCommand = new("vaultwarden", "Analyze Vaultwarden compatibility and generate filtered specs")
{
  vwVersionArg,
  vwSpecsOption,
  vwOutputOption
};

vaultwardenCommand.SetAction(async (parseResult, ct) =>
{
  string version = parseResult.GetValue(vwVersionArg)!;
  string specsDir = parseResult.GetValue(vwSpecsOption)!;
  string outputBase = parseResult.GetValue(vwOutputOption)!;

  Console.WriteLine($"Analyzing Vaultwarden {version} compatibility...");
  Console.WriteLine();

  VaultwardenAnalysis analysis =
    await Bitwarden.OpenApi.Generator.Vaultwarden.RouteParser.AnalyzeAsync(version, ct: ct);
  Console.WriteLine($"  API routes:      {analysis.ApiRoutes.Count}");
  Console.WriteLine($"  Identity routes: {analysis.IdentityRoutes.Count}");
  Console.WriteLine($"  Public routes:   {analysis.PublicRoutes.Count}");
  Console.WriteLine($"  Admin routes:    {analysis.AdminRoutes.Count} (Vaultwarden-specific)");
  Console.WriteLine();

  // Find the matching Bitwarden spec version
  string bwVersion = analysis.BitwardenCompatVersion;
  string bwSpecDir = Path.Combine(specsDir, bwVersion);
  if (!Directory.Exists(bwSpecDir))
  {
    // Fall back to latest available
    string? available = Directory.GetDirectories(specsDir)
      .Select(Path.GetFileName)
      .Where(d => d != null && char.IsDigit(d[0]))
      .OrderByDescending(d => d)
      .FirstOrDefault();

    if (available == null)
    {
      Console.Error.WriteLine($"No Bitwarden specs found in {specsDir}. Run 'generate' first.");
      Environment.ExitCode = 1;
      return;
    }

    Console.WriteLine($"  Bitwarden {bwVersion} spec not found, using {available}");
    bwVersion = available;
    bwSpecDir = Path.Combine(specsDir, bwVersion);
  }

  Console.WriteLine($"  Comparing against Bitwarden {bwVersion}...");
  Console.WriteLine();

  // Compare each spec — use requested version for directory name, not Cargo.toml version
  string vwOutputDir = Path.Combine(outputBase, version);
  (string inputFile, string outputFile, string specName, List<VaultwardenRoute> routes)[] specMappings =
    new (string inputFile, string outputFile, string specName,
      List<Bitwarden.OpenApi.Generator.Vaultwarden.VaultwardenRoute> routes)[]
      {
        ("bitwarden-vault.json", "vaultwarden-vault.json", "Vault API", analysis.ApiRoutes),
        ("bitwarden-identity.json", "vaultwarden-identity.json", "Identity API", analysis.IdentityRoutes),
        ("bitwarden-public.json", "vaultwarden-public.json", "Public API", analysis.PublicRoutes)
      };

  List<Bitwarden.OpenApi.Generator.Vaultwarden.CompatibilityResult> results = [];
  foreach ((string inputFile, string outputFile, string specName, List<VaultwardenRoute> routes) in specMappings)
  {
    string specPath = Path.Combine(bwSpecDir, inputFile);
    if (!File.Exists(specPath))
    {
      Console.WriteLine($"  {specName}: spec not found, skipping");
      continue;
    }

    CompatibilityResult result = Bitwarden.OpenApi.Generator.Vaultwarden.CompatibilityAnalyzer.Compare(
      specPath, specName, routes, analysis.Version, bwVersion);
    results.Add(result);

    Bitwarden.OpenApi.Generator.Vaultwarden.CompatibilityAnalyzer.PrintReport(result);

    // Generate filtered spec
    string outputPath = Path.Combine(vwOutputDir, outputFile);
    Bitwarden.OpenApi.Generator.Vaultwarden.CompatibilityAnalyzer.GenerateFilteredSpec(specPath, outputPath, result,
      version);
  }

  Console.WriteLine();
  Console.WriteLine($"Filtered specs written to {vwOutputDir}/");

  // Write compatibility report as JSON
  string reportPath = Path.Combine(vwOutputDir, "compatibility-report.json");
  var report = new
  {
    vaultwarden_version = version,
    bitwarden_version = bwVersion,
    generated = DateTime.UtcNow.ToString("O"),
    specs = results.Select(r => new
    {
      name = r.SpecName,
      matched = r.Matched.Count,
      total = r.Matched.Count + r.MissingInVaultwarden.Count,
      compatibility = r.Matched.Count + r.MissingInVaultwarden.Count > 0
        ? (double)r.Matched.Count / (r.Matched.Count + r.MissingInVaultwarden.Count) * 100
        : 0,
      missing = r.MissingInVaultwarden.Select(m => $"{m.Method} {m.Path}"),
      extra = r.ExtraInVaultwarden.Select(m => $"{m.Method} {m.Path}")
    })
  };
  File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
  Console.WriteLine($"Compatibility report: {reportPath}");
});

rootCommand.Add(vaultwardenCommand);

// ── Run ──────────────────────────────────────────────────────────────────

ParseResult parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();