using System.Diagnostics.Metrics;

namespace Wedding.Infrastructure;

public static class OperationalMetrics
{
    public static readonly Meter Meter = new("Wedding.Payments", "1.0.0");
    public static readonly Counter<long> ProviderFailures = Meter.CreateCounter<long>("wedding.provider.failures");
    public static readonly Counter<long> Mismatches = Meter.CreateCounter<long>("wedding.financial.mismatches");
    public static readonly Counter<long> Uncertain = Meter.CreateCounter<long>("wedding.checkout.uncertain");
    public static readonly Counter<long> ReconciliationFailures = Meter.CreateCounter<long>("wedding.reconciliation.failures");
    public static readonly Histogram<double> InboxAge = Meter.CreateHistogram<double>("wedding.inbox.age", "s");
    private static long inboxDepth, uncertainCount, interventionCount;
    private static double oldestInboxAge;
    public static readonly ObservableGauge<long> InboxDepth = Meter.CreateObservableGauge("wedding.inbox.depth", () => Interlocked.Read(ref inboxDepth));
    public static readonly ObservableGauge<long> UncertainCount = Meter.CreateObservableGauge("wedding.checkout.uncertain_count", () => Interlocked.Read(ref uncertainCount));
    public static readonly ObservableGauge<long> InterventionCount = Meter.CreateObservableGauge("wedding.intervention.count", () => Interlocked.Read(ref interventionCount));
    public static readonly ObservableGauge<double> OldestInboxAge = Meter.CreateObservableGauge("wedding.inbox.oldest_age", () => Volatile.Read(ref oldestInboxAge), "s");
    public static void Snapshot(long inbox, long uncertain, long interventions, double oldestAge)
    {
        Interlocked.Exchange(ref inboxDepth, inbox); Interlocked.Exchange(ref uncertainCount, uncertain);
        Interlocked.Exchange(ref interventionCount, interventions); Volatile.Write(ref oldestInboxAge, oldestAge);
    }
}
