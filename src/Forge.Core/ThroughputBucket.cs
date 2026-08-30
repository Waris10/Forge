namespace Forge.Core;

/// <summary>
/// One time-bucket in the throughput chart. Plain settable properties
/// so Dapper can hydrate without constructor-signature matching —
/// same lesson as JobRow.
/// </summary>
public class ThroughputBucket
{
    public DateTime BucketStart { get; set; }
    public long Succeeded { get; set; }
    public long Failed { get; set; }
    public long Dead { get; set; }
}