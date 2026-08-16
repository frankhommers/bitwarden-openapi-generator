using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace Bitwarden.OpenApi.Generator.Docker;

/// <summary>
/// Generates OpenAPI specs by building the Bitwarden server in a Docker container.
/// Uses CliWrap to shell to Docker CLI.
/// </summary>
public class DockerSpecGenerator
{
  private const string ImageName = "bitwarden-openapi-generator";
  private const string RepoUrl = "https://github.com/bitwarden/server.git";

  private static readonly string[] SpecFiles =
  [
    "bitwarden-vault.json",
    "bitwarden-public.json",
    "bitwarden-identity.json"
  ];

  // Docker build step keywords we want to surface to the user
  private static readonly string[] InterestingKeywords =
  [
    "git clone", "dotnet tool restore", "dotnet build", "dotnet swagger",
    "Step ", "RUN ", " --->"
  ];

  private readonly bool _verbose;

  public DockerSpecGenerator(bool verbose = false)
  {
    _verbose = verbose;
  }

  /// <summary>
  /// Generate OpenAPI specs for a specific Bitwarden server version.
  /// </summary>
  public async Task<GenerateResult> GenerateAsync(string version, string outputDir, CancellationToken ct = default)
  {
    string gitRef = version == "main" ? "main" : $"v{version}";
    string tag = $"{ImageName}:{version.Replace('.', '-')}";
    string sdkVersion = GetSdkVersion(version);

    Directory.CreateDirectory(outputDir);

    Log(version, $"Using .NET SDK {sdkVersion}");

    // Build Docker image with streaming output
    string dockerfilePath = GetDockerfilePath();
    StringBuilder stderrBuffer = new();
    List<string> errorLines = [];

    void HandleOutput(string line)
    {
      LogBuildOutput(line, version);
      if (!IsErrorLine(line)) return;
      lock (errorLines)
      {
        if (errorLines.Count < 20)
          errorLines.Add(line.Trim());
      }
    }

    try
    {
      CommandResult result = await Cli.Wrap("docker")
        .WithArguments([
          "build",
          "--no-cache",
          "-t", tag,
          "--build-arg", $"REPO_URL={RepoUrl}",
          "--build-arg", $"REF={gitRef}",
          "--build-arg", $"SDK_VERSION={sdkVersion}",
          "-f", dockerfilePath,
          "."
        ])
        .WithWorkingDirectory(Path.GetDirectoryName(dockerfilePath)!)
        .WithStandardOutputPipe(PipeTarget.ToDelegate(HandleOutput))
        .WithStandardErrorPipe(PipeTarget.Merge(
          PipeTarget.ToDelegate(HandleOutput),
          PipeTarget.ToStringBuilder(stderrBuffer)))
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync(ct);

      if (result.ExitCode != 0)
      {
        string error = errorLines.Count > 0
          ? string.Join(" | ", errorLines)
          : stderrBuffer.ToString();
        return new GenerateResult
        {
          Success = false,
          Version = version,
          Error = $"Docker build failed (exit {result.ExitCode}): {error[..Math.Min(500, error.Length)]}"
        };
      }
    }
    catch (Exception ex)
    {
      return new GenerateResult { Success = false, Version = version, Error = ex.Message };
    }

    // Create container and copy specs out
    Log(version, "Extracting specs from container...");
    try
    {
      BufferedCommandResult createResult = await Cli.Wrap("docker")
        .WithArguments(["create", tag])
        .ExecuteBufferedAsync(ct);
      string containerId = createResult.StandardOutput.Trim();

      try
      {
        foreach (string specFile in SpecFiles)
        {
          string destPath = Path.Combine(outputDir, specFile);
          await Cli.Wrap("docker")
            .WithArguments(["cp", $"{containerId}:/specs/{specFile}", destPath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

          if (File.Exists(destPath))
            Log(version, $"  Extracted {specFile} ({new FileInfo(destPath).Length:N0} bytes)");
        }
      }
      finally
      {
        await Cli.Wrap("docker")
          .WithArguments(["rm", "-f", containerId])
          .WithValidation(CommandResultValidation.None)
          .ExecuteAsync(ct);
      }
    }
    catch (Exception ex)
    {
      return new GenerateResult
        { Success = false, Version = version, Error = $"Failed to extract specs: {ex.Message}" };
    }

    // Verify output
    List<string> generatedFiles = SpecFiles
      .Select(f => Path.Combine(outputDir, f))
      .Where(File.Exists)
      .ToList();

    return new GenerateResult
    {
      Success = generatedFiles.Count > 0,
      Version = version,
      OutputDir = outputDir,
      SpecFiles = generatedFiles
    };
  }

  /// <summary>
  /// List all available Bitwarden server release tags from GitHub.
  /// </summary>
  public async Task<List<string>> ListTagsAsync(CancellationToken ct = default)
  {
    BufferedCommandResult result = await Cli.Wrap("git")
      .WithArguments(["ls-remote", "--tags", RepoUrl])
      .ExecuteBufferedAsync(ct);

    return result.StandardOutput
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line =>
      {
        int idx = line.IndexOf("refs/tags/v", StringComparison.Ordinal);
        if (idx < 0) return null;
        string tag = line[(idx + "refs/tags/v".Length)..];
        if (tag.Contains('^')) return null;
        if (tag.Split('.') is [var a, var b, var c] &&
            int.TryParse(a, out _) && int.TryParse(b, out _) && int.TryParse(c, out _))
          return tag;
        return null;
      })
      .Where(t => t != null)
      .Cast<string>()
      .OrderBy(v => v.Split('.').Select(int.Parse).Aggregate(0L, (acc, n) => acc * 10000 + n))
      .ToList();
  }

  /// <summary>
  /// Clean up Docker images created by this tool.
  /// </summary>
  public async Task CleanupAsync(CancellationToken ct = default)
  {
    await Cli.Wrap("docker")
      .WithArguments(["image", "prune", "-f", "--filter", $"reference={ImageName}:*"])
      .WithValidation(CommandResultValidation.None)
      .ExecuteAsync(ct);
  }

  /// <summary>
  /// Determine the .NET SDK version needed for a given Bitwarden server version.
  /// The server migrated from .NET 6 to .NET 8 at 2024.8.0 and to .NET 10 at 2026.5.0.
  /// </summary>
  private static string GetSdkVersion(string version)
  {
    const string LatestSdk = "10.0";

    if (version == "main") return LatestSdk;

    string[] parts = version.Split('.');
    if (parts.Length < 2 || !int.TryParse(parts[0], out int year) || !int.TryParse(parts[1], out int minor))
      return LatestSdk;

    if (year < 2024 || (year == 2024 && minor < 8))
      return "6.0";

    if (year < 2026 || (year == 2026 && minor < 5))
      return "8.0";

    return LatestSdk;
  }

  private void LogBuildOutput(string line, string version)
  {
    if (string.IsNullOrWhiteSpace(line)) return;

    if (_verbose)
    {
      Console.WriteLine($"  [{version}] {line}");
      return;
    }

    // In non-verbose mode, show only interesting build steps
    // Docker BuildKit format: "#N step description"
    // Classic format: "Step N/M : RUN ..."
    string trimmed = line.TrimStart();

    // Always surface compiler/restore errors, also inside BuildKit "#N <time> ..." lines
    if (IsErrorLine(trimmed))
    {
      Console.WriteLine($"  [{version}] {trimmed}");
      return;
    }

    // BuildKit: lines starting with #<number> are step headers
    if (trimmed.StartsWith('#') && trimmed.Length > 1 && char.IsDigit(trimmed[1]))
    {
      // Only show RUN steps (skip COPY, FROM, etc. noise)
      if (trimmed.Contains("RUN ", StringComparison.OrdinalIgnoreCase))
      {
        // Extract just the command part
        int runIdx = trimmed.IndexOf("RUN ", StringComparison.OrdinalIgnoreCase);
        string cmd = trimmed[(runIdx + 4)..].Trim();
        // Shorten long commands
        if (cmd.Length > 80) cmd = cmd[..77] + "...";
        Console.WriteLine($"  [{version}] {cmd}");
      }

      return;
    }

    // Classic Docker build format
    if (trimmed.StartsWith("Step ", StringComparison.OrdinalIgnoreCase))
    {
      if (InterestingKeywords.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  [{version}] {trimmed}");
      return;
    }

  }

  private static bool IsErrorLine(string line) =>
    line.Contains("error ", StringComparison.OrdinalIgnoreCase) ||
    line.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
    line.Contains("FAILED", StringComparison.Ordinal);

  private static void Log(string version, string message)
  {
    Console.WriteLine($"  [{version}] {message}");
  }

  private static string GetDockerfilePath()
  {
    string assemblyDir = Path.GetDirectoryName(typeof(DockerSpecGenerator).Assembly.Location)!;

    string path = Path.Combine(assemblyDir, "Docker", "Dockerfile.txt");
    if (File.Exists(path)) return path;

    string? projectDir = FindProjectDir();
    if (projectDir != null)
    {
      path = Path.Combine(projectDir, "Docker", "Dockerfile.txt");
      if (File.Exists(path)) return path;
    }

    throw new FileNotFoundException("Could not find Dockerfile.txt");
  }

  private static string? FindProjectDir()
  {
    string? dir = AppContext.BaseDirectory;
    while (dir != null)
    {
      if (Directory.GetFiles(dir, "*.csproj").Length > 0)
        return dir;
      dir = Path.GetDirectoryName(dir);
    }

    return null;
  }
}

public class GenerateResult
{
  public bool Success { get; init; }
  public string Version { get; init; } = "";
  public string? OutputDir { get; init; }
  public string? Error { get; init; }
  public List<string> SpecFiles { get; init; } = [];
}