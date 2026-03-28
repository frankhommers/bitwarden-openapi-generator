# Bitwarden OpenAPI Generator

Generates OpenAPI specs from the [Bitwarden server](https://github.com/bitwarden/server) source code using Docker, with [Vaultwarden](https://github.com/dani-garcia/vaultwarden) compatibility analysis.

> **Disclaimer:** This is an unofficial project, not affiliated with Bitwarden Inc. or Vaultwarden. The generated specs are derived from Bitwarden's AGPL-3.0 licensed server source code. This tool itself is licensed under MIT.

## What it does

- Builds the Bitwarden server in a Docker container and extracts Swagger/OpenAPI specs
- Patches known issues: duplicate paths, multiline summaries, missing `/connect/token` endpoint, incorrect schema types
- Sets `info.version` to the actual Bitwarden server version
- Generates specs for multiple versions and deduplicates identical API surfaces
- Analyzes Vaultwarden source code to determine which Bitwarden endpoints are implemented
- Produces filtered Vaultwarden specs with unused schemas pruned

## Generated specs

Three specs per version:

| Spec | Description |
|------|-------------|
| `bitwarden-vault.json` | Internal API — ciphers, folders, sync, sends, organizations (used by CLI, web vault, mobile) |
| `bitwarden-identity.json` | Identity API — authentication, tokens, SSO, 2FA, including patched `/connect/token` |
| `bitwarden-public.json` | Public API — organization management endpoints |

Pre-generated specs are included in `specs/bitwarden/` (40 versions from 2025.1.0 to 2026.3.1) and `specs/vaultwarden/` (1.35.4).

## Requirements

- .NET 10 SDK
- Docker

## Usage

```bash
# Generate specs for a specific Bitwarden version
dotnet run --project src/Bitwarden.OpenApi.Generator -- generate 2026.3.1

# Generate specs for all versions from 2025 onwards
dotnet run --project src/Bitwarden.OpenApi.Generator -- generate-all --from 2025

# List available Bitwarden server tags
dotnet run --project src/Bitwarden.OpenApi.Generator -- list-tags

# Analyze Vaultwarden compatibility and generate filtered specs
dotnet run --project src/Bitwarden.OpenApi.Generator -- vaultwarden 1.35.4

# Re-run consolidation on existing specs
dotnet run --project src/Bitwarden.OpenApi.Generator -- consolidate
```

Use `--output` to change the output directory (default: `specs/bitwarden` or `specs/vaultwarden`).
Use `--verbose` for full Docker build output.

## Patches applied

The raw Swagger output from Bitwarden has several issues that are automatically patched:

| Patch | Description |
|-------|-------------|
| Duplicate paths | Merges `/organizations/{id}` and `/organizations/{organizationId}` |
| Multiline summaries | Flattens `\n` in operation summaries that break code generators |
| Cipher data type | Fixes `CipherDetailsResponseModel.data` from `string` to `object` |
| `/connect/token` | Adds the OAuth2 token endpoint that IdentityServer doesn't export via Swagger |

## Vaultwarden compatibility

The `vaultwarden` command parses Rocket route attributes from Vaultwarden source, matches them against the Bitwarden spec version that Vaultwarden claims compatibility with, and produces:

- Filtered specs containing only implemented endpoints
- Pruned schemas (unreferenced models removed)
- A `compatibility-report.json` with match/miss details

Example output for Vaultwarden 1.35.4 (claiming Bitwarden 2025.12.0):

| Spec | Matched | Total | Coverage |
|------|---------|-------|----------|
| Vault API | 235 | 562 | 42% |
| Identity API | 4 | 15 | 27% |
| Public API | 1 | 26 | 4% |

The "missing" endpoints are predominantly billing, SCIM, SSO/SAML, and enterprise policy features. Core vault operations (ciphers, folders, sync, sends) are well covered.

## Project structure

```
src/Bitwarden.OpenApi.Generator/
  Program.cs                         # CLI commands
  Docker/
    DockerSpecGenerator.cs           # Builds Bitwarden in Docker, extracts specs
    Dockerfile.txt                   # Multi-stage build template
  Patching/
    SpecPatcher.cs                   # Orchestrates all patches
    VaultPatches.cs                  # Duplicate paths, summaries, cipher data type
    IdentityPatches.cs               # /connect/token endpoint
  Vaultwarden/
    RouteParser.cs                   # Parses Rocket #[get/post("...")] attributes
    CompatibilityAnalyzer.cs         # Compares routes, filters specs, prunes schemas
  Consolidation/
    SpecConsolidator.cs              # Deduplicates identical API surfaces
```

## License

MIT — see [LICENSE](LICENSE).

The generated OpenAPI specs contain descriptions and metadata from the Bitwarden server, which is licensed under [AGPL-3.0](https://github.com/bitwarden/server/blob/master/LICENSE.txt).
