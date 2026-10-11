using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Persistence.Health;

/// <summary>
/// Readiness check (D-20): opens a connection with the app connection string (<c>ConnectionStrings:Postgres</c>)
/// and runs <c>SELECT 1</c>. Descriptions never include the connection string (INV-28).
/// </summary>
public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.App);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus, $"ConnectionStrings:{ConnectionStringNames.App} is not configured.");
        }

        try
        {
            var connection = new NpgsqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = new NpgsqlCommand("SELECT 1", connection);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Postgres is unreachable.", ex);
        }
    }
}
