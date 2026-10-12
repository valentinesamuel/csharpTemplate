using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Security;
using Npgsql;
using Shouldly;

namespace Acme.Service.IntegrationTests;

/// <summary>
/// <c>Migrations:RunOnStart</c> (INV-16): the opt-in start-up step migrates first and starts the web app only when that
/// succeeded. Asserted on the real Host process, because the migration runs before any web host exists.
/// </summary>
public sealed class MigrateOnStartTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_RunOnStartWithoutMigrationsConnection_ExitsWith1AndNeverStartsWebApp()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        using var host = StartHost(new Dictionary<string, string>
        {
            ["Migrations__RunOnStart"] = "true",
            ["ConnectionStrings__Postgres"] = connectionString,
        });
        var exited = await host.WaitForExitAsync(_processTimeout, TestContext.Current.CancellationToken);

        exited.ShouldBeTrue("the Host should exit when the migration cannot run");
        host.Process.ExitCode.ShouldNotBe(0);
        host.Output.ShouldContain("the web app was not started");
        host.Output.ShouldNotContain("Now listening on");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_RunOnStartWithMigrationsConnection_MigratesThenServesReady()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        var port = FreePort();

        using var host = StartHost(
            new Dictionary<string, string>
            {
                ["Migrations__RunOnStart"] = "true",
                ["ConnectionStrings__Postgres"] = connectionString,
                ["ConnectionStrings__PostgresMigrations"] = connectionString,
            },
            port);

        var ready = await WaitForReadyAsync(port, host, TestContext.Current.CancellationToken);

        ready.ShouldBeTrue($"the app should become ready after migrating. Output: {host.Output}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from information_schema.schemata where schema_name = 'auth'", connection);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1L);
    }

    private static async Task<bool> WaitForReadyAsync(int port, HostProcess host, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow + _processTimeout;
        while (DateTime.UtcNow < deadline && !host.Process.HasExited)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet: the migration is still running.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HostProcess StartHost(Dictionary<string, string> variables, int port = 0)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            WorkingDirectory = baseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(baseDirectory, "Acme.Service.Host.dll"));

        // A clean, explicit configuration: no inherited ASP.NET Core/.NET settings or provider keys.
        foreach (var key in startInfo.Environment.Keys.Where(IsInheritedSetting).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        // Only ASPNETCORE_ENVIRONMENT is set, as on a platform: the start-up migration must carry it over itself.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["Cache__Provider"] = "InMemory";
        startInfo.Environment["Blob__Provider"] = "Local";
        startInfo.Environment["Blob__Local__RootPath"] = Path.Combine(Path.GetTempPath(), "service-tests", "migrate-on-start");
        startInfo.Environment["Notifications__Provider"] = "Console";
        startInfo.Environment["Jobs__Provider"] = "InMemory";
        startInfo.Environment["Locks__Provider"] = "InMemory";
        startInfo.Environment["Auth__Links__ConfirmEmailUrl"] = "https://app.example.test/confirm-email";
        startInfo.Environment["Auth__Links__ResetPasswordUrl"] = "https://app.example.test/reset-password";
        foreach (var (key, value) in TestSigningKeys.Entry(0, TestSigningKeys.DefaultKid, "Active", TestSigningKeys.DefaultPrivateKeyBase64))
        {
            startInfo.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        foreach (var (key, value) in variables)
        {
            startInfo.Environment[key] = value;
        }

        return new HostProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Host process."));
    }

    private static bool IsInheritedSetting(string key) =>
        key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || key.Contains("__", StringComparison.Ordinal);

    private static string DotnetHost()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(hostPath) && File.Exists(hostPath) ? hostPath : "dotnet";
    }

    private sealed class HostProcess : IDisposable
    {
        private readonly System.Text.StringBuilder _output = new();

        public HostProcess(Process process)
        {
            Process = process;
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public Process Process { get; }

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                await Process.WaitForExitAsync(linked.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }

            Process.Dispose();
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}
