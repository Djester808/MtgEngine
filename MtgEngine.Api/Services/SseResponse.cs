using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgEngine.Api.Services;

/// <summary>Shared SSE framing and safe terminal errors for AI responses.</summary>
internal static class SseResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task RunAsync<T>(HttpResponse response, Func<SuggestionEmit, Task<T>> run,
        string failureMessage, CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        async Task Emit(string name, object payload)
        {
            await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
        try
        {
            await Emit("final", (await run(Emit))!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            try
            { await Emit("error", new { message = failureMessage }); }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
}
