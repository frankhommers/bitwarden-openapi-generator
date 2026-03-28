using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Bitwarden.OpenApi.Generator.Patching;

public static class IdentityPatches
{
  public static int AddConnectToken(OpenApiDocument doc)
  {
    if (doc.Paths.ContainsKey("/connect/token")) return 0;

    doc.Components ??= new OpenApiComponents();
    doc.Components.Schemas ??= new Dictionary<string, OpenApiSchema>();

    doc.Components.Schemas["TokenResponse"] = new OpenApiSchema
    {
      Type = "object",
      Properties = new Dictionary<string, OpenApiSchema>
      {
        ["access_token"] = new() { Type = "string" },
        ["expires_in"] = new() { Type = "integer", Format = "int32" },
        ["token_type"] = new() { Type = "string" },
        ["refresh_token"] = new() { Type = "string", Nullable = true },
        ["scope"] = new() { Type = "string" },
        ["PrivateKey"] = new() { Type = "string", Nullable = true, Description = "RSA private key encrypted with the user symmetric key" },
        ["Key"] = new() { Type = "string", Nullable = true, Description = "Protected symmetric key encrypted with the stretched master key" },
        ["Kdf"] = new() { Type = "integer", Format = "int32", Description = "KDF type: 0=PBKDF2_SHA256, 1=Argon2id" },
        ["KdfIterations"] = new() { Type = "integer", Format = "int32" },
        ["KdfMemory"] = new() { Type = "integer", Format = "int32", Nullable = true },
        ["KdfParallelism"] = new() { Type = "integer", Format = "int32", Nullable = true },
        ["ForcePasswordReset"] = new() { Type = "boolean" },
        ["ResetMasterPassword"] = new() { Type = "boolean" },
        ["MasterPasswordPolicy"] = new() { Type = "object", Nullable = true },
        ["UserDecryptionOptions"] = new() { Type = "object", Nullable = true },
      }
    };

    doc.Components.Schemas["TokenErrorResponse"] = new OpenApiSchema
    {
      Type = "object",
      Properties = new Dictionary<string, OpenApiSchema>
      {
        ["error"] = new() { Type = "string" },
        ["error_description"] = new() { Type = "string", Nullable = true },
        ["TwoFactorProviders"] = new()
        {
          Type = "array",
          Items = new OpenApiSchema { Type = "integer", Format = "int32" },
          Nullable = true,
          Description = "Available 2FA provider types when 2FA is required"
        },
        ["TwoFactorProviders2"] = new() { Type = "object", Nullable = true, Description = "Detailed 2FA provider info keyed by provider type" },
        ["SsoEmail2faSessionToken"] = new() { Type = "string", Nullable = true },
        ["MasterPasswordPolicy"] = new() { Type = "object", Nullable = true },
      }
    };

    OpenApiSchema requestSchema = new()
    {
      Type = "object",
      Required = new HashSet<string> { "grant_type", "client_id" },
      Properties = new Dictionary<string, OpenApiSchema>
      {
        ["grant_type"] = new() { Type = "string", Description = "OAuth2 grant type: 'password' or 'client_credentials'", Enum = [new OpenApiString("password"), new OpenApiString("client_credentials")] },
        ["username"] = new() { Type = "string", Description = "Email address (for password grant)" },
        ["password"] = new() { Type = "string", Description = "Base64-encoded master password hash (for password grant)" },
        ["scope"] = new() { Type = "string", Description = "Requested scopes, e.g. 'api offline_access'" },
        ["client_id"] = new() { Type = "string", Description = "Client identifier: 'cli', 'web', 'browser', 'desktop', 'mobile', or an API key client_id" },
        ["client_secret"] = new() { Type = "string", Description = "Client secret (for client_credentials grant with API key)" },
        ["deviceType"] = new() { Type = "integer", Format = "int32", Description = "Device type enum: 9=CLI, 6=WindowsDesktop, 7=MacOsDesktop, etc." },
        ["deviceIdentifier"] = new() { Type = "string", Description = "Unique device GUID" },
        ["deviceName"] = new() { Type = "string", Description = "Device name string, e.g. 'cli'" },
        ["devicePushToken"] = new() { Type = "string", Description = "Push notification token (empty for CLI)" },
        ["twoFactorToken"] = new() { Type = "string", Description = "2FA code (TOTP, YubiKey, etc.)" },
        ["twoFactorProvider"] = new() { Type = "integer", Format = "int32", Description = "2FA provider: 0=Authenticator, 1=Email, 2=Duo, 3=YubiKey, 5=Remember, 7=WebAuthn" },
        ["twoFactorRemember"] = new() { Type = "integer", Format = "int32", Description = "Set to 1 to remember this device for 2FA" },
      }
    };

    doc.Paths["/connect/token"] = new OpenApiPathItem
    {
      Operations = new Dictionary<OperationType, OpenApiOperation>
      {
        [OperationType.Post] = new()
        {
          Tags = [new OpenApiTag { Name = "Connect" }],
          OperationId = "Connect_Token",
          Summary = "Exchange credentials for an access token (OAuth2 password or client_credentials grant)",
          RequestBody = new OpenApiRequestBody
          {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
              ["application/x-www-form-urlencoded"] = new() { Schema = requestSchema }
            }
          },
          Responses = new OpenApiResponses
          {
            ["200"] = new()
            {
              Description = "Successful authentication",
              Content = new Dictionary<string, OpenApiMediaType>
              {
                ["application/json"] = new()
                {
                  Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "TokenResponse" } }
                }
              }
            },
            ["400"] = new()
            {
              Description = "Authentication failed (invalid credentials, 2FA required, etc.)",
              Content = new Dictionary<string, OpenApiMediaType>
              {
                ["application/json"] = new()
                {
                  Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "TokenErrorResponse" } }
                }
              }
            }
          }
        }
      }
    };

    return 1;
  }
}
