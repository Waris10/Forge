namespace Forge.Api.Contracts
{
    public sealed record BulkRetryRequest(Guid[]? Ids);
    public sealed record BulkRetryResponse(int Requested, int Retried, int Failed, Guid[] FailedIds);
}
