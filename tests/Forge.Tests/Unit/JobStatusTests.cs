using Forge.Core;

namespace Forge.Tests.Unit;

public class JobStatusTests
{
    [Fact]
    public void Enum_Has_All_Expected_Values()
    {
        // These must match the Postgres CHECK constraint exactly.
        var names = Enum.GetNames<JobStatus>();

        Assert.Contains("Queued", names);
        Assert.Contains("Running", names);
        Assert.Contains("Succeeded", names);
        Assert.Contains("Failed", names);
        Assert.Contains("Dead", names);
        Assert.Equal(5, names.Length);
    }

    [Theory]
    [InlineData("queued", JobStatus.Queued)]
    [InlineData("running", JobStatus.Running)]
    [InlineData("succeeded", JobStatus.Succeeded)]
    [InlineData("failed", JobStatus.Failed)]
    [InlineData("dead", JobStatus.Dead)]
    public void Lowercase_String_Parses_To_Enum(string lowercase, JobStatus expected)
    {
        // This is exactly how JobRow.ToJob() parses the DB string.
        var parsed = Enum.Parse<JobStatus>(lowercase, ignoreCase: true);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(JobStatus.Queued, "queued")]
    [InlineData(JobStatus.Running, "running")]
    [InlineData(JobStatus.Succeeded, "succeeded")]
    [InlineData(JobStatus.Failed, "failed")]
    [InlineData(JobStatus.Dead, "dead")]
    public void Enum_ToLowerInvariant_Matches_Db_Constraint(JobStatus status, string expected)
    {
        // This is how every repository write converts the enum to a DB string.
        Assert.Equal(expected, status.ToString().ToLowerInvariant());
    }
}
