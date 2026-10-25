namespace MtgEngine.Api.Services;

public static class CollectionUndoHeaders
{
    public static T Apply<T>(HttpResponse response, UndoableChange<T> change, Guid[] collections, string kind)
    {
        response.Headers["X-Compleat-Undo"] = change.Token.ToString();
        response.Headers["X-Compleat-Undo-Expires"] = change.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        response.Headers["X-Compleat-Undo-Collections"] = string.Join(",", collections);
        response.Headers["X-Compleat-Undo-Kind"] = kind;
        return change.Value;
    }
}
