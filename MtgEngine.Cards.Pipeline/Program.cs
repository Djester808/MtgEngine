using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline;

var root = FindRepoRoot();
var command = args.Length > 0 ? args[0] : "help";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (command)
{
    case "reference":
        Console.Write(VocabularyReference.Text());
        return 0;

    case "schema":
        Console.Write(CardFileSerializer.DefinitionSchema().ToJsonString(new() { WriteIndented = true }));
        return 0;

    case "select":
        {
            var count = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 200;
            var raw = Corpus.LoadRaw(Corpus.DefaultPath(root));
            var ids = Corpus.DeckOracleIds(Path.Combine(root, "MtgEngine.Api", "mtgengine.db"));
            var chosen = 0;
            foreach (var id in ids)
            {
                var card = Corpus.Card(raw, id);
                if (card is null || card.IsFreeSlice)
                    continue;
                Console.WriteLine($"{card.OracleId}  {card.Name}  [{string.Join(", ", card.Keywords)}]  tokens={card.Tokens.Count}");
                if (++chosen == count)
                    break;
            }

            return 0;
        }

    // Two commands, not one: a batch takes hours, and a process that waits for it holds the
    // pipeline dll and the corpus for as long as it waits. Submit exits; collect is run again
    // later and exits 3 while the batches are still going.
    case "pilot-submit":
        {
            var count = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 200;
            var outDir = args.Length > 2 ? args[2] : Path.Combine(root, ".agents", "pilot");
            var cards = Pilot.Select(PilotEnvironment.DeckCandidates(root), count);
            Console.WriteLine($"pilot: {cards.Count} faces selected (free slice skipped)");
            return await Pilot.SubmitAsync(PilotEnvironment.ForRepo(root, repair: true), cards, outDir, cts.Token);
        }

    case "pilot-collect":
        {
            var outDir = args.Length > 1 ? args[1] : Path.Combine(root, ".agents", "pilot");
            var repair = !args.Contains("--no-repair", StringComparer.Ordinal);
            return await Pilot.CollectAsync(PilotEnvironment.ForRepo(root, repair), outDir, cts.Token);
        }

    default:
        Console.Error.WriteLine("usage: pipeline reference | schema | select [count] | pilot-submit [count] [outDir] | pilot-collect [outDir] [--no-repair]");
        return 64;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MtgEngine.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Run from inside the repository.");
}
