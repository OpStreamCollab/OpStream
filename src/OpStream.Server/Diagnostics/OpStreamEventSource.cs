using System.Diagnostics.Tracing;

namespace OpStream.Server.Diagnostics;

/// <summary>
/// Classic <see cref="EventSource"/> + <see cref="EventCounter"/> bridge for tooling
/// that does not consume OpenTelemetry (e.g. <c>dotnet-counters</c>, PerfView,
/// Visual Studio Diagnostic Tools).
///
/// <para>
/// All counters are polled or incrementing counters published once per second.
/// Inspect with: <c>dotnet-counters monitor --name MyApp OpStream</c>.
/// </para>
/// </summary>
[EventSource(Name = "OpStream")]
internal sealed class OpStreamEventSource : EventSource
{
    public static readonly OpStreamEventSource Log = new();

    private long _opsApplied;
    private long _opsRejected;
    private long _activeDocuments;
    private readonly object _histogramLock = new();
    private readonly Dictionary<string, EventCounter> _histogramCounters = new();

    // Lazily-initialised counters keep allocation cost low when no one is listening.
    private IncrementingPollingCounter? _opsAppliedCounter;
    private IncrementingPollingCounter? _opsRejectedCounter;
    private PollingCounter? _activeDocumentsCounter;

    private OpStreamEventSource() { }

    /// <summary>Increments the running total of successfully applied operations.</summary>
    public void OpApplied()
    {
        Interlocked.Increment(ref _opsApplied);
    }

    /// <summary>Increments the running total of rejected operations.</summary>
    public void OpRejected()
    {
        Interlocked.Increment(ref _opsRejected);
    }

    /// <summary>Adjusts the active-documents gauge by <paramref name="delta"/>.</summary>
    public void AdjustActiveDocuments(int delta)
    {
        Interlocked.Add(ref _activeDocuments, delta);
    }

    /// <summary>
    /// Forwards a histogram sample so that the same number is visible to
    /// non-OTel listeners. The first sample for a given <paramref name="name"/>
    /// lazily creates an <see cref="EventCounter"/> behind a lock.
    /// </summary>
    public void RecordHistogramSample(string name, double value)
    {
        if (!IsEnabled()) return;

        EventCounter? counter;
        lock (_histogramLock)
        {
            if (!_histogramCounters.TryGetValue(name, out counter))
            {
                counter = new EventCounter(name, this)
                {
                    DisplayName = name,
                    DisplayUnits = name.EndsWith("ms", StringComparison.Ordinal) ? "ms" : ""
                };
                _histogramCounters[name] = counter;
            }
        }
        counter.WriteMetric(value);
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _opsAppliedCounter ??= new IncrementingPollingCounter(
                "ops-applied-per-second", this, () => Interlocked.Read(ref _opsApplied))
            {
                DisplayName = "Operations Applied",
                DisplayRateTimeScale = TimeSpan.FromSeconds(1)
            };

            _opsRejectedCounter ??= new IncrementingPollingCounter(
                "ops-rejected-per-second", this, () => Interlocked.Read(ref _opsRejected))
            {
                DisplayName = "Operations Rejected",
                DisplayRateTimeScale = TimeSpan.FromSeconds(1)
            };

            _activeDocumentsCounter ??= new PollingCounter(
                "active-documents", this, () => Interlocked.Read(ref _activeDocuments))
            {
                DisplayName = "Active Documents"
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _opsAppliedCounter?.Dispose();
            _opsRejectedCounter?.Dispose();
            _activeDocumentsCounter?.Dispose();
            lock (_histogramLock)
            {
                foreach (var c in _histogramCounters.Values) c.Dispose();
                _histogramCounters.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
