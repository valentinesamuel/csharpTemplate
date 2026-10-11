using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Host.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// Builds the Auth services the way the command hosts do (Data Protection, module registry, <c>AddPersistence</c>),
/// over one test database, and runs the real <c>migrate</c> verb.
/// </summary>
internal static class AuthTestServices
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A provider over <paramref name="connectionString"/> for <paramref name="modules"/> (default: the hosted modules).</summary>
    public static ServiceProvider Create(string connectionString, params IModule[] modules)
    {
        if (modules.Length == 0)
        {
            modules = HostModules.Create();
        }


        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["ConnectionStrings:PostgresMigrations"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtectionSetup();
        services.AddModuleRegistry(modules);
        foreach (var module in modules)
        {
            module.AddPersistence(services, configuration);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Runs <c>migrate</c> (migrations, then the seeder) for the hosted modules and asserts exit code 0.</summary>
    public static async Task MigrateAsync(string connectionString)
    {
        var exitCode = await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=InMemory"],
            HostModules.Create(),
            TextWriter.Null,
            Ct);
        exitCode.ShouldBe(0);
    }

    /// <summary>Runs a scalar query.</summary>
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs a query and returns each row's columns joined with '|'.</summary>
    public static async Task<List<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join('|', values.Select(v => v is DBNull ? "<null>" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>Runs a non-query statement.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>True when the schema exists.</summary>
    public static async Task<bool> SchemaExistsAsync(string connectionString, string schema) =>
        await ScalarAsync<bool>(connectionString, "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)", ("schema", schema));
}
