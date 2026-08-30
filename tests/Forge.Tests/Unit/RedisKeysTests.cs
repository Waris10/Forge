using Forge.Storage.Redis;

namespace Forge.Tests.Unit;

public class RedisKeysTests
{
    [Fact]
    public void Queue_Formats_Correctly()
    {
        Assert.Equal("forge:queue:default", RedisKeys.Queue("default"));
        Assert.Equal("forge:queue:email", RedisKeys.Queue("email"));
    }

    [Fact]
    public void Processing_Formats_Correctly()
    {
        Assert.Equal("forge:processing:worker-1", RedisKeys.Processing("worker-1"));
    }

    [Fact]
    public void Job_Formats_With_Guid()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        Assert.Equal("forge:job:12345678-1234-1234-1234-123456789abc", RedisKeys.Job(id));
    }

    [Fact]
    public void Heartbeat_Formats_Correctly()
    {
        Assert.Equal("forge:heartbeat:worker-42", RedisKeys.Heartbeat("worker-42"));
    }

    [Fact]
    public void Scheduled_Is_Constant()
    {
        Assert.Equal("forge:scheduled", RedisKeys.Scheduled);
    }

    [Fact]
    public void Dlq_Is_Constant()
    {
        Assert.Equal("forge:dlq", RedisKeys.Dlq);
    }

    [Fact]
    public void SchedulerLock_Is_Constant()
    {
        Assert.Equal("forge:lock:scheduler", RedisKeys.SchedulerLock);
    }

    [Fact]
    public void All_Keys_Share_Forge_Prefix()
    {
        // Every key should start with "forge:" so KEYS forge:*
        // captures everything Forge owns and nothing it doesn't.
        var keys = new[]
        {
            RedisKeys.Queue("default"),
            RedisKeys.Processing("w"),
            RedisKeys.Job(Guid.NewGuid()),
            RedisKeys.Scheduled,
            RedisKeys.Dlq,
            RedisKeys.Heartbeat("w"),
            RedisKeys.SchedulerLock
        };

        Assert.All(keys, k => Assert.StartsWith("forge:", k));
    }
}
