namespace MtgEngine.Api.Services.Community;

/// <summary>Shared by Explore and deck suggestions so simultaneous visitors respect upstream pacing.</summary>
public sealed class EdhrecRequestGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public DateTimeOffset LastStarted { get; set; }
    public void Dispose() => Semaphore.Dispose();
}

public sealed class EdhrecRequestHandler(EdhrecRequestGate gate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var delay = gate.LastStarted.AddMilliseconds(500) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            gate.LastStarted = DateTimeOffset.UtcNow;
            return await base.SendAsync(request, cancellationToken);
        }
        finally { gate.Semaphore.Release(); }
    }
}
