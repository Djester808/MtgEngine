using System.Globalization;

namespace MtgEngine.Cards.Pipeline;

/// <summary>What one run of the template stage did. Notes name the cards it refused and why.</summary>
public sealed record TemplateRun(int Cards, int Faces, int Written, int Refused, IReadOnlyList<string> Notes);

/// <summary>
/// The free slice: the faces the Scryfall JSON alone describes — vanilla, keyword-only, and the
/// six mana patterns. No model reads these. The stage builds the <see cref="CardDefinition"/>
/// directly from the printed fields, so nothing here can be a translation error; it can only be
/// a refusal, and a refused face is left for the translation stage.
///
/// CARD_ENGINE_PLAN.md rule 3: coverage is counted in verified plays, so these files land as
/// <see cref="ImplementationStatus.Translated"/> by default. Whether a template counts as
/// verified without a runner is the coordinator's call, so it is a constructor option rather
/// than a decision this stage makes.
/// </summary>
public sealed class TemplateStage
{
    /// <summary>The engine hash and run id a template's verification carries: it came from no engine and no run.</summary>
    public const string Marker = "template";

    private readonly ImplementationStatus statusWhenClean;
    private readonly DateTimeOffset verifiedAt;

    /// <param name="statusWhenClean">The status a lint-clean template gets. Verified also writes a <see cref="Verification"/>.</param>
    /// <param name="verifiedAt">
    /// The instant a Verified template records. It defaults to the epoch rather than now so a
    /// re-run writes byte-identical files; the coordinator passes the real time if it wants one.
    /// </param>
    public TemplateStage(ImplementationStatus statusWhenClean = ImplementationStatus.Translated, DateTimeOffset? verifiedAt = null)
    {
        this.statusWhenClean = statusWhenClean;
        this.verifiedAt = verifiedAt ?? DateTimeOffset.UnixEpoch;
    }

    /// <summary>Whether every face of a card is a shape this stage can build. The count the plan tracks is over these faces.</summary>
    public static bool Accepts(CorpusCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.Faces.Count > 0 && card.Faces.Select((f, i) => TryFace(f, Templates.RoleOf(card.Layout, i))).All(f => f is not null);
    }

    /// <summary>
    /// One face built from the printed fields, or null when a line is not vanilla, a keyword or a
    /// mana pattern. The type line is parsed, not guessed, and an unknown type word refuses too.
    ///
    /// The line parser is the acceptance test, not <see cref="Templates.IsKeywordOnly"/>: that
    /// check only starts a new keyword after a comma when the next word is capitalised, so it
    /// misses "Flying, vigilance", while a line it admits ("Ward—Pay 3 life.") can still be one
    /// this stage cannot spell. Whether the Scryfall keyword list and the built abilities agree is
    /// the linter's L002 check, at the card level where a two-faced card's keywords live.
    /// </summary>
    public static Face? TryFace(CorpusFace face, FaceRole role)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (Templates.ParseTypeLine(face.TypeLine) is not { } types)
            return null;

        var abilities = new List<Ability>();
        foreach (var line in Lines(face.OracleText))
        {
            if (Templates.ManaLine(line) is { } mana)
            {
                abilities.Add(mana);
                continue;
            }

            if (Templates.KeywordLine(line) is { } keywordAbilities)
            {
                abilities.AddRange(keywordAbilities);
                continue;
            }

            if (!Templates.IsBlank(line))
                return null;
        }

        return new Face(
            face.Name,
            role,
            types,
            string.IsNullOrWhiteSpace(face.ManaCost) ? null : face.ManaCost,
            Stat(face.Power),
            Stat(face.Toughness),
            string.IsNullOrWhiteSpace(face.Loyalty) ? null : face.Loyalty,
            Defense(face.Defense),
            Abilities: abilities.Count == 0 ? null : abilities,
            OracleText: face.OracleText.Length == 0 ? null : face.OracleText);
    }

    /// <summary>The card file for a template card, or null when the card is not one.</summary>
    public CardFile? Build(CorpusCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var faces = new List<Face>();
        for (var i = 0; i < card.Faces.Count; i++)
        {
            if (TryFace(card.Faces[i], Templates.RoleOf(card.Layout, i)) is not { } face)
                return null;
            faces.Add(face);
        }

        if (faces.Count == 0)
            return null;

        var file = new CardFile(
            card.OracleId,
            card.Name,
            card.OracleHash,
            IrVersion.Current,
            new CardDefinition(card.OracleId, card.Name, Templates.LayoutOf(card.Layout), faces),
            ImplementationStatus.Translated,
            new TemplateProvenance(TemplateOf(card)),
            card.Keywords);

        if (statusWhenClean == ImplementationStatus.Verified && !Linter.Check(file).HasErrors)
        {
            file = file with
            {
                Status = ImplementationStatus.Verified,
                Verification = new Verification(Marker, Marker, faces.Count, faces.Count, verifiedAt),
            };
        }

        return file;
    }

    /// <summary>
    /// Writes one file per card into <paramref name="outDir"/>, named by oracle id. A card whose
    /// file does not lint clean is refused with a note rather than written: rule 4, nothing
    /// half-implemented reaches a table.
    /// </summary>
    public TemplateRun Run(IEnumerable<CorpusCard> cards, string outDir)
    {
        ArgumentNullException.ThrowIfNull(cards);
        Directory.CreateDirectory(outDir);
        var notes = new List<string>();
        var written = 0;
        var faces = 0;
        var refused = 0;
        var seen = 0;
        foreach (var card in cards)
        {
            seen++;
            var file = Build(card);
            if (file is null)
            {
                refused++;
                continue;
            }

            var lint = Linter.Check(file);
            if (lint.HasErrors)
            {
                refused++;
                notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: lint {1}",
                    card.Name,
                    string.Join(",", lint.Findings.Where(f => f.Severity == LintSeverity.Error).Select(f => f.Code).Distinct(StringComparer.Ordinal))));
                continue;
            }

            File.WriteAllText(Path.Combine(outDir, card.OracleId + ".json"), CardFileSerializer.Write(file));
            written++;
            faces += file.Definition.Faces.Count;
        }

        return new TemplateRun(seen, faces, written, refused, notes);
    }

    /// <summary>Which of the three templates a card is, for its provenance.</summary>
    public static string TemplateOf(CorpusCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Faces.Any(Templates.IsManaPattern))
            return "mana";
        return card.Faces.All(f => Lines(f.OracleText).All(Templates.IsBlank)) ? "vanilla" : "keyword-only";
    }

    private static IReadOnlyList<string> Lines(string oracleText) =>
        oracleText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static PrintedStat? Stat(string? printed) =>
        string.IsNullOrWhiteSpace(printed) ? null : new PrintedStat(printed);

    private static int? Defense(string? printed) =>
        int.TryParse(printed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
