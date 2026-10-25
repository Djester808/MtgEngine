using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>One coverage policy for projected badges and the API's manual-action guard.</summary>
public static class RulesCoverage
{
    /// <summary>
    /// Literal tokens carry their complete rules in the generated definition, not in the
    /// admitted deck's oracle-id set. Copies with an oracle id retain that card's coverage.
    /// An ordinary card with an empty id does not gain coverage by being unidentified.
    /// </summary>
    public static bool IsEnforced(GameObject obj, Func<string, bool> oracleEnforced) =>
        (obj.IsToken && obj.Definition.OracleId.Length == 0) || oracleEnforced(obj.Definition.OracleId);
}
