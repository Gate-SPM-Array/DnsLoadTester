namespace DnsLoadTester.Core.Stats;

/// <summary>
/// Istogramma log-lineare delle latenze in microsecondi (errore relativo ≤ ~3%), da 1 µs a oltre 60 s.
/// Usato per i percentili per secondo, per minuto e cumulativi. Non thread-safe.
/// </summary>
public sealed class LatencyHistogram
{
    public const int SubBuckets = 32;
    public const int Linear = 64;
    public const int BucketCount = Linear + 30 * SubBuckets;

    private readonly long[] _counts = new long[BucketCount];
    public long Count { get; private set; }
    public long SumUs { get; private set; }
    public long MinUs { get; private set; } = long.MaxValue;
    public long MaxUs { get; private set; }

    public long[] RawCounts => _counts;

    public static int IndexOf(long us)
    {
        if (us < 0) us = 0;
        if (us < Linear) return (int)us;
        int log2 = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)us);   // ≥ 6
        int shift = log2 - 5;                                                          // ≥ 1
        long mant = us >> shift;                                                       // 32..63
        int idx = Linear + (shift - 1) * SubBuckets + (int)(mant - SubBuckets);
        return Math.Min(idx, BucketCount - 1);
    }

    /// <summary>Valore rappresentativo (punto medio) del bucket, in µs.</summary>
    public static double ValueOf(int index)
    {
        if (index < Linear) return index;
        int k = index - Linear;
        int shift = k / SubBuckets + 1;
        long mant = k % SubBuckets + SubBuckets;
        long low = mant << shift;
        long high = (mant + 1) << shift;
        return (low + high - 1) / 2.0;
    }

    public void Record(long us)
    {
        _counts[IndexOf(us)]++;
        Count++;
        SumUs += us;
        if (us < MinUs) MinUs = us;
        if (us > MaxUs) MaxUs = us;
    }

    public void Add(LatencyHistogram other)
    {
        if (other.Count == 0) return;
        var src = other._counts;
        for (int i = 0; i < BucketCount; i++) _counts[i] += src[i];
        Count += other.Count;
        SumUs += other.SumUs;
        if (other.MinUs < MinUs) MinUs = other.MinUs;
        if (other.MaxUs > MaxUs) MaxUs = other.MaxUs;
    }

    public void Reset()
    {
        Array.Clear(_counts);
        Count = 0; SumUs = 0; MinUs = long.MaxValue; MaxUs = 0;
    }

    public LatencyHistogram Clone()
    {
        var h = new LatencyHistogram();
        h.Add(this);
        return h;
    }

    /// <summary>Percentile (0-100) in millisecondi; NaN se vuoto.</summary>
    public double PercentileMs(double p)
    {
        if (Count == 0) return double.NaN;
        if (p >= 100) return MaxUs / 1000.0;
        long target = (long)Math.Ceiling(Count * p / 100.0);
        if (target < 1) target = 1;
        long acc = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            acc += _counts[i];
            if (acc >= target)
            {
                double v = ValueOf(i);
                v = Math.Clamp(v, MinUs, MaxUs);
                return v / 1000.0;
            }
        }
        return MaxUs / 1000.0;
    }

    public double AvgMs => Count == 0 ? double.NaN : SumUs / (double)Count / 1000.0;
    public double MinMs => Count == 0 ? double.NaN : MinUs / 1000.0;
    public double MaxMs => Count == 0 ? double.NaN : MaxUs / 1000.0;

    /// <summary>Rappresentazione sparsa (indice → conteggio) per la serializzazione.</summary>
    public Dictionary<int, long> ToSparse()
    {
        var d = new Dictionary<int, long>();
        for (int i = 0; i < BucketCount; i++) if (_counts[i] != 0) d[i] = _counts[i];
        return d;
    }

    public static LatencyHistogram FromSparse(Dictionary<int, long>? sparse, long minUs, long maxUs, long sumUs)
    {
        var h = new LatencyHistogram();
        if (sparse == null) return h;
        foreach (var (i, c) in sparse)
            if (i >= 0 && i < BucketCount) { h._counts[i] += c; h.Count += c; }
        h.SumUs = sumUs;
        h.MinUs = h.Count > 0 ? minUs : long.MaxValue;
        h.MaxUs = maxUs;
        return h;
    }
}
