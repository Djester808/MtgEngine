namespace MtgEngine.Rules.State;

/// <summary>A particular deck card for this game. Server-only; survives changes of rules object.</summary>
public readonly record struct CardInstanceId(int Value);

/// <summary>An object in the game, CR 109.1. Stable for the object's life in a zone; a zone change makes a new object, CR 400.7.</summary>
public readonly record struct ObjectId(int Value)
{
    public override string ToString() => $"#{Value}";
}

/// <summary>A zero-based seat within one game, not an account identity or a card/object identity.</summary>
public readonly record struct PlayerId(int Value)
{
    public override string ToString() => $"P{Value}";
}

/// <summary>Where an object is. Shared zones have no player; a player's zones name one.</summary>
public readonly record struct ZoneKey(Cards.Zone Zone, PlayerId? Player)
{
    public static ZoneKey Shared(Cards.Zone zone) => new(zone, null);

    public static ZoneKey Of(Cards.Zone zone, PlayerId player) => new(zone, player);
}

/// <summary>A pending question's identity, so an answer can be matched to what was asked.</summary>
public readonly record struct ChoiceId(int Value);
