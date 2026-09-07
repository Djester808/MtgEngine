using Anthropic;
using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline;
using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Cards.Pipeline.Translate;
using MtgEngine.Cards.Pipeline.Verify;

var root = FindRepoRoot();
var command = args.Length > 0 ? args[0] : "help";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// A refusal from the Anthropic API (a balance too low, a bad key) is a state to report in
// one line, not a crash: the message is the API's own sentence and carries no secret.
try
{
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
                var collectEnv = args.Contains("--local", StringComparer.Ordinal) ? PilotEnvironment.Local(root, outDir) : PilotEnvironment.ForRepo(root, repair);
                return await Pilot.CollectAsync(collectEnv, outDir, cts.Token);
            }

        // The re-translation round (seam-requests/P14.md): the cards that disagreed or carry an
        // Unsupported marker, never a verified one. --dry-run lists them and costs nothing.
        case "retranslate-submit":
            {
                var cardsDir = Positional(args, 1) ?? Path.Combine(root, "cards");
                var outDir = Positional(args, 2) ?? Path.Combine(root, ".agents", "retranslate");
                if (!RetranslateSelection.TryParse(Flag(args, "--only"), out var kinds, out var error))
                {
                    Console.Error.WriteLine(error ?? "no re-translation kind was named");
                    return 64;
                }

                var selection = RetranslateSelection.Select(CardDirectory.Load(cardsDir), kinds);
                RetranslateSelection.Print(selection, Console.Out);
                if (args.Contains("--dry-run", StringComparer.Ordinal))
                    return 0;

                var batch = RetranslateSelection.Cards(PilotEnvironment.DeckCandidates(root), selection);
                // --local: no API. The requests are written as files for a translator on the
                // subscription to answer;  reads the answers back.
                var submitEnv = args.Contains("--local", StringComparer.Ordinal) ? PilotEnvironment.Local(root, outDir) : PilotEnvironment.ForRepo(root, repair: true);
                return await Pilot.SubmitAsync(submitEnv, batch.Cards, outDir, cts.Token);
            }

        case "status":
            return StatusCommand.Run(Positional(args, 1) ?? Path.Combine(root, "cards"), Console.Out);

        // The verification loop. Nothing here reaches the network: a card is verified by being
        // played, and the engine is what plays it (CARD_ENGINE_PLAN.md, rule 3).
        case "verify":
            {
                var cardsDir = Positional(args, 1) ?? Path.Combine(root, "cards");
                var only = Flag(args, "--only");
                return VerifyCommand.Run(cardsDir, only, Console.Out, cts.Token);
            }

        // The same submit/collect split as the pilot, and for the same reason: a batch takes hours.
        case "rulings-submit":
            {
                var cardsDir = Positional(args, 1) ?? Path.Combine(root, "cards");
                var outDir = Positional(args, 2) ?? Path.Combine(root, ".agents", "rulings");
                return await RulingsCommand.SubmitAsync(RulingsFor(root, cardsDir), cardsDir, outDir, cts.Token);
            }

        case "rulings-collect":
            {
                var outDir = Positional(args, 1) ?? Path.Combine(root, ".agents", "rulings");
                var cardsDir = RulingsSubmission.Read(outDir)?.CardsDirectory ?? Path.Combine(root, "cards");
                return await RulingsCommand.CollectAsync(RulingsFor(root, cardsDir), outDir, cts.Token);
            }

        default:
            Console.Error.WriteLine(
                "usage: pipeline reference | schema | select [count] | pilot-submit [count] [outDir] | pilot-collect [outDir] [--no-repair]"
                + " | verify [cardsDir] [--only <oracleId>] | rulings-submit [cardsDir] [outDir] | rulings-collect [outDir]"
                + " | retranslate-submit [cardsDir] [outDir] [--only disputed,unsupported,failed,all] [--dry-run] [--local] | status [cardsDir]"
            + " (--local on retranslate-submit and pilot-collect: answers from files under <outDir>/local, never the API)");
            return 64;
    }
}
catch (Anthropic.Exceptions.AnthropicException refused)
{
    var reason = System.Text.RegularExpressions.Regex.Match(refused.Message, "\"message\":\"([^\"]+)\"");
    Console.Error.WriteLine("The Anthropic API refused the request: " + (reason.Success ? reason.Groups[1].Value : refused.Message.Split('\n')[0]));
    Console.Error.WriteLine("Nothing was submitted. Add credits at Plans & Billing and run the command again.");
    return 75;
}

/// <summary>A positional argument, or null when the caller gave a flag there or nothing at all.</summary>
static string? Positional(string[] argv, int index) =>
    index < argv.Length && !argv[index].StartsWith("--", StringComparison.Ordinal) ? argv[index] : null;

/// <summary>The value after a named flag, or null when it was not given.</summary>
static string? Flag(string[] argv, string name)
{
    var at = Array.IndexOf(argv, name);
    return at >= 0 && at + 1 < argv.Length ? argv[at + 1] : null;
}

/// <summary>
/// The real rulings environment: the Anthropic transport, <c>cards/</c>, and the corpus — which
/// is not in git, so its absence is a state the command reports rather than a crash.
/// </summary>
static RulingsEnvironment RulingsFor(string repoRoot, string cardsDir)
{
    var corpus = Corpus.DefaultPath(repoRoot);
    var cards = CardDirectory.Load(cardsDir);
    return new RulingsEnvironment(
        new AnthropicTranslationClient(new AnthropicClient { ApiKey = Secrets.AnthropicApiKey() }),
        new CardFileWriter(cardsDir),
        File.Exists(corpus) ? () => RulingsCommand.FromCorpus(corpus, cards) : null,
        [new RulingsStageOptions().Model],
        Console.Out);
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
