using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The three facts about a turn <see cref="TurnHistory"/> does not record yet, asked for in
/// seam-requests/E0c.md: the objects that died and left the battlefield rather than their ids
/// (so morbid can say "a creature" and revolt "a permanent you controlled", CR 603.10), damage
/// totalled per source-and-receiver pair (CR 120.3), and how many times each ability has resolved
/// this turn (CR 603.4).
///
/// Each accessor is bound once, by name and by exact type, against the record this build actually
/// has. It answers false today and starts answering the moment the field lands, so the arms that
/// read it are written once and do not change when the seam does; the guard is what the tests
/// assert against, and the assertions waiting on the seam say so by name. Reflection is deliberate
/// and is the only construct that compiles both before and after a record the query does not own
/// changes the type of a field.
/// </summary>
public static class TurnFacts
{
    private static readonly Func<TurnHistory, ImmutableList<GameObject>>? ReadDied =
        Bind<ImmutableList<GameObject>>(nameof(TurnHistory.Died));

    private static readonly Func<TurnHistory, ImmutableList<GameObject>>? ReadLeftBattlefield =
        Bind<ImmutableList<GameObject>>(nameof(TurnHistory.LeftBattlefield));

    private static readonly Func<TurnHistory, ImmutableDictionary<(ObjectId Source, PlayerId To), int>>? ReadDamagePairs =
        Bind<ImmutableDictionary<(ObjectId Source, PlayerId To), int>>("DamageDealtToPlayer");

    private static readonly Func<TurnHistory, ImmutableDictionary<(ObjectId Source, int AbilityIndex), int>>? ReadResolutions =
        Bind<ImmutableDictionary<(ObjectId Source, int AbilityIndex), int>>("AbilitiesResolvedThisTurn");

    /// <summary>True once the history keeps the departed object and not only its id.</summary>
    public static bool KeepsDepartedObjects => ReadDied is not null && ReadLeftBattlefield is not null;

    /// <summary>True once the history totals damage per source-and-receiver pair.</summary>
    public static bool KeepsDamagePairs => ReadDamagePairs is not null;

    /// <summary>True once the history counts how many times each ability resolved this turn.</summary>
    public static bool CountsResolutions => ReadResolutions is not null;

    public static bool TryDied(TurnHistory history, [NotNullWhen(true)] out ImmutableList<GameObject>? died) =>
        Try(ReadDied, history, out died);

    public static bool TryLeftBattlefield(TurnHistory history, [NotNullWhen(true)] out ImmutableList<GameObject>? left) =>
        Try(ReadLeftBattlefield, history, out left);

    public static bool TryDamagePairs(
        TurnHistory history,
        [NotNullWhen(true)] out ImmutableDictionary<(ObjectId Source, PlayerId To), int>? pairs) =>
        Try(ReadDamagePairs, history, out pairs);

    public static bool TryResolutions(
        TurnHistory history,
        [NotNullWhen(true)] out ImmutableDictionary<(ObjectId Source, int AbilityIndex), int>? resolutions) =>
        Try(ReadResolutions, history, out resolutions);

    private static bool Try<T>(Func<TurnHistory, T>? read, TurnHistory history, [NotNullWhen(true)] out T? value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(history);
        value = read?.Invoke(history);
        return value is not null;
    }

    /// <summary>
    /// A getter for a property of exactly that name and exactly that type, or null when the record
    /// does not have it yet. Tuple element names are not part of the runtime type, so the named
    /// tuples above match a field declared with different names.
    /// </summary>
    private static Func<TurnHistory, T>? Bind<T>(string name)
        where T : class
    {
        var property = typeof(TurnHistory).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(T) || property.GetGetMethod() is not { } getter)
            return null;

        return history => (T)getter.Invoke(history, null)!;
    }
}
