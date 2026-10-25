using System.Globalization;
using System.Text.Json;

namespace MtgEngine.Cards;

public static partial class PrintedCardMapping
{
    /// <summary>Printed characteristics only. Rules text is preserved without executable interpretation.</summary>
    public static Face? Face(string name, FaceRole role, string typeLine, string? manaCost, string? oracleText,
        string? power, string? toughness, string? loyalty, string? defense, Color? colorIndicator = null)
    {
        if (string.IsNullOrWhiteSpace(name) || ParseTypeLine(typeLine) is not { } types)
            return null;
        return new Face(name, role, types, Optional(manaCost), Stat(power), Stat(toughness), Optional(loyalty),
            int.TryParse(defense, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null,
            colorIndicator, OracleText: Optional(oracleText));
    }

    /// <summary>Reads the catalog's original face data without flattening layouts or translating rules text.</summary>
    public static CardDefinition? Read(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object || Text(source, "oracle_id") is not { Length: > 0 } id
            || Text(source, "name") is not { Length: > 0 } name || Text(source, "layout") is not { } layoutName)
            return null;
        var layout = LayoutOf(layoutName);
        if (layout == Layout.Other)
            return null;
        var sources = source.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array
            ? faces.EnumerateArray().ToArray() : [source];
        if (sources.Length == 0)
            return null;
        var expected = layout is Layout.Split or Layout.Flip or Layout.Transform or Layout.ModalDfc
            or Layout.Adventure or Layout.Battle or Layout.Omen ? 2 : 1;
        if (sources.Length != expected)
            return null;
        var result = new List<Face>();
        for (var index = 0; index < sources.Length; index++)
        {
            var item = sources[index];
            if (item.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryIndicator(item, out var indicator))
                return null;
            if (Face(Text(item, "name") ?? string.Empty, RoleOf(layoutName, index), Text(item, "type_line") ?? string.Empty,
                Text(item, "mana_cost"), Text(item, "oracle_text"), Text(item, "power"), Text(item, "toughness"),
                Text(item, "loyalty"), Text(item, "defense"), indicator) is not { } face)
                return null;
            result.Add(face);
        }
        return new CardDefinition(id, name, layout, result.ToArray());
    }

    private static bool TryIndicator(JsonElement source, out Color? indicator)
    {
        indicator = null;
        if (!source.TryGetProperty("color_indicator", out var colors))
            return true;
        if (colors.ValueKind != JsonValueKind.Array)
            return false;
        var result = Color.Colorless;
        foreach (var value in colors.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                return false;
            var color = value.GetString() switch
            {
                "W" => Color.White,
                "U" => Color.Blue,
                "B" => Color.Black,
                "R" => Color.Red,
                "G" => Color.Green,
                _ => Color.Colorless,
            };
            if (color == Color.Colorless)
                return false;
            result |= color;
        }
        indicator = result;
        return true;
    }

    private static string? Text(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static PrintedStat? Stat(string? value) => Optional(value) is { } printed ? new PrintedStat(printed) : null;
}
