using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// How much of the real card corpus the compiler can read.
/// </summary>
/// <remarks>
/// The number that matters for this feature. Card coverage is not something a pass/fail suite can
/// express — every test can be green with eighteen cards implemented — so it is measured against
/// the whole Scryfall oracle corpus and ratcheted: the floor may only go up.
/// <para>
/// It also prints the commonest lines it could not read, in frequency order. That list is the work
/// queue, and it is deliberately part of the test output rather than a separate tool, because the
/// next person to touch the compiler should not have to go looking for what to do next.
/// </para>
/// <para>
/// The corpus is a 194MB bulk file the API downloads at runtime. When it is absent — a clean
/// checkout, or CI without network — these tests skip rather than fail: a missing download is not
/// a regression in the compiler.
/// </para>
/// </remarks>
public sealed class CardCompilerCoverageTests(ITestOutputHelper output)
{
    /// <summary>
    /// The floor: what coverage actually is today, not what it ought to be.
    /// </summary>
    /// <remarks>
    /// Raise it when coverage improves; never lower it to make a build pass. It was first set to
    /// an aspirational 30% before anything had been measured, which failed the build on the day
    /// it was written — a ratchet records what is true and fails only on a regression.
    /// <para>
    /// A ratchet nobody ratchets stops being one. This sat at 23.5% while coverage reached 46.6%,
    /// so it would have watched half the cards stop compiling without failing the build. Set just
    /// under the measured figure: close enough to catch a real regression, with enough slack that
    /// a card the corpus gains does not fail an unrelated commit.
    /// </para>
    /// </remarks>
    private const double MinimumCoverage = 0.470;

    /// <summary>The corpus, or null when the bulk file has not been downloaded.</summary>
    /// <remarks>
    /// Shared with <see cref="CardCompilerWorkQueueTests"/>, which measures the same corpus a
    /// different way. Two copies of the Scryfall reader would drift, and the two numbers only
    /// mean anything next to each other if they came from the same set of cards.
    /// </remarks>
    internal static IReadOnlyList<CardDefinition>? LoadCorpusOrSkip()
    {
        var path = CorpusPath();
        return path is null ? null : LoadCorpus(path);
    }

    private static string? CorpusPath()
    {
        foreach (var dir in new[] { "bulk-data", Path.Combine("..", "..", "..", "..", "..", "MtgEngine.Api", "bin", "Debug", "net10.0", "bulk-data") })
        {
            var path = Path.Combine(AppContext.BaseDirectory, dir, "oracle_cards.json");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static IReadOnlyList<CardDefinition> LoadCorpus(string path)
    {
        var cards = new List<CardDefinition>(40_000);

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim().TrimEnd(',');
            if (trimmed.Length < 2 || trimmed is "[" or "]")
                continue;

            CardDefinition? card;
            try
            {
                card = FromScryfall(JsonDocument.Parse(trimmed).RootElement);
            }
            catch (JsonException)
            {
                continue;
            }

            if (card is not null)
                cards.Add(card);
        }

        return cards;
    }

    /// <summary>Only what the compiler reads: the printed characteristics and the rules text.</summary>
    private static CardDefinition? FromScryfall(JsonElement json)
    {
        var layout = json.TryGetProperty("layout", out var l) ? l.GetString() : null;
        if (layout is "token" or "emblem" or "art_series" or "double_faced_token")
            return null;

        if (!json.TryGetProperty("legalities", out var legal))
            return null;

        var playable = false;
        foreach (var format in legal.EnumerateObject())
        {
            if (format.Value.GetString() is "legal" or "restricted")
            {
                playable = true;
                break;
            }
        }

        if (!playable)
            return null;

        var keywords = KeywordAbility.None;
        if (json.TryGetProperty("keywords", out var kws))
        {
            foreach (var kw in kws.EnumerateArray())
            {
                keywords |= kw.GetString() switch
                {
                    "Flying" => KeywordAbility.Flying,
                    "Reach" => KeywordAbility.Reach,
                    "First strike" => KeywordAbility.FirstStrike,
                    "Double strike" => KeywordAbility.DoubleStrike,
                    "Trample" => KeywordAbility.Trample,
                    "Deathtouch" => KeywordAbility.Deathtouch,
                    "Lifelink" => KeywordAbility.Lifelink,
                    "Vigilance" => KeywordAbility.Vigilance,
                    "Haste" => KeywordAbility.Haste,
                    "Hexproof" => KeywordAbility.Hexproof,
                    "Indestructible" => KeywordAbility.Indestructible,
                    "Menace" => KeywordAbility.Menace,
                    "Flash" => KeywordAbility.Flash,
                    "Shroud" => KeywordAbility.Shroud,
                    "Defender" => KeywordAbility.Defender,
                    "Swampwalk" => KeywordAbility.Swampwalk,
                    "Forestwalk" => KeywordAbility.Forestwalk,
                    "Islandwalk" => KeywordAbility.Islandwalk,
                    "Mountainwalk" => KeywordAbility.Mountainwalk,
                    "Plainswalk" => KeywordAbility.Plainswalk,
                    "Horsemanship" => KeywordAbility.Horsemanship,
                    "Fear" => KeywordAbility.Fear,
                    "Shadow" => KeywordAbility.Shadow,
                    "Intimidate" => KeywordAbility.Intimidate,
                    "Skulk" => KeywordAbility.Skulk,
                    "Flanking" => KeywordAbility.Flanking,
                    "Infect" => KeywordAbility.Infect,
                    "Wither" => KeywordAbility.Wither,
                    "Changeling" => KeywordAbility.Changeling,
                    "Daybound" => KeywordAbility.Daybound,
                    "Nightbound" => KeywordAbility.Nightbound,
                    "Enlist" => KeywordAbility.Enlist,
                    _ => KeywordAbility.None,
                };
            }
        }

        var types = CardType.None;
        var typeLine = json.TryGetProperty("type_line", out var tl) ? tl.GetString() ?? string.Empty : string.Empty;
        foreach (var (word, flag) in TypeWords)
        {
            if (typeLine.Contains(word, StringComparison.OrdinalIgnoreCase))
                types |= flag;
        }

        var colours = ColoursOf(json, "color_identity");
        var itsColours = ColoursOf(json, "colors");
        var rules = OracleTextOf(json);
        keywords |= ProtectionsIn(rules);

        // A card with faces keeps its cost, power and toughness on the faces too, so the front
        // face answers for the card - which is the face that is cast.
        var front = FaceOrSelf(json);

        return new CardDefinition
        {
            OracleId = json.TryGetProperty("oracle_id", out var oid) ? oid.GetString() ?? string.Empty : string.Empty,
            Name = json.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            OracleText = rules,
            CardTypes = types,
            Keywords = keywords,
            ColorIdentity = colours,
            Colors = itsColours,
            ManaCostRaw = Text(front, "mana_cost"),
            Cmc = json.TryGetProperty("cmc", out var cmc) && cmc.TryGetDouble(out var value)
                ? (int)value
                : 0,
            Power = Stat(front, "power"),
            Toughness = Stat(front, "toughness"),
            Subtypes = SubtypesOf(typeLine),
            Faces = FacesOf(json, keywords),
        };
    }

    /// <summary>
    /// The protection flags a card's text names (CR 702.16b).
    /// </summary>
    /// <remarks>
    /// Read from the text because the bulk data cannot say it any other way: its keyword list
    /// carries the bare word "Protection", and which colour is only in the rules line. A card
    /// whose flags did not include the colour failed the check that a keyword line names
    /// keywords the card actually has, so "Flying, protection from red" went unread as a line
    /// even though both halves were understood.
    /// </remarks>
    private static KeywordAbility ProtectionsIn(string oracleText)
    {
        var found = KeywordAbility.None;

        foreach (var (word, flag) in ProtectionWords)
        {
            if (oracleText.Contains(word, StringComparison.OrdinalIgnoreCase))
                found |= flag;
        }

        return found;
    }

    private static readonly (string Word, KeywordAbility Flag)[] ProtectionWords =
    [
        ("protection from white", KeywordAbility.ProtectionFromWhite),
        ("protection from blue", KeywordAbility.ProtectionFromBlue),
        ("protection from black", KeywordAbility.ProtectionFromBlack),
        ("protection from red", KeywordAbility.ProtectionFromRed),
        ("protection from green", KeywordAbility.ProtectionFromGreen),
        ("protection from artifacts", KeywordAbility.ProtectionFromArtifacts),
    ];

    /// <summary>
    /// A card's rules text, including the text that lives on its faces.
    /// </summary>
    /// <remarks>
    /// **853 playable cards keep no rules text at the top level at all** - every transform,
    /// adventure, split, modal double-faced, prepared and flip card - and this loader read only
    /// the top level. They arrived with no text, compiled with nothing unread, and were counted
    /// as fully understood: 853 blank cards inside the coverage figure, none of which the
    /// compiler had ever been shown.
    /// <para>
    /// The game itself never saw them that way. <c>CardParser</c>, which is what builds the card
    /// pool a game is actually played with, has joined the two faces with a <c>//</c> since long
    /// before this; only the instrument was blind, and an instrument that leaves a field blank
    /// makes anything reading that field look implemented. Joined the same way here so the two
    /// agree about what a card says.
    /// </para>
    /// </remarks>
    private static string OracleTextOf(JsonElement json)
    {
        var text = Text(json, "oracle_text");

        if (!json.TryGetProperty("card_faces", out var faces)
            || faces.ValueKind != JsonValueKind.Array)
        {
            return text;
        }

        foreach (var face in faces.EnumerateArray())
        {
            var half = Text(face, "oracle_text");
            if (half.Length == 0)
                continue;

            text = text.Length == 0 ? half : text + "\n//\n" + half;
        }

        return text;
    }

    /// <summary>The front face when the card has faces, and the card itself when it does not.</summary>
    private static JsonElement FaceOrSelf(JsonElement json) =>
        json.TryGetProperty("card_faces", out var faces)
            && faces.ValueKind == JsonValueKind.Array
            && faces.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } front
            && !json.TryGetProperty("mana_cost", out _)
            ? front
            : json;

    /// <summary>
    /// The card's faces, when it has more than one set of characteristics.
    /// </summary>
    /// <remarks>
    /// Each face is read the same way the card is - the difference is only where the fields live.
    /// The keywords come from the card rather than from the face because the bulk data keeps its
    /// keyword list at the top level for both halves; a keyword named on one face is therefore
    /// offered to both, which over-states rather than under-states and is the safer of the two
    /// for a reader whose whole purpose is to notice what it cannot read.
    /// </remarks>
    private static ImmutableList<CardFace> FacesOf(JsonElement json, KeywordAbility keywords)
    {
        if (!json.TryGetProperty("card_faces", out var faces)
            || faces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var read = ImmutableList.CreateBuilder<CardFace>();

        foreach (var face in faces.EnumerateArray())
        {
            if (face.ValueKind != JsonValueKind.Object)
                continue;

            var line = Text(face, "type_line");
            var types = CardType.None;

            foreach (var (word, flag) in TypeWords)
            {
                if (line.Contains(word, StringComparison.OrdinalIgnoreCase))
                    types |= flag;
            }

            read.Add(new CardFace
            {
                Name = Text(face, "name"),
                ManaCostRaw = Text(face, "mana_cost"),
                TypeLine = line,
                CardTypes = types,
                Subtypes = SubtypesOf(line),
                OracleText = Text(face, "oracle_text"),
                Power = Stat(face, "power"),
                Toughness = Stat(face, "toughness"),
                Colors = ColoursOf(face, "colors"),
                Keywords = keywords,
            });
        }

        return read.ToImmutable();
    }

    private static string Text(JsonElement json, string property) =>
        json.TryGetProperty(property, out var found) ? found.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// A printed power or toughness, or null when it is not a plain number.
    /// </summary>
    /// <remarks>
    /// "*" and "1+*" are real printings and are not integers. Null is the honest answer for them:
    /// a card whose power is defined by an ability has no printed number, and inventing a zero
    /// would make every such creature compile as a 0/0 that dies on arrival.
    /// </remarks>
    private static int? Stat(JsonElement json, string property) =>
        int.TryParse(
            Text(json, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// The subtypes on a type line, which are whatever follows the dash (CR 205.3a).
    /// </summary>
    /// <remarks>
    /// Read because a great deal of the compiler asks about them - every tribal lord, every
    /// "target Goblin", and the basic land types the verge lands turn on. A corpus with no
    /// subtypes reports all of it as unread.
    /// </remarks>
    /// <summary>Subtypes that name a mechanic rather than a tribe.</summary>
    private static readonly HashSet<string> MechanicSubtypes =
        new(StringComparer.Ordinal)
        {
            "Class", "Siege", "Background", "Room", "Spacecraft", "Attraction", "Trap",
            "Saga", "Curse", "Vehicle", "Equipment", "Aura", "Adventure", "Lesson", "Case",
        };

    private static ImmutableList<string> SubtypesOf(string typeLine)
    {
        // Only the front face. A two-faced type line carries both halves with "//" between
        // them, and reading past it made "//" a subtype of 642 cards - which is how this was
        // noticed at all, since an instrument that groups by subtype prints the row.
        var front = typeLine.Split("//", StringSplitOptions.None)[0];
        var dash = front.IndexOf('\u2014', StringComparison.Ordinal);
        if (dash < 0)
            return [];

        return
        [
            .. front[(dash + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim()),
        ];
    }

    /// <summary>
    /// One of the two colour lists the bulk data carries, by name.
    /// </summary>
    /// <remarks>
    /// Read because the compiler needs it, not because coverage does: conspire refuses to compile
    /// a colourless spell, so a corpus built without colours reported every conspire card as
    /// unread however well the keyword worked. An instrument that leaves a field blank makes
    /// anything reading that field look unimplemented.
    /// <para>
    /// Both are read because they are different questions. "colors" is what the card <em>is</em>
    /// (CR 202.2) and "color_identity" is what it may be played alongside (CR 903.4); they
    /// disagree on 1,158 nonland cards, and this loader used to answer the first question with
    /// the second field - while citing the rule for the first in its own comment.
    /// </para>
    /// <para>
    /// A card with faces keeps its colours on the faces rather than at the top level, so the
    /// front face answers for the card - which is the face that is cast.
    /// </para>
    /// </remarks>
    private static ImmutableList<ManaColor> ColoursOf(JsonElement json, string field)
    {
        if (!json.TryGetProperty(field, out var colours)
            || colours.ValueKind != JsonValueKind.Array)
        {
            if (json.TryGetProperty("card_faces", out var faces)
                && faces.ValueKind == JsonValueKind.Array
                && faces.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } front)
            {
                return ColoursOf(front, field);
            }

            return [];
        }

        var read = ImmutableList.CreateBuilder<ManaColor>();

        foreach (var colour in colours.EnumerateArray())
        {
            read.Add(colour.GetString() switch
            {
                "W" => ManaColor.White,
                "U" => ManaColor.Blue,
                "B" => ManaColor.Black,
                "R" => ManaColor.Red,
                _ => ManaColor.Green,
            });
        }

        return read.ToImmutable();
    }

    private static readonly (string Word, CardType Flag)[] TypeWords =
    [
        ("Creature", CardType.Creature),
        ("Instant", CardType.Instant),
        ("Sorcery", CardType.Sorcery),
        ("Artifact", CardType.Artifact),
        ("Enchantment", CardType.Enchantment),
        ("Land", CardType.Land),
        ("Planeswalker", CardType.Planeswalker),
    ];

    [Fact]
    public void The_compiler_reads_a_known_share_of_every_playable_card()
    {
        var path = CorpusPath();
        if (path is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping (run the API once to fetch it).");
            return;
        }

        var corpus = LoadCorpus(path);
        Assert.NotEmpty(corpus);

        var complete = 0;
        var totalLines = 0;
        var readLines = 0;
        var oneLineShort = 0;
        var unreadLines = new Dictionary<string, int>(StringComparer.Ordinal);

        // How many *cards* carry each shape, beside how many lines it is. The two diverge hard
        // and in both directions: "creatures you control gain <keyword>" was 192 lines on almost
        // as many cards, while the level-up family is 110 lines on 25 - four or five lines each,
        // because a leveler prints a band per level. A list ranked by lines sent me to size up a
        // mechanic worth twenty-five cards as though it were worth a hundred.
        var unreadCards = new Dictionary<string, int>(StringComparer.Ordinal);
        var soleBlockers = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySubtype = new Dictionary<string, (int Complete, int Total)>(StringComparer.Ordinal);
        var stoppingFamilies = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            var lines = CardCompiler.Lines(card).Count();

            totalLines += lines;
            readLines += lines - compiled.Unhandled.Count;

            foreach (var subtype in card.Subtypes)
            {
                var seen = bySubtype.GetValueOrDefault(subtype);
                bySubtype[subtype] =
                    (seen.Complete + (compiled.IsComplete ? 1 : 0), seen.Total + 1);
            }

            foreach (var family in card.Subtypes.Where(MechanicSubtypes.Contains))
            {
                if (!stoppingFamilies.TryGetValue(family, out var stopping))
                    stoppingFamilies[family] = stopping = new(StringComparer.Ordinal);

                foreach (var line in compiled.Unhandled)
                {
                    var shape = Shape(line);
                    stopping[shape] = stopping.GetValueOrDefault(shape) + 1;
                }
            }

            if (compiled.IsComplete)
            {
                complete++;
                continue;
            }

            if (compiled.Unhandled.Count == 1)
            {
                oneLineShort++;
                var blocker = Shape(compiled.Unhandled[0]);
                soleBlockers[blocker] = soleBlockers.GetValueOrDefault(blocker) + 1;
            }

            var shapesHere = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in compiled.Unhandled)
            {
                var shape = Shape(line);
                unreadLines[shape] = unreadLines.GetValueOrDefault(shape) + 1;
                shapesHere.Add(shape);
            }

            foreach (var shape in shapesHere)
                unreadCards[shape] = unreadCards.GetValueOrDefault(shape) + 1;
        }

        var coverage = (double)complete / corpus.Count;

        // Two numbers, because they say different things. A card counts only when every one of
        // its lines is read, so per-card coverage climbs far more slowly than per-line — and the
        // count of cards that are one line short says how much of the gap is a long tail rather
        // than a few missing templates.
        output.WriteLine($"corpus:     {corpus.Count} playable cards, {totalLines} lines of rules text");
        output.WriteLine($"lines read: {readLines}  ({(double)readLines / totalLines:P1})");
        output.WriteLine($"complete:   {complete}  ({coverage:P1})");
        output.WriteLine($"one line short: {oneLineShort}");
        output.WriteLine(string.Empty);
        // Ranked by cards each would finish, not by how often the line appears. They are not the
        // same list: a line on 900 cards that all have three other unread lines completes none.
        output.WriteLine("templates that would each complete the most cards — the work queue:");
        foreach (var (line, count) in soleBlockers.OrderByDescending(p => p.Value).Take(40))
            output.WriteLine($"  {count,6}  {line}");

        // How steep the queue is, which is the number that decides whether template work is
        // still the right shape of work. Early on the head of this list was worth hundreds of
        // cards each and the answer was obvious. When the head is worth ten and the top hundred
        // between them are worth a few per cent, the remaining cards are not a queue of families
        // any more — they are individual cards wearing a queue's clothing, and the cost per card
        // has stopped falling.
        var ranked = soleBlockers.OrderByDescending(p => p.Value).Select(p => p.Value).ToList();
        var reachable = ranked.Sum();

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"the queue is {ranked.Count} distinct templates covering {reachable} cards "
            + $"({(double)reachable / corpus.Count:P1} of the corpus)");

        foreach (var top in (int[])[10, 100, 1000])
        {
            var got = ranked.Take(top).Sum();
            output.WriteLine(
                $"  top {top,4}: {got,6} cards  ({(double)got / corpus.Count:P1} of the corpus, "
                + $"{(double)got / Math.Max(reachable, 1):P1} of what the queue can reach)");
        }

        // The other list, and it is not a worse version of the first: a family can be large and
        // barely appear above. "Creatures you control gain <keyword> until end of turn" sat at 5
        // in the sole-blocker queue because most cards carrying it had something else unread as
        // well - and reading it moved 138 cards, because those other lines were the *same*
        // family on the same cards. Ranked by lines rather than by cards for that reason: it is
        // the list that shows a template's real size.
        // The third list, and the one that exists because the first two share a blind spot.
        // Both of them rank *lines*, so a mechanic whose cards each carry three or four unread
        // lines at once is invisible to the first (no line is any card's sole blocker) and
        // scattered across the second (each line is different). Sagas were exactly that: 240
        // cards, and the highest any single chapter line reached in the work queue was zero.
        //
        // A subtype is a name the cards share that does not depend on their text, so a family
        // hiding behind a hundred different sentences still shows up here as one row with a bad
        // ratio. It is the list to read when the other two have gone flat.
        output.WriteLine(string.Empty);
        output.WriteLine("least-read subtypes with 20 or more cards — the families text cannot show:");
        foreach (var (subtype, seen) in bySubtype
            .Where(p => p.Value.Total >= 20)
            .OrderBy(p => (double)p.Value.Complete / p.Value.Total)
            .ThenByDescending(p => p.Value.Total)
            .Take(40))
        {
            output.WriteLine(
                $"  {seen.Total - seen.Complete,6} unread of {seen.Total,6}  "
                + $"({(double)seen.Complete / seen.Total:P0} read)  {subtype}");
        }

        // Knowing a family is unread says nothing about what it would take. These are the
        // subtypes that name a *mechanic* rather than a tribe, so the lines under each one are
        // the mechanic's own vocabulary rather than an accident of what those cards happen to
        // say - which is what makes the difference between "30 cards" and "30 cards and here is
        // the shape of the work".
        output.WriteLine(string.Empty);
        output.WriteLine("what stops each unread mechanic:");
        foreach (var (family, stopping) in stoppingFamilies.OrderByDescending(p => p.Value.Count))
        {
            output.WriteLine($"  {family}:");
            foreach (var (line, count) in stopping.OrderByDescending(p => p.Value).Take(6))
                output.WriteLine($"    {count,5}  {line}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("commonest unread lines — lines, then how many cards carry them:");
        foreach (var (line, count) in unreadLines.OrderByDescending(p => p.Value).Take(40))
            output.WriteLine($"  {count,6} {unreadCards.GetValueOrDefault(line),6}  {line}");

        Assert.True(
            coverage >= MinimumCoverage,
            $"Card coverage fell to {coverage:P1}; the floor is {MinimumCoverage:P0}.");
    }

    /// <summary>
    /// How much of a two-faced card the compiler already understands — a report, not a gate.
    /// </summary>
    /// <remarks>
    /// 837 playable cards keep their rules on faces, and they are the single largest thing
    /// standing between the compiler and the corpus: the `//` between the halves is the top of
    /// the unread list by a factor of fourteen, and most of what follows it - daybound, nightbound,
    /// the two "transform this" upkeep triggers, aftermath - is on those same cards.
    /// <para>
    /// **They are not playable and this does not make them playable.** The engine has one set of
    /// characteristics per object and no way to cast one face rather than another. What this
    /// measures is the other half of the question: whether the *words* on those faces are already
    /// within the compiler's reach, so that the work left is the card model and the engine rather
    /// than another two hundred templates.
    /// </para>
    /// </remarks>
    [Fact]
    public void How_much_of_a_two_faced_card_the_compiler_reads_is_reported()
    {
        var corpus = LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var faced = 0;
        var everyFaceRead = 0;
        var facesRead = 0;
        var facesTotal = 0;
        var blockers = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus.Where(c => c.Faces.Count > 0))
        {
            faced++;
            var all = true;

            foreach (var face in card.Faces)
            {
                facesTotal++;
                var compiled = CardCompiler.CompileFace(card, face);

                if (compiled.IsComplete)
                {
                    facesRead++;
                    continue;
                }

                all = false;
                foreach (var line in compiled.Unhandled)
                {
                    var shape = Shape(line);
                    blockers[shape] = blockers.GetValueOrDefault(shape) + 1;
                }
            }

            if (all)
                everyFaceRead++;
        }

        output.WriteLine($"two-faced cards: {faced}");
        output.WriteLine($"faces fully read: {facesRead} of {facesTotal}");
        output.WriteLine($"cards whose every face is read: {everyFaceRead}");
        output.WriteLine(string.Empty);
        output.WriteLine("what stops the rest:");
        foreach (var (line, count) in blockers.OrderByDescending(p => p.Value).Take(20))
            output.WriteLine($"  {count,6}  {line}");

        // A floor, so a loader that stopped reading faces fails here rather than passing by
        // having nothing to look at.
        Assert.True(faced >= 800, $"only {faced} cards arrived with faces; the corpus has 853");
    }

    /// <summary>A line with its numbers and mana symbols blanked, so shapes group together.</summary>
    private static string Shape(string line)
    {
        var shaped = System.Text.RegularExpressions.Regex.Replace(line, @"\b\d+\b", "N");
        shaped = System.Text.RegularExpressions.Regex.Replace(shaped, @"\{[^}]+\}", "{M}");

        // A run of symbols is one cost, and blanking them one at a time filed the same keyword
        // under as many rows as it has printed costs: "Disturb {1}{W}" and "Disturb {3}{U}{U}"
        // became "{M}{M}" and "{M}{M}{M}". Collapsing the run merges 59 families and moves four
        // rows into the top ten - which is to say the queue was recommending the wrong work, not
        // by a little.
        shaped = System.Text.RegularExpressions.Regex.Replace(shaped, @"(\{M\})+", "{M}");

        // The same word said two ways is one shape: "two target creatures" and "3 target
        // creatures" are the same problem, and the digit form was already folded to N.
        shaped = System.Text.RegularExpressions.Regex.Replace(
            shaped,
            @"\b(one|two|three|four|five|six|seven|eight|nine|ten)\b",
            "N",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Long enough to tell the variants apart. At 96 the six shapes of the threaten effect
        // - the plain one, the two that also pump, the one that scries, the one about Goats -
        // collapsed into a single entry of eleven cards, and the plain one had compiled for
        // months. A work queue that merges a solved line with five unsolved ones sends you to
        // read code that already works.
        return shaped.Length > 200 ? shaped[..200] : shaped;
    }
}
