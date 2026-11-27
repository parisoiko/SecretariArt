namespace SecretariArt.Services;

// How many timetables may be generated at once across the server ("Generation:MaxConcurrent", default 2). Each run
// keeps several cores busy for up to the time limit; the per-person rate limit alone wouldn't stop many schools
// generating at the same moment. A singleton, so every request shares the same slots.
public sealed class GenerationSlots(IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, configuration.GetValue("Generation:MaxConcurrent", 2)));

    private readonly TimeSpan _wait = TimeSpan.FromSeconds(configuration.GetValue("Generation:WaitSeconds", 30.0));

    // Waits a while ("Generation:WaitSeconds", default 30) for a free slot; false when none came free.
    public Task<bool> WaitAsync(CancellationToken cancellationToken) => _slots.WaitAsync(_wait, cancellationToken);

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
