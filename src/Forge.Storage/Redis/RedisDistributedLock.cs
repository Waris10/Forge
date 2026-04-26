using StackExchange.Redis;

namespace Forge.Storage.Redis;

/// <summary>
/// A single-instance Redis lock using SET NX PX. The simplest correct lock
/// for a "we want exactly one of these processes to be active at a time"
/// problem like our scheduler.
///
/// Mechanics:
///   - Acquire: SET key=<lockName> value=<ourInstanceId> NX PX <ttlMs>.
///     NX = only set if not exists. PX = TTL in milliseconds. The atomic
///     combination of these flags is what makes this a lock primitive
///     instead of just a write. Returns true if we got it.
///   - Refresh: SET ... XX PX <ttlMs>. XX = only set if exists. We'd never
///     want to "refresh" a lock we don't hold — XX prevents that.
///   - Release: only release if the value matches our instance id, via
///     a tiny Lua script. Without that check we could release a lock
///     someone else now holds (we crashed, our TTL expired, another
///     instance acquired, then we wake up and try to "release").
///
/// What this lock is NOT:
///   - Not Redlock (which targets multi-master Redis with network partitions).
///   - Not safe under arbitrary clock skew or pauses longer than the TTL.
///   - Not a fencing-token lock (a paranoid version would also include a
///     monotonic counter the underlying resource validates).
///
/// What it IS: correct enough for "elect a singleton scheduler" against
/// a single Redis instance, which is our deployment model. The same shape
/// Sidekiq Pro uses for its leader-election. Honest about its limits.
/// </summary>
public class RedisDistributedLock
{
    // Lua script: release iff we still own the lock. Atomic check-and-delete.
    private const string ReleaseLua = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        else
          return 0
        end
        """;

    private readonly IConnectionMultiplexer _redis;

    public RedisDistributedLock(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    /// <summary>
    /// Try to acquire the lock. Returns true if we got it, false if someone
    /// else holds it.
    /// </summary>
    public async Task<bool> TryAcquire(string lockName, string instanceId, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();
        return await db.StringSetAsync(
            key: lockName,
            value: instanceId,
            expiry: ttl,
            when: When.NotExists);
    }

    /// <summary>
    /// Refresh the TTL on a lock we already hold. Uses XX so we never
    /// accidentally re-acquire after someone else has taken over.
    /// Returns true if our refresh succeeded, false if we'd lost the lock.
    /// </summary>
    public async Task<bool> TryRefresh(string lockName, string instanceId, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();

        // We need: "set value to instanceId AND extend TTL, but only if
        // the current value is still instanceId." StackExchange.Redis's
        // StringSetAsync with When.Exists gives us "set if exists" but not
        // "set if exists AND value matches" — so we use a tiny script.
        const string lua = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
              return redis.call('PEXPIRE', KEYS[1], ARGV[2])         
            else
              return 0
            end
            """;

        var result = await db.ScriptEvaluateAsync(
            lua,
            keys: new RedisKey[] { lockName },
            values: new RedisValue[] { instanceId, (long)ttl.TotalMilliseconds });

        return (long)result == 1;
    }

    /// <summary>
    /// Release the lock if (and only if) we still own it.
    /// </summary>
    public async Task Release(string lockName, string instanceId)
    {
        var db = _redis.GetDatabase();
        await db.ScriptEvaluateAsync(
            ReleaseLua,
            keys: new RedisKey[] { lockName },
            values: new RedisValue[] { instanceId });
    }
}