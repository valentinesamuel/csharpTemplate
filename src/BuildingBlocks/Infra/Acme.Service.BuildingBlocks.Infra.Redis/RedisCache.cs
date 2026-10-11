using System.Text.Json;
using Acme.Service.BuildingBlocks.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// Distributed <see cref="ICache"/> on Redis (<c>Cache:Provider=Redis</c>, StackExchange.Redis).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Connection.</b> One <see cref="ConnectionMultiplexer"/> per process, owned by this singleton and created
/// lazily on first use with <c>AbortOnConnectFail=false</c>, so the app starts while Redis is down and the
/// multiplexer keeps reconnecting in the background. Connect and operation timeouts default to 500 ms; no call waits
/// longer than the connect timeout for the initial connect, and commands fail fast while disconnected.</item>
/// <item><b>Degradation.</b> When Redis is unreachable or <see cref="RedisCircuitBreaker"/> is open (5 consecutive
/// failures open it for 30 s): <see cref="GetAsync{T}"/> returns a miss; <see cref="SetAsync{T}"/> and
/// <see cref="RemoveAsync"/> are skipped; <see cref="TryAddAsync{T}"/> throws
/// <see cref="CacheUnavailableException"/>; <see cref="IsAvailable"/> is false. Failures are logged as a Warning at
/// most once per minute (with the count of suppressed ones); the connection string is never logged (INV-28).</item>
/// <item><b>Keys.</b> Every key is prefixed with <see cref="KeyPrefix"/>, so several services can share a Redis.</item>
/// <item><b>Values.</b> Serialized with System.Text.Json (default options) as UTF-8 bytes, using the static type
/// <typeparamref name="T"/> of each call. Polymorphic values are NOT supported: a derived instance stored as its base
/// type loses its derived members, and interfaces or abstract types cannot be read back. Cache concrete, sealed
/// DTOs. A serialized value larger than <see cref="RedisCacheOptions.MaxValueBytes"/> (default 1 MiB) is rejected
/// with an <see cref="ArgumentException"/>. A stored value that no longer deserializes (for example after a DTO
/// change) is treated as a miss.</item>
/// </list>
/// </remarks>
internal sealed partial class RedisCache : ICache, IAsyncDisposable, IDisposable
{
    /// <summary>Prefix of every Redis key (template token, replaced by the init script).</summary>
    public const string KeyPrefix = "acme-service:";

    /// <summary>Minimum interval between two "unavailable" warnings.</summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly ConfigurationOptions _configuration;
    private readonly int _maxValueBytes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisCache> _logger;
    private readonly TimeSpan _connectTimeout;
    private readonly Lock _connectLock = new();
    private volatile ConnectionMultiplexer? _connection;
    private Task<ConnectionMultiplexer>? _connectTask;
    private long _nextWarningAtTicks;
    private int _suppressedWarnings;
    private volatile bool _disposed;

    public RedisCache(IOptions<RedisCacheOptions> options, TimeProvider timeProvider, ILogger<RedisCache> logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), new RedisCircuitBreaker(timeProvider), timeProvider, logger)
    {
    }

    internal RedisCache(RedisCacheOptions options, RedisCircuitBreaker breaker, TimeProvider timeProvider, ILogger<RedisCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(breaker);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = options.ToConfigurationOptions();
        _maxValueBytes = options.MaxValueBytes;
        _connectTimeout = options.ConnectTimeout;
        Breaker = breaker;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The circuit breaker guarding every Redis call.</summary>
    internal RedisCircuitBreaker Breaker { get; }

    /// <inheritdoc />
    /// <remarks>
    /// False while the circuit is open, the multiplexer is disconnected, or the first connect is still in flight.
    /// True before the first call.
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            if (_disposed || Breaker.IsOpen)
            {
                return false;
            }

            if (_connection is { } connection)
            {
                return connection.IsConnected;
            }

            lock (_connectLock)
            {
                return _connectTask is null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue value;
        try
        {
            value = await ExecuteAsync(db => db.StringGetAsync(PrefixedKey(key)), cancellationToken).ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            WarnUnavailable(nameof(GetAsync), ex);
            return default;
        }

        if (value.IsNull)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>((byte[])value!);
        }
        catch (JsonException)
        {
            LogUndeserializable(_logger, typeof(T).Name);
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = Serialize(value);
        try
        {
            await ExecuteAsync(db => db.StringSetAsync(PrefixedKey(key), bytes, ttl, When.Always), cancellationToken).ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            WarnUnavailable(nameof(SetAsync), ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await ExecuteAsync(db => db.KeyDeleteAsync(PrefixedKey(key)), cancellationToken).ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            WarnUnavailable(nameof(RemoveAsync), ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="CacheUnavailableException">Redis is unreachable or the circuit breaker is open.</exception>
    public async Task<bool> TryAddAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = Serialize(value);
        try
        {
            // SET key value PX ttl NX: atomic on the server.
            return await ExecuteAsync(db => db.StringSetAsync(PrefixedKey(key), bytes, ttl, When.NotExists), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            WarnUnavailable(nameof(TryAddAsync), ex);
            throw;
        }
    }

    /// <summary>Round-trips a PING through the breaker. Returns false when Redis is unavailable.</summary>
    internal async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(db => db.PingAsync(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var connection = MarkDisposed();
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => MarkDisposed()?.Dispose();

    /// <summary>Marks the cache disposed and detaches the multiplexer; a connect still in flight disposes its own.</summary>
    private ConnectionMultiplexer? MarkDisposed()
    {
        lock (_connectLock)
        {
            _disposed = true;
            var connection = _connection;
            _connection = null;
            return connection;
        }
    }

    private static RedisKey PrefixedKey(string key) => KeyPrefix + key;

    private static bool IsRedisFailure(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException;

    private byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > _maxValueBytes)
        {
            throw new ArgumentException(
                $"The serialized value is {bytes.Length} bytes; the maximum is {_maxValueBytes} bytes ({RedisCacheOptions.MaxValueBytesKey}).",
                nameof(value));
        }

        return bytes;
    }

    private async Task<TResult> ExecuteAsync<TResult>(Func<IDatabase, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new CacheUnavailableException("The Redis cache has been disposed.");
        }

        if (!Breaker.TryAcquire())
        {
            throw new CacheUnavailableException("Redis is unavailable (circuit breaker open).");
        }

        try
        {
            var connection = await GetConnectionAsync().ConfigureAwait(false);
            var result = await operation(connection.GetDatabase()).WaitAsync(cancellationToken).ConfigureAwait(false);
            Breaker.RecordSuccess();
            return result;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            if (Breaker.RecordFailure())
            {
                LogCircuitOpened(_logger, Breaker.FailureThreshold, Breaker.BreakDuration);
            }

            throw new CacheUnavailableException("Redis is unavailable.", ex);
        }
        catch
        {
            // Cancellation or a caller-side error: no verdict on Redis health.
            Breaker.RecordAbandoned();
            throw;
        }
    }

    private async Task<ConnectionMultiplexer> GetConnectionAsync()
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        Task<ConnectionMultiplexer> connecting;
        lock (_connectLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connecting = _connectTask ??= Task.Run(ConnectAsync);
        }

        try
        {
            // StackExchange.Redis may take a multiple of ConnectTimeout to give up on the first attempt, so a caller
            // waits at most ConnectTimeout (TimeoutException counts as a failure) while the connect continues.
            return await connecting.WaitAsync(_connectTimeout).ConfigureAwait(false);
        }
        catch (Exception) when (connecting.IsFaulted || connecting.IsCanceled)
        {
            // Let the next call start a new attempt.
            lock (_connectLock)
            {
                if (ReferenceEquals(_connectTask, connecting))
                {
                    _connectTask = null;
                }
            }

            throw;
        }
    }

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        // With AbortOnConnectFail=false this completes even when Redis is down; the multiplexer then keeps
        // reconnecting in the background.
        var connection = await ConnectionMultiplexer.ConnectAsync(_configuration).ConfigureAwait(false);
        lock (_connectLock)
        {
            if (!_disposed)
            {
                _connection = connection;
                return connection;
            }
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(RedisCache));
    }

    private void WarnUnavailable(string operation, CacheUnavailableException exception)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var next = Interlocked.Read(ref _nextWarningAtTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextWarningAtTicks, now + WarningInterval.Ticks, next) != next)
        {
            Interlocked.Increment(ref _suppressedWarnings);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressedWarnings, 0);

        // The exception text comes from StackExchange.Redis, which never includes the password (INV-28).
        LogUnavailable(_logger, exception.InnerException ?? exception, operation, suppressed);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis cache unavailable during {Operation}; the call degraded ({SuppressedCount} similar warnings suppressed since the last one)")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string operation, int suppressedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis circuit breaker opened after {FailureThreshold} consecutive failures; cache calls are skipped for {BreakDuration}")]
    private static partial void LogCircuitOpened(ILogger logger, int failureThreshold, TimeSpan breakDuration);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A cached value could not be deserialized as {TargetType}; treated as a miss")]
    private static partial void LogUndeserializable(ILogger logger, string targetType);
}
