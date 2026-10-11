namespace Acme.Service.BuildingBlocks.Jobs;

/// <summary>A job run on a cron schedule registered via <see cref="IJobQueue.AddOrUpdateRecurringAsync{TJob}"/>.</summary>
public interface IRecurringJob
{
    /// <summary>Runs one occurrence of the job.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
