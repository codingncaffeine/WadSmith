using System.Collections.Concurrent;
using System.Globalization;

namespace WadSmith.App.Canvas;

/// <summary>
/// Frame timings of the map canvas: how long each frame took to draw (render thread) and each map
/// snapshot to take (UI thread). On when WADSMITH_FRAME_STATS=1 or when a tool turns it on.
/// </summary>
internal static class FrameStats
{
    private static readonly ConcurrentQueue<double> Draws = new();
    private static readonly ConcurrentQueue<double> Snapshots = new();

    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("WADSMITH_FRAME_STATS") == "1";

    /// <summary>Whether the last frame was drawn by the GPU (null before the first frame).</summary>
    public static bool? Gpu { get; set; }

    public static void Draw(double ms) => Add(Draws, ms);

    public static void Snapshot(double ms) => Add(Snapshots, ms);

    public static void Reset()
    {
        Draws.Clear();
        Snapshots.Clear();
    }

    /// <summary>"draw p50 x ms, p95 y ms, max z ms over n frames; snapshot …".</summary>
    public static string Report() => $"draw {Describe(Draws)} ({(Gpu switch { true => "GPU", false => "software", null => "no frames" })}); snapshot {Describe(Snapshots)}";

    public static IReadOnlyList<double> DrawTimes => [.. Draws];

    private static void Add(ConcurrentQueue<double> q, double ms)
    {
        if (!Enabled) return;
        q.Enqueue(ms);
        while (q.Count > 4000) q.TryDequeue(out _);
    }

    private static string Describe(ConcurrentQueue<double> q)
    {
        var a = q.ToArray();
        if (a.Length == 0) return "none";
        Array.Sort(a);
        double P(double p) => a[Math.Min(a.Length - 1, (int)(p * a.Length))];
        return string.Create(CultureInfo.InvariantCulture, $"p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, max {a[^1]:0.0} ms over {a.Length}");
    }
}
