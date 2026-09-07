using System.Globalization;
using Anthropic;
using Anthropic.Models.Messages;
using Batches = Anthropic.Models.Messages.Batches;

namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// The real transport: Message Batches with the vocabulary reference as a cached system prefix,
/// and a single non-batch call for the repair round. Mirrors the calls the wave-0 pilot's
/// <c>Translator</c> makes; that copy goes when the pilot does.
/// </summary>
public sealed class AnthropicTranslationClient(AnthropicClient client) : ITranslationClient
{
    /// <summary>Enough for the largest card in the corpus; the batch is billed on what is produced.</summary>
    private const int MaxTokens = 16000;

    public async Task<string> SubmitBatch(string model, IReadOnlyList<(string customId, string system, string user)> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var payload = new List<Batches.Request>();
        foreach (var (customId, system, user) in requests)
        {
            payload.Add(new()
            {
                CustomID = customId,
                Params = new Batches.Params
                {
                    Model = model,
                    MaxTokens = MaxTokens,
                    System = new Batches.ParamsSystem(SystemBlocks(system)),
                    Messages = [new() { Role = Role.User, Content = user }],
                },
            });
        }

        var batch = await client.Messages.Batches.Create(new Batches.BatchCreateParams { Requests = payload }, cancellationToken: ct);
        return batch.ID;
    }

    public async Task<BatchStatus> Poll(string batchId, CancellationToken ct)
    {
        var status = await client.Messages.Batches.Retrieve(batchId, cancellationToken: ct);
        var counts = status.RequestCounts;
        var ended = string.Equals(status.ProcessingStatus.ToString(), "ended", StringComparison.OrdinalIgnoreCase);
        return new BatchStatus(
            ended,
            System.Convert.ToInt64(counts.Succeeded, CultureInfo.InvariantCulture),
            System.Convert.ToInt64(counts.Errored, CultureInfo.InvariantCulture),
            System.Convert.ToInt64(counts.Processing, CultureInfo.InvariantCulture));
    }

    public async IAsyncEnumerable<BatchItem> Results(string batchId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in client.Messages.Batches.ResultsStreaming(batchId, cancellationToken: ct))
            yield return ToItem(item);
    }

    public async Task<BatchItem> Repair(string model, string system, string user, string previousAnswer, string problem, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = new MessageCreateParamsSystem(SystemBlocks(system)),
            Messages =
            [
                new() { Role = Role.User, Content = user },
                new() { Role = Role.Assistant, Content = string.IsNullOrWhiteSpace(previousAnswer) ? "{}" : previousAnswer },
                new() { Role = Role.User, Content = TranslationPrompt.RepairInstruction(problem) },
            ],
        };
        var response = await client.Messages.Create(parameters, cancellationToken: ct);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var usage = response.Usage;
        return new BatchItem("repair", text, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
    }

    /// <summary>The system prefix is one cached block, so every request in the batch reads it rather than paying for it.</summary>
    private static List<TextBlockParam> SystemBlocks(string system) =>
        [new() { Text = system, CacheControl = new CacheControlEphemeral() }];

    private static BatchItem ToItem(Batches.MessageBatchIndividualResponse item)
    {
        var value = item.Result.Value;
        if (value is Batches.MessageBatchSucceededResult ok)
        {
            var text = string.Concat(ok.Message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            var usage = ok.Message.Usage;
            return new BatchItem(item.CustomID, text, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
        }

        return new BatchItem(item.CustomID, string.Empty, 0, 0, 0, 0, "batch result: " + (value?.GetType().Name ?? "none"));
    }
}
