using System.Diagnostics;

namespace DnsLoadTester.Core.Engine;

/// <summary>
/// Distribuisce gli invii su una griglia temporale assoluta (slot n → t = n / QPS), con rampa opzionale.
/// Se il sistema resta indietro (server lento, concorrenza insufficiente) non recupera con raffiche:
/// oltre 1 s di ritardo la griglia viene riallineata.
/// </summary>
public sealed class RateScheduler
{
    private readonly double _qps;
    private readonly double _ramp;
    private readonly long _startTs;
    private long _offsetTicks;
    private long _issued;
    private readonly object _rebaseLock = new();
    private static readonly long MaxBacklogTicks = Stopwatch.Frequency;     // 1 s

    public bool Unlimited => _qps <= 0;
    public long BehindEvents;   // numero di riallineamenti (indicatore di carico non sostenibile)

    public RateScheduler(double qps, double rampSeconds, long startTs)
    {
        _qps = qps;
        _ramp = Math.Max(0, rampSeconds);
        _startTs = startTs;
    }

    private double SlotSeconds(long n)
    {
        if (_ramp <= 0) return n / _qps;
        double rampSlots = _qps * _ramp / 2.0;
        return n < rampSlots ? Math.Sqrt(2.0 * _ramp * n / _qps) : n / _qps + _ramp / 2.0;
    }

    public async ValueTask WaitTurnAsync(CancellationToken ct)
    {
        if (_qps <= 0) return;
        long n = Interlocked.Increment(ref _issued) - 1;
        long target = _startTs + Volatile.Read(ref _offsetTicks) + (long)(SlotSeconds(n) * Stopwatch.Frequency);
        long now = Stopwatch.GetTimestamp();
        long behind = now - target;
        if (behind > MaxBacklogTicks)
        {
            lock (_rebaseLock)
            {
                long t2 = _startTs + _offsetTicks + (long)(SlotSeconds(n) * Stopwatch.Frequency);
                long b2 = now - t2;
                if (b2 > MaxBacklogTicks)
                {
                    _offsetTicks += b2 - Stopwatch.Frequency / 20;
                    BehindEvents++;
                }
            }
            return;
        }
        long waitTicks = target - now;
        if (waitTicks <= 0) return;
        var wait = TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency);
        if (wait.TotalMilliseconds >= 1)
            await Task.Delay(wait, ct).ConfigureAwait(false);
        else
            while (Stopwatch.GetTimestamp() < target) Thread.SpinWait(50);
    }
}
