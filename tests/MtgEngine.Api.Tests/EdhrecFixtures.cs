using System.Text.Json;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The EDHREC pages recorded from the live site on 2026-09-21, trimmed to a few cards per list with
/// every key of a card view and every top-level key kept. Nothing in the suite fetches from the
/// network: an upstream shape is a thing to record, not a thing to ask for while testing.
/// </summary>
internal static class EdhrecFixtures
{
    public const string Commander = "commanders_atraxa-praetors-voice.json";
    public const string CommanderBudget = "commanders_atraxa-praetors-voice_budget.json";
    public const string CommanderTheme = "commanders_atraxa-praetors-voice_infect.json";
    public const string Card = "cards_sol-ring.json";

    /// <summary>
    /// A card almost nobody plays, so its figures read nothing like Sol Ring's: 170,762 of
    /// 4,958,954 decks, salt 0.2249. It is the page that proves a card route answers about the
    /// card it was asked for rather than about whichever one a fake was left serving.
    /// </summary>
    public const string OtherCard = "cards_thrummingbird.json";

    public const string AverageDeck = "average-decks_atraxa-praetors-voice.json";

    /// <summary>
    /// <c>pages/commanders/year.json</c> recorded whole on 2026-10-07: "Past 2 Years", a hundred
    /// cardviews ranked 1 to 100, Y'shtola first at 56,157 decks.
    /// </summary>
    public const string TopCommanders = "commanders_year.json";

    /// <summary>
    /// The body EDHREC actually returns for a page it does not publish: HTTP 403 from Amazon S3
    /// with an <c>AccessDenied</c> code, recorded from
    /// <c>pages/commanders/nonexistent-commander-zz.json</c>.
    /// </summary>
    public const string AccessDenied = "commanders_nonexistent-commander-zz.403.xml";

    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "edhrec", name));

    public static JsonDocument Json(string name) => JsonDocument.Parse(Text(name));
}
