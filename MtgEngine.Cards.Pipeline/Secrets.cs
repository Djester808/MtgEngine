using Microsoft.Extensions.Configuration;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// The Anthropic key, from the API's user-secrets store or the environment. Never printed,
/// never written to a file, never in a brief.
/// </summary>
public static class Secrets
{
    public static string AnthropicApiKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("c08fa7fc-c4cf-464d-912e-e908d84a6322")
            .AddEnvironmentVariables()
            .Build();

        var key = configuration["Anthropic:ApiKey"] ?? configuration["ANTHROPIC_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "No Anthropic key. Set it with `dotnet user-secrets set \"Anthropic:ApiKey\" \"...\" --project MtgEngine.Api` or the ANTHROPIC_API_KEY variable.");
        }

        return key;
    }
}
