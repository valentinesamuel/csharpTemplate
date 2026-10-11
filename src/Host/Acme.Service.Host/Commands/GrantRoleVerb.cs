using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth;
using Serilog;

namespace Acme.Service.Host.Commands;

/// <summary>
/// The <c>auth:grant-role --email &lt;e&gt; --role &lt;r&gt;</c> verb (§0.8, §0.9): bootstraps the first admin. Builds the
/// minimal <see cref="CommandHost"/> over <c>ConnectionStrings:Postgres</c> and grants an existing role to an existing
/// user. Idempotent; creates no users, roles or schema objects.
/// </summary>
internal static class GrantRoleVerb
{
    public const string Verb = AuthModule.GrantRoleVerb;

    /// <summary>Exit code for invalid usage.</summary>
    public const int UsageExitCode = 2;

    private const string EmailOption = "--email";
    private const string RoleOption = "--role";

    /// <summary>
    /// Runs the verb. <paramref name="args"/> starts with the verb; besides <c>--email</c> and <c>--role</c>, only
    /// <c>--Section:Key=value</c> configuration overrides are accepted.
    /// </summary>
    /// <returns>0 when the user has the role afterwards; 1 when the user or role is missing; 2 on invalid usage.</returns>
    public static async Task<int> RunAsync(string[] args, IModule[] modules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(modules);

        if (!TryParse(args, out var email, out var role, out var configurationArgs))
        {
            Log.Error("Usage: {Verb} --email <email> --role <role> [--Section:Key=value ...]", Verb);
            return UsageExitCode;
        }

        var builder = CommandHost.CreateBuilder(configurationArgs, modules);
        using var host = builder.Build();
        return await AuthModule.GrantRoleAsync(host.Services, email, role, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the verb for the modules hosted by this service.</summary>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, HostModules.Create(), CancellationToken.None);

    private static bool TryParse(string[] args, out string email, out string role, out List<string> configurationArgs)
    {
        email = string.Empty;
        role = string.Empty;
        configurationArgs = [];

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is EmailOption or RoleOption)
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return false;
                }

                if (arg == EmailOption)
                {
                    email = args[++i];
                }
                else
                {
                    role = args[++i];
                }
            }
            else if (CommandHost.AreConfigurationArgs([arg]))
            {
                configurationArgs.Add(arg);
            }
            else
            {
                return false;
            }
        }

        return email.Length > 0 && role.Length > 0;
    }
}
