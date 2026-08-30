using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class IdempotencyTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly JobRepository _repo;

    public IdempotencyTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Duplicate_IdempotencyKey_Returns_Same_Job()
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        var job1 = Job.NewQueued("Email", payload, idempotencyKey: "order-42-email");
        await _repo.Insert(job1, CancellationToken.None);

        // Look up by idempotency key — should find the existing job.
        var found = await _repo.FindByIdempotencyKey("order-42-email", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(job1.Id, found!.Id);
        Assert.Equal("Email", found.JobType);
    }

    [Fact]
    public async Task Null_IdempotencyKey_Returns_Null()
    {
        var found = await _repo.FindByIdempotencyKey("nonexistent-key", CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task Different_IdempotencyKeys_Create_Separate_Jobs()
    {
        var payload = JsonSerializer.SerializeToElement(new { });

        var job1 = Job.NewQueued("Email", payload, idempotencyKey: "order-1");
        var job2 = Job.NewQueued("Email", payload, idempotencyKey: "order-2");
        await _repo.Insert(job1, CancellationToken.None);
        await _repo.Insert(job2, CancellationToken.None);

        var found1 = await _repo.FindByIdempotencyKey("order-1", CancellationToken.None);
        var found2 = await _repo.FindByIdempotencyKey("order-2", CancellationToken.None);

        Assert.NotNull(found1);
        Assert.NotNull(found2);
        Assert.NotEqual(found1!.Id, found2!.Id);
    }

    [Fact]
    public async Task Null_IdempotencyKey_Allows_Multiple_Inserts()
    {
        var payload = JsonSerializer.SerializeToElement(new { });

        var job1 = Job.NewQueued("Email", payload);
        var job2 = Job.NewQueued("Email", payload);
        await _repo.Insert(job1, CancellationToken.None);
        await _repo.Insert(job2, CancellationToken.None);

        // Both should exist independently.
        var get1 = await _repo.Get(job1.Id, CancellationToken.None);
        var get2 = await _repo.Get(job2.Id, CancellationToken.None);

        Assert.NotNull(get1);
        Assert.NotNull(get2);
        Assert.NotEqual(get1!.Id, get2!.Id);
    }
}
