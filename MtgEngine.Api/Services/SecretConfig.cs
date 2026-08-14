namespace MtgEngine.Api.Services;

/// <summary>
/// Centralised access to secrets. Secrets must never live in appsettings.json —
/// supply them via user-secrets (dev) or environment variables (prod), where the
/// config path maps with '__' in place of ':' (e.g. Anthropic__ApiKey).
/// </summary>
internal static class SecretConfig
{
    public const string AnthropicApiKeyPath = "Anthropic:ApiKey";
    public const string JwtSecretPath = "Jwt:Secret";

    /// <summary>Minimum key length for HMAC-SHA256 token signing, in bytes.</summary>
    private const int MinJwtSecretBytes = 32;

    public static string AnthropicApiKey(IConfiguration config) =>
        Require(config, AnthropicApiKeyPath, "sk-ant-...");

    public static string JwtSecret(IConfiguration config)
    {
        var secret = Require(config, JwtSecretPath, "<random 32+ byte string>");

        // HS256 rejects keys shorter than the hash size; fail here with a clear
        // message rather than deep inside the token pipeline.
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(secret);
        if (byteCount < MinJwtSecretBytes)
        {
            throw new ConfigurationException(
                $"{JwtSecretPath} is only {byteCount} bytes; HMAC-SHA256 signing requires " +
                $"at least {MinJwtSecretBytes}. Generate a new one with:\n" +
                "  dotnet user-secrets set \"Jwt:Secret\" " +
                "\"$([Convert]::ToBase64String((1..48|%{Get-Random -Max 256})))\" --project MtgEngine.Api");
        }

        return secret;
    }

    private static string Require(IConfiguration config, string path, string exampleValue)
    {
        var value = config[path];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConfigurationException(
                $"{path} is not configured. Set it with:\n" +
                $"  dotnet user-secrets set \"{path}\" \"{exampleValue}\" --project MtgEngine.Api\n" +
                $"or supply the {path.Replace(":", "__")} environment variable.");
        }
        return value;
    }
}
