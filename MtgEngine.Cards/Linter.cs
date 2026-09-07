using System.Collections;
using System.Reflection;

namespace MtgEngine.Cards;

public enum LintSeverity
{
    Warning,
    Error,
}

public sealed record LintFinding(LintSeverity Severity, string Code, string Message, string Path);

public sealed record LintReport(IReadOnlyList<LintFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == LintSeverity.Error);
}

/// <summary>
/// The checks a card file must pass before it counts as translated. Structural, not
/// semantic: the runner decides whether a card plays right; this decides whether it can be
/// asked to. Each check has a code so a report can be counted by cause.
/// </summary>
public static class Linter
{
    /// <summary>Keywords whose printed form always carries a parameter, and which one.</summary>
    private static readonly IReadOnlyDictionary<KeywordKind, string> RequiredParameter = new Dictionary<KeywordKind, string>
    {
        [KeywordKind.Equip] = "Cost",
        [KeywordKind.Cycling] = "Cost",
        [KeywordKind.Kicker] = "Cost",
        [KeywordKind.Ward] = "Cost",
        [KeywordKind.Flashback] = "Cost",
        [KeywordKind.Buyback] = "Cost",
        [KeywordKind.Echo] = "Cost",
        [KeywordKind.Madness] = "Cost",
        [KeywordKind.Morph] = "Cost",
        [KeywordKind.Ninjutsu] = "Cost",
        [KeywordKind.Transmute] = "Cost",
        [KeywordKind.Unearth] = "Cost",
        [KeywordKind.Evoke] = "Cost",
        [KeywordKind.Prowl] = "Cost",
        [KeywordKind.Reinforce] = "Cost",
        [KeywordKind.Retrace] = "Cost",
        [KeywordKind.LevelUp] = "Cost",
        [KeywordKind.Overload] = "Cost",
        [KeywordKind.Scavenge] = "Cost",
        [KeywordKind.Bestow] = "Cost",
        [KeywordKind.Outlast] = "Cost",
        [KeywordKind.Dash] = "Cost",
        [KeywordKind.Awaken] = "Cost",
        [KeywordKind.Surge] = "Cost",
        [KeywordKind.Emerge] = "Cost",
        [KeywordKind.Escalate] = "Cost",
        [KeywordKind.Embalm] = "Cost",
        [KeywordKind.Eternalize] = "Cost",
        [KeywordKind.JumpStart] = "Cost",
        [KeywordKind.Spectacle] = "Cost",
        [KeywordKind.Escape] = "Cost",
        [KeywordKind.Mutate] = "Cost",
        [KeywordKind.Encore] = "Cost",
        [KeywordKind.Foretell] = "Cost",
        [KeywordKind.Disturb] = "Cost",
        [KeywordKind.Cleave] = "Cost",
        [KeywordKind.Reconfigure] = "Cost",
        [KeywordKind.Blitz] = "Cost",
        [KeywordKind.Prototype] = "Cost",
        [KeywordKind.Craft] = "Cost",
        [KeywordKind.Disguise] = "Cost",
        [KeywordKind.Plot] = "Cost",
        [KeywordKind.Impending] = "Cost",
        [KeywordKind.Exhaust] = "Cost",
        [KeywordKind.Warp] = "Cost",
        [KeywordKind.Fortify] = "Cost",
        [KeywordKind.Recover] = "Cost",
        [KeywordKind.Transfigure] = "Cost",
        [KeywordKind.AuraSwap] = "Cost",
        [KeywordKind.CumulativeUpkeep] = "Cost",
        [KeywordKind.Suspend] = "Cost",
        [KeywordKind.Bushido] = "N",
        [KeywordKind.Rampage] = "N",
        [KeywordKind.Annihilator] = "N",
        [KeywordKind.Crew] = "N",
        [KeywordKind.Modular] = "N",
        [KeywordKind.Renown] = "N",
        [KeywordKind.Afflict] = "N",
        [KeywordKind.Fabricate] = "N",
        [KeywordKind.Tribute] = "N",
        [KeywordKind.Toxic] = "N",
        [KeywordKind.Backup] = "N",
        [KeywordKind.Saddle] = "N",
        [KeywordKind.Devour] = "N",
        [KeywordKind.Absorb] = "N",
        [KeywordKind.Frenzy] = "N",
        [KeywordKind.Graft] = "N",
        [KeywordKind.Ripple] = "N",
        [KeywordKind.Vanishing] = "N",
        [KeywordKind.Fading] = "N",
        [KeywordKind.Amplify] = "N",
        [KeywordKind.Soulshift] = "N",
        [KeywordKind.Poisonous] = "N",
        [KeywordKind.Bloodthirst] = "N",
        [KeywordKind.Ravenous] = "N",
        [KeywordKind.Squad] = "Cost",
        [KeywordKind.Casualty] = "N",
        [KeywordKind.Offspring] = "Cost",
        [KeywordKind.Protection] = "Of",
        [KeywordKind.Landwalk] = "Of",
        [KeywordKind.Affinity] = "Of",
        [KeywordKind.Champion] = "Of",
        [KeywordKind.Offering] = "Of",
        [KeywordKind.Enchant] = "Of",
        [KeywordKind.Splice] = "Of",
    };

    public static LintReport Check(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var findings = new List<LintFinding>();

        CheckFaces(file, findings);
        CheckScryfallKeywords(file, findings);
        CheckStatus(file, findings);

        var faceIndex = 0;
        foreach (var face in file.Definition.Faces)
        {
            var abilityIndex = 0;
            foreach (var ability in face.AbilityList)
            {
                var path = $"faces[{faceIndex}].abilities[{abilityIndex}]";
                CheckAbility(ability, path, findings);
                abilityIndex++;
            }

            faceIndex++;
        }

        return new LintReport(findings);
    }

    private static void CheckFaces(CardFile file, List<LintFinding> findings)
    {
        if (file.Definition.Faces.Count == 0)
            findings.Add(new(LintSeverity.Error, "L007", "A definition needs at least one face.", "faces"));
        if (file.Definition.Faces.Count > 0 && file.Definition.Faces.All(f => f.Role != FaceRole.Main))
            findings.Add(new(LintSeverity.Error, "L007", "No face has the Main role.", "faces"));
        foreach (var (face, i) in file.Definition.Faces.Select((f, i) => (f, i)))
        {
            if (string.IsNullOrWhiteSpace(face.Name))
                findings.Add(new(LintSeverity.Error, "L007", "A face has no name.", $"faces[{i}].name"));
        }

        if (file.IrVersion != IrVersion.Current)
            findings.Add(new(LintSeverity.Error, "L012", $"IR version {file.IrVersion} is not the current {IrVersion.Current}; migrate the file.", "irVersion"));
    }

    /// <summary>Every keyword Scryfall lists must appear as a keyword ability or a keyword-action effect; extras are warnings.</summary>
    private static void CheckScryfallKeywords(CardFile file, List<LintFinding> findings)
    {
        if (file.KeywordList.Count == 0)
            return;

        var nodes = Walk(file.Definition).ToList();
        var keywords = nodes.OfType<KeywordAbility>().Select(k => k.Keyword)
            .Concat(nodes.OfType<GrantKeyword>().Select(g => g.Keyword))
            .ToHashSet();
        var actions = nodes.Select(n => n.GetType().Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in file.KeywordList)
        {
            var kind = VocabularyNames.Keyword(name);
            if (kind is { } k)
            {
                if (!keywords.Contains(k))
                    findings.Add(new(LintSeverity.Error, "L002", $"Scryfall lists the keyword '{name}' and the definition has no {k} ability.", "scryfallKeywords"));
                continue;
            }

            var action = VocabularyNames.Action(name);
            if (action is { } a && !actions.Contains(a.ToString()) && !(a == KeywordAction.TapAndUntap && (actions.Contains("Tap") || actions.Contains("Untap"))))
                findings.Add(new(LintSeverity.Error, "L002", $"Scryfall lists the keyword action '{name}' and the definition has no {a} effect.", "scryfallKeywords"));
        }

        var listed = file.KeywordList.Select(VocabularyNames.Keyword).OfType<KeywordKind>().ToHashSet();
        foreach (var extra in keywords.Where(k => !listed.Contains(k)))
            findings.Add(new(LintSeverity.Warning, "L002", $"The definition has {extra} and Scryfall does not list it; check it is granted, not printed.", "scryfallKeywords"));
    }

    private static void CheckStatus(CardFile file, List<LintFinding> findings)
    {
        if (file.Status == ImplementationStatus.Verified)
        {
            if (file.Verification is null)
                findings.Add(new(LintSeverity.Error, "L009", "A Verified file must carry a Verification record from the runner.", "status"));
            if (file.ScenarioList.Count == 0)
                findings.Add(new(LintSeverity.Error, "L009", "A Verified file must have at least one scenario.", "status"));
        }

        if (file.Status == ImplementationStatus.Disputed && (file.Candidates is null || file.Candidates.Count < 2))
            findings.Add(new(LintSeverity.Error, "L013", "A Disputed file must keep both readings in Candidates.", "status"));
        if (file.Status == ImplementationStatus.Excluded && file.Source is not ExcludedProvenance)
            findings.Add(new(LintSeverity.Error, "L013", "An Excluded file must say why in its provenance.", "status"));
    }

    private static void CheckAbility(Ability ability, string path, List<LintFinding> findings)
    {
        var nodes = Walk(ability).ToList();

        foreach (var u in nodes.OfType<Unsupported>())
            findings.Add(new(LintSeverity.Error, "L001", $"Unsupported: {u.Reason}", path));

        if (ability is KeywordAbility kw && RequiredParameter.TryGetValue(kw.Keyword, out var parameter))
        {
            var present = parameter switch
            {
                "Cost" => kw.Cost is not null,
                "N" => kw.N is not null,
                "Of" => kw.Of is not null,
                _ => true,
            };
            if (!present)
                findings.Add(new(LintSeverity.Error, "L005", $"{kw.Keyword} needs its {parameter}.", path));
        }

        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (IsConsumer(node))
                continue;
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string))
                    continue;
                if (prop.Name is "RememberAs" or "StoreAs" or "Key" or "StoreChoiceAs" or "RememberHitAs" or "RememberRestAs")
                {
                    if (prop.GetValue(node) is string key && key.Length > 0)
                        produced.Add(key);
                }
            }
        }

        foreach (var node in nodes)
        {
            var key = node switch
            {
                Remembered r => r.Key,
                RememberedPlayer r => r.Key,
                Stored s => s.Key,
                ChosenNumber c => c.Key,
                ChosenPlayer c => c.Key,
                ChoiceIs c => c.Key,
                StoredIs s => s.Key,
                IsChosenName c => c.Key,
                IsChosenType c => c.Key,
                IsChosenColor c => c.Key,
                _ => null,
            };
            if (key is not null && !produced.Contains(key))
                findings.Add(new(LintSeverity.Error, "L003", $"'{key}' is referenced but nothing in this ability produces it.", path));
        }

        foreach (var fixedAmount in nodes.OfType<Fixed>().Where(f => f.N < 0))
            findings.Add(new(LintSeverity.Warning, "L004", $"A negative literal amount ({fixedAmount.N}); use the record's own direction instead.", path));

        foreach (var target in nodes.OfType<Target>().Where(t => t.Count is Fixed { N: 0 }))
            findings.Add(new(LintSeverity.Error, "L008", "A target with a count of zero targets nothing.", path));

        foreach (var counter in nodes.Select(CounterKindOf).OfType<string>().Where(c => !Counters.Known.Contains(c)))
            findings.Add(new(LintSeverity.Warning, "L006", $"Counter kind '{counter}' is not in Counters.Known.", path));

        foreach (var token in nodes.OfType<TokenSpec>().Where(t => string.IsNullOrWhiteSpace(t.Name)))
            findings.Add(new(LintSeverity.Error, "L010", "A token needs a name.", path));
    }

    /// <summary>Records whose Key reads a value an earlier step produced, rather than producing one.</summary>
    private static bool IsConsumer(object node) => node is Remembered or RememberedPlayer or Stored or ChosenNumber
        or ChosenPlayer or ChoiceIs or StoredIs or IsChosenName or IsChosenType or IsChosenColor;

    private static string? CounterKindOf(object node) => node switch
    {
        PutCounters p => p.Counter,
        RemoveCounters r => r.Counter,
        EntersWithCounters e => e.Counter,
        CounterSpec c => c.Counter,
        PutPlayerCounters p => p.Counter,
        CountersOnPlayer c => c.Counter,
        RemoveCountersCost c => c.Counter,
        PutCountersCost c => c.Counter,
        _ => null,
    };

    /// <summary>Every vocabulary record reachable from a root, depth first, including the root.</summary>
    public static IEnumerable<object> Walk(object root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;
                var value = prop.GetValue(node);
                foreach (var child in Children(value))
                    stack.Push(child);
            }
        }
    }

    private static IEnumerable<object> Children(object? value)
    {
        switch (value)
        {
            case null:
            case string:
                yield break;
            case IEnumerable list:
                foreach (var item in list)
                {
                    foreach (var child in Children(item))
                        yield return child;
                }

                yield break;
            default:
                if (value.GetType().Namespace == typeof(Linter).Namespace && !value.GetType().IsEnum && !value.GetType().IsPrimitive)
                    yield return value;
                yield break;
        }
    }
}
