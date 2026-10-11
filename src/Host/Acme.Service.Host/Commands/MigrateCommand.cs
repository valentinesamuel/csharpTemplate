using System.Globalization;
using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Serilog;

namespace Acme.Service.Host.Commands;

/// <summary>
/// The <c>migrate</c> verb (§0.9, D-07). Builds a minimal host (configuration, logging and
/// <see cref="IModule.AddPersistence"/> for every module; no JWT/SMTP/blob settings needed; see <see cref="CommandHost"/>),
/// then applies the migrations of every module context in registration order and runs the post-migration steps (the
/// Hangfire schema install when <c>Jobs:Provider=Hangfire</c>, then the Auth permission/role seeder).
/// <c>migrate --list</c> prints the pending migrations per context instead. This is the only code path that
/// migrates the database (INV-16).
/// </summary>
internal static class MigrateCommand
{
    public const string Verb = "migrate";
    public const string ListOption = "--list";

    /// <summary>Exit code for invalid usage.</summary>
    public const int UsageExitCode = 2;

    /// <summary>
    /// Runs the command. <paramref name="args"/> starts with the verb; other arguments are passed to the host
    /// configuration (for example <c>--ConnectionStrings:PostgresMigrations=...</c>).
    /// </summary>
    /// <returns>0 on success; 2 on invalid usage. Failures throw and are reported by Program (exit 1).</returns>
    public static async Task<int> RunAsync(string[] args, IModule[] modules, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(output);

        var options = args.Skip(1).ToList();
        var listOnly = options.Remove(ListOption);
        if (!CommandHost.AreConfigurationArgs(options))
        {
            Log.Error("Usage: migrate [--list] [--Section:Key=value ...]");
            return UsageExitCode;
        }

        var builder = CommandHost.CreateBuilder(options, modules);

        // Post-migration steps of providers (the provider registry does not run here): Jobs:Provider=Hangfire installs
        // the Hangfire schema after the module migrations (INV-16: nothing else creates it).
        HangfireRegistration.AddSchemaMigration(builder.Services, builder.Configuration);

        builder.Services.AddMigrationRunner();

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<MigrationRunner>();

        if (runner.Targets.Count == 0)
        {
            await output.WriteLineAsync("no module contexts registered").ConfigureAwait(false);
        }

        if (listOnly)
        {
            var pending = await runner.ListPendingAsync(cancellationToken).ConfigureAwait(false);
            foreach (var context in pending)
            {
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context.ModuleKey} ({context.ContextName}): {context.Migrations.Count} pending")).ConfigureAwait(false);
                foreach (var migration in context.Migrations)
                {
                    await output.WriteLineAsync($"  {migration}").ConfigureAwait(false);
                }
            }

            return 0;
        }

        await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("Migration completed for {ModuleCount} module context(s)", runner.Targets.Count);
        return 0;
    }

    /// <summary>Runs the command for the modules hosted by this service, writing to standard output.</summary>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, HostModules.Create(), Console.Out, CancellationToken.None);
}
