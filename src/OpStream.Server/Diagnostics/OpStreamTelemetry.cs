using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpStream.Server.Diagnostics;

/// <summary>
/// Single entry point for OpStream's OpenTelemetry instruments.
/// <para>
/// All spans, counters, and histograms emitted by the library originate here.
/// External hosts register them through <c>builder.AddOpStreamTelemetry()</c>
/// (in the <c>OpStream.Aspire</c> package) or by adding
/// <c>ActivitySource = "OpStream"</c> and <c>Meter = "OpStream"</c> to their
/// own OpenTelemetry pipeline.
/// </para>
/// </summary>
public static class OpStreamTelemetry
{
    /// <summary>Universal service name used as the source of metrics and traces.</summary>
    public const string ServiceName = "OpStream";

    /// <summary>Telemetry schema version. Bumped whenever the meaning of a metric changes.</summary>
    public const string Version = "0.1.0";

    // ─── Traces ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Activity source used by every OpStream span. Add it to your tracer pipeline:
    /// <c>tracerProviderBuilder.AddSource(OpStreamTelemetry.ServiceName)</c>.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, Version);

    // ─── Metrics ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Meter used by every OpStream instrument. Add it to your metrics pipeline:
    /// <c>meterProviderBuilder.AddMeter(OpStreamTelemetry.ServiceName)</c>.
    /// </summary>
    public static readonly Meter Meter = new(ServiceName, Version);

    // Counters ----------------------------------------------------------------

    /// <summary>Number of documents currently held in memory by this node.</summary>
    public static readonly UpDownCounter<int> ActiveDocuments =
        Meter.CreateUpDownCounter<int>(
            "opstream.active_documents",
            unit: "{documents}",
            description: "Number of documents currently held in memory.");

    /// <summary>Total operations successfully applied since process start.</summary>
    public static readonly Counter<long> OperationsProcessed =
        Meter.CreateCounter<long>(
            "opstream.ops_processed_total",
            unit: "{operations}",
            description: "Total operations successfully applied across all documents.");

    /// <summary>Operations rejected (by validators, authorization, or protocol).</summary>
    public static readonly Counter<long> OperationsRejected =
        Meter.CreateCounter<long>(
            "opstream.ops_rejected_total",
            unit: "{operations}",
            description: "Total operations rejected by validators, authorization, or protocol checks.");

    // Histograms --------------------------------------------------------------

    /// <summary>End-to-end latency of <c>DocumentSession.ApplyOpAsync</c>, in milliseconds.</summary>
    public static readonly Histogram<double> ApplyLatency =
        Meter.CreateHistogram<double>(
            "opstream.op_apply_latency_ms",
            unit: "ms",
            description: "Wall-clock time to apply an operation, including transform/store/broadcast.");

    /// <summary>
    /// How many concurrent ops had to be rebased before the incoming op could be applied
    /// (the cost of contention).
    /// </summary>
    public static readonly Histogram<int> TransformCountPerOp =
        Meter.CreateHistogram<int>(
            "opstream.transform_count_per_op",
            unit: "{transforms}",
            description: "Number of concurrent ops rebased against the incoming op before apply.");

    /// <summary>Number of peers notified when an op was broadcast.</summary>
    public static readonly Histogram<int> BroadcastFanout =
        Meter.CreateHistogram<int>(
            "opstream.broadcast_fanout",
            unit: "{peers}",
            description: "Number of peers notified per broadcast.");

    /// <summary>Active peers per document, sampled when an op is applied.</summary>
    public static readonly Histogram<int> PeersPerDocument =
        Meter.CreateHistogram<int>(
            "opstream.peers_per_document",
            unit: "{peers}",
            description: "Active peers per document, sampled on every applied operation.");

    /// <summary>Latency of <c>IDocumentStore.AppendOpAsync</c>, in milliseconds.</summary>
    public static readonly Histogram<double> StoreAppendLatency =
        Meter.CreateHistogram<double>(
            "opstream.store.append_latency_ms",
            unit: "ms",
            description: "Latency of appending an op to the document store.");

    /// <summary>Latency of read operations against the store (snapshot or op stream).</summary>
    public static readonly Histogram<double> StoreReadLatency =
        Meter.CreateHistogram<double>(
            "opstream.store.read_latency_ms",
            unit: "ms",
            description: "Latency of reading from the document store (snapshot or op stream).");

    /// <summary>Latency of <c>IBackplane.PublishAsync</c>, in milliseconds.</summary>
    public static readonly Histogram<double> BackplanePublishLatency =
        Meter.CreateHistogram<double>(
            "opstream.backplane.publish_latency_ms",
            unit: "ms",
            description: "Latency of publishing a backplane message.");

    /// <summary>
    /// Records the time that elapsed since <paramref name="startTimestamp"/> on the supplied histogram.
    /// </summary>
    /// <param name="histogram">Target histogram instrument.</param>
    /// <param name="startTimestamp">Result of <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="tags">Optional dimensional tags.</param>
    public static void RecordElapsedMs(
        this Histogram<double> histogram,
        long startTimestamp,
        params KeyValuePair<string, object?>[] tags)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        histogram.Record(elapsedMs, tags);
        OpStreamEventSource.Log.RecordHistogramSample(histogram.Name, elapsedMs);
    }
}
