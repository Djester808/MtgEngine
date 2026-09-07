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
                var local = args.Contains("--local", StringComparer.Ordinal);
                var positionals = Positionals(args);

                // --local --next N: the next slice of the corpus, one local batch per model under
                // <outDir>/local, skipping every face that has a card file or was ever submitted.
                // The out directory is .agents/slices/001, 002, ... unless given; its name scopes
                // the batch ids so two slices never share one in the store.
                if (Flag(args, "--next") is { } next)
                {
                    if (!local)
                    {
                        Console.Error.WriteLine("pilot-submit --next needs --local: corpus slices run on the file transport, never the API.");
                        return 64;
                    }

                    var sliceDir = positionals.Count > 0 ? positionals[0] : Pilot.NextSliceDirectory(Path.Combine(root, ".agents", "slices"));
                    var sliceEnv = PilotEnvironment.Local(root, sliceDir, scope: Path.GetFileName(Path.TrimEndingDirectorySeparator(sliceDir)));
                    var slice = Pilot.Next(
                        PilotEnvironment.DeckCandidates(root),
                        int.Parse(next, System.Globalization.CultureInfo.InvariantCulture),
                        Pilot.Done(sliceEnv));
                    Console.WriteLine($"slice: {slice.Count} faces selected (free slice, filed and already-submitted faces skipped)");
                    return await Pilot.SubmitAsync(sliceEnv, slice, sliceDir, cts.Token, merge: true);
                }

                var count = positionals.Count > 0 ? int.Parse(positionals[0], System.Globalization.CultureInfo.InvariantCulture) : 200;
                var outDir = positionals.Count > 1 ? positionals[1] : Path.Combine(root, ".agents", "pilot");
                var cards = Pilot.Select(PilotEnvironment.DeckCandidates(root), count);
                Console.WriteLine($"pilot: {cards.Count} faces selected (free slice skipped)");
                var pilotEnv = local ? PilotEnvironment.Local(root, outDir) : PilotEnvironment.ForRepo(root, repair: true);
                return await Pilot.SubmitAsync(pilotEnv, cards, outDir, cts.Token);
            }

        case "pilot-collect":
            {
                var outDir = Positional(args, 1) ?? Path.Combine(root, ".agents", "pilot");
                var repair = !args.Contains("--no-repair", StringComparer.Ordinal);
                var collectEnv = args.Contains("--local", StringComparer.Ordinal) ? PilotEnvironment.Local(root, outDir) : PilotEnvironment.ForRepo(root, repair);
                return await Pilot.CollectAsync(collectEnv, outDir, cts.Token, again: args.Contains("--again", StringComparer.Ordinal));
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
                // subscription to answer; pilot-collect --local reads the answers back.
                // --model <label>: one local batch for that model only, recorded beside the
                // submission's other batches, so a third reading of the disputed cards is one command.
                var local = args.Contains("--local", StringComparer.Ordinal);
                var model = Flag(args, "--model");
                if (model is not null && !local)
                {
                    Console.Error.WriteLine("retranslate-submit --model needs --local: a single-model batch is a translator's, never the API's.");
                    return 64;
                }

                var submitEnv = local ? PilotEnvironment.Local(root, outDir) : PilotEnvironment.ForRepo(root, repair: true);
                return model is null
                    ? await Pilot.SubmitAsync(submitEnv, batch.Cards, outDir, cts.Token)
                    : await Pilot.SubmitAsync(submitEnv, batch.Cards, outDir, cts.Token, models: [model], merge: true);
            }

        // A translator's self-check over one local batch: the collect step's parse and lint, files only.
        case "answers-check":
            {
                var batchDir = Positional(args, 1);
                if (batchDir is null)
                {
                    Console.Error.WriteLine("answers-check needs the local batch directory");
                    return 64;
                }

                var raw = Corpus.LoadRaw(Corpus.DefaultPath(root));
                return AnswersCheckCommand.Run(batchDir, id => Corpus.Card(raw, id), Console.Out);
            }

        // The system text a translator reads (the prompt, the one-spelling rules and the vocabulary
        // reference), so a local batch's system.md can be refreshed after the vocabulary moves.
        case "prompt":
            Console.Out.Write(TranslationPrompt.SystemWithVocabulary());
            return 0;

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
                "usage: pipeline reference | schema | select [count] | pilot-submit [count] [outDir] [--local] | pilot-submit --local --next <n> [outDir] | pilot-collect [outDir] [--no-repair] [--local] [--again]"
                + " | verify [cardsDir] [--only <oracleId>] | rulings-submit [cardsDir] [outDir] | rulings-collect [outDir]"
                + " | retranslate-submit [cardsDir] [outDir] [--only disputed,unsupported,failed,all] [--dry-run] [--local] [--model <label>] | status [cardsDir] | answers-check <batchDir>"
            + " (--local: answers from files under <outDir>/local, never the API; --again on pilot-collect: read every answer file again after translators replaced them;"
            + " --next <n> on pilot-submit: the next n deck faces without a card file or a batch, into .agents/slices/<n>; --model on retranslate-submit: one batch for that model, beside the others)");
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

/// <summary>Every argument after the command that is neither a flag nor a flag's value, in order: what a command with switches before its positionals reads.</summary>
static IReadOnlyList<string> Positionals(string[] argv)
{
    var found = new List<string>();
    for (var i = 1; i < argv.Length; i++)
    {
        if (argv[i] is "--next" or "--only" or "--model")
        {
            i++;
            continue;
        }

        if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            found.Add(argv[i]);
    }

    return found;
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
