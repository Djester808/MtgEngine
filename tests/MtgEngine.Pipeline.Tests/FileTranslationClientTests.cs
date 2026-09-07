using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The offline transport: a batch is a directory of request files, an answer a file beside them.
/// Nothing here reaches the network; a translator on the subscription writes the answers.
/// </summary>
public sealed class FileTranslationClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mtg-file-client-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_batch_is_written_as_files_and_ends_when_every_answer_exists()
    {
        var client = new FileTranslationClient(_root);

        var batchId = await client.SubmitBatch("model-a", [("card-1", "the system text", "translate card 1"), ("card-2", "the system text", "translate card 2")], CancellationToken.None);

        var directory = client.Directory(batchId);
        Assert.Equal("the system text", File.ReadAllText(Path.Combine(directory, FileTranslationClient.SystemFile)));
        Assert.Equal("translate card 2", File.ReadAllText(Path.Combine(directory, FileTranslationClient.RequestsDirectory, "card-2.md")));
        Assert.False((await client.Poll(batchId, CancellationToken.None)).Ended);
        Assert.Equal(new[] { "card-1", "card-2" }, client.Missing(batchId));

        File.WriteAllText(Path.Combine(directory, FileTranslationClient.AnswersDirectory, "card-1.json"), "{\"kind\":\"one\"}");
        var half = await client.Poll(batchId, CancellationToken.None);
        Assert.False(half.Ended);
        Assert.Equal(1, half.Succeeded);
        Assert.Equal(1, half.Processing);

        File.WriteAllText(Path.Combine(directory, FileTranslationClient.AnswersDirectory, "card-2.json"), "{\"kind\":\"two\"}");
        Assert.True((await client.Poll(batchId, CancellationToken.None)).Ended);

        var items = new List<BatchItem>();
        await foreach (var item in client.Results(batchId, CancellationToken.None))
            items.Add(item);
        Assert.Equal(new[] { "card-1", "card-2" }, items.Select(i => i.CustomId));
        Assert.Equal("{\"kind\":\"two\"}", items[1].Text);
        Assert.All(items, i => Assert.Null(i.Error));
    }

    [Fact]
    public async Task A_missing_answer_is_an_error_item_and_repair_is_not_offered()
    {
        var client = new FileTranslationClient(_root);
        var batchId = await client.SubmitBatch("model-b", [("card-9", "system", "user")], CancellationToken.None);

        var items = new List<BatchItem>();
        await foreach (var item in client.Results(batchId, CancellationToken.None))
            items.Add(item);
        Assert.NotNull(Assert.Single(items).Error);

        var repair = await client.Repair("model-b", "system", "user", "previous", "problem", CancellationToken.None);
        Assert.NotNull(repair.Error);
    }

    [Fact]
    public async Task Batches_are_numbered_per_root_so_two_models_never_share_a_directory()
    {
        var client = new FileTranslationClient(_root);

        var first = await client.SubmitBatch("model-a", [("card-1", "s", "u")], CancellationToken.None);
        var second = await client.SubmitBatch("model-b", [("card-1", "s", "u")], CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.NotEqual(client.Directory(first), client.Directory(second));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
