using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

internal static class BasicMana
{
    internal static int[] Values(ManaPool mana) => [mana.White, mana.Blue, mana.Black, mana.Red, mana.Green, mana.Colorless];
    private static ManaPool Pool(int[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5]);

    internal static ManaPool Add(ManaPool pool, int color)
    {
        var values = Values(pool);
        if (values.Any(v => v < 0))
            throw new InvalidOperationException("Invalid mana pool.");
        values[color] = checked(values[color] + 1);
        return Pool(values);
    }

    internal static (int[] Colored, int Generic) Cost(string? text)
    {
        if (string.IsNullOrEmpty(text))
            throw new NotSupportedException("A printed mana cost is required.");
        var colored = new int[6];
        var generic = 0;
        for (var i = 0; i < text.Length;)
        {
            var end = text.IndexOf('}', i);
            if (text[i] != '{' || end < 0)
                throw new NotSupportedException("Unsupported mana cost.");
            var symbol = text[(i + 1)..end];
            var color = symbol.Length == 1 ? "WUBRGC".IndexOf(symbol[0]) : -1;
            if (color >= 0)
                colored[color] = checked(colored[color] + 1);
            else if (symbol.Length > 0 && symbol.All(char.IsAsciiDigit) && int.TryParse(symbol, out var number))
                generic = checked(generic + number);
            else
                throw new NotSupportedException("Only fixed generic, colored and colorless costs are supported.");
            i = end + 1;
        }
        return (colored, generic);
    }

    internal static ManaPool Pay(ManaPool pool, ManaPool payment, string? text)
    {
        var (colored, generic) = Cost(text);
        var have = Values(pool);
        var spend = Values(payment);
        if (have.Any(v => v < 0) || spend.Where((v, i) => v < colored[i] || v > have[i]).Any()
            || spend.Sum(v => (long)v) != colored.Sum(v => (long)v) + generic)
            throw new InvalidOperationException("The chosen mana cannot pay this cost exactly.");
        return Pool(have.Select((v, i) => v - spend[i]).ToArray());
    }
}
