namespace Acme.Service.BuildingBlocks.Jobs;

/// <summary>Provider-neutral background job port (selected by <c>Jobs:Provider</c>).</summary>
public interface IJobQueue
{
    /// <summary>Enqueues <typeparamref name="TJob"/> to run as soon as possible. Returns the provider job id.</summary>
    Task<string> EnqueueAsync<TJob, TPayload>(TPayload payload, CancellationToken cancellationToken)
        where TJob : IJob<TPayload>;

    /// <summary>Schedules <typeparamref name="TJob"/> to run after <paramref name="delay"/>. Returns the provider job id.</summary>
    Task<string> ScheduleAsync<TJob, TPayload>(TPayload payload, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob<TPayload>;

    /// <summary>Creates or updates the recurring job <paramref name="recurringJobId"/> on <paramref name="cronExpression"/>.</summary>
    Task AddOrUpdateRecurringAsync<TJob>(string recurringJobId, string cronExpression, CancellationToken cancellationToken)
        where TJob : IRecurringJob;
}
