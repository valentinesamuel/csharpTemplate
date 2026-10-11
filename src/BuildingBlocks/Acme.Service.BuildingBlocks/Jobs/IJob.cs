namespace Acme.Service.BuildingBlocks.Jobs;

/// <summary>A background job implemented in a module and run by the <see cref="IJobQueue"/> provider.</summary>
/// <typeparam name="TPayload">Serializable job payload.</typeparam>
public interface IJob<in TPayload>
{
    /// <summary>Runs the job. Jobs may run more than once and must be idempotent.</summary>
    Task ExecuteAsync(TPayload payload, CancellationToken cancellationToken);
}
