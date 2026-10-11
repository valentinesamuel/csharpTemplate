using System.Diagnostics;
using Acme.Service.Testing.Security;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// Runs the real Host out of process and returns its exit code and output. Startup failures are asserted this way,
/// because Program reports them itself and exits 1 before a WebApplicationFactory could observe a host (as in the
/// provider selection tests).
/// </summary>
internal static class HostProcess
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts the Host in the Testing environment with the in-process providers and the given environment variables
    /// (keys use <c>__</c>), and waits for it to exit.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyDictionary<string, string> variables)
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

        foreach (var key in startInfo.Environment.Keys.Where(IsInheritedSetting).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["Cache__Provider"] = "InMemory";
        startInfo.Environment["Blob__Provider"] = "Local";
        startInfo.Environment["Blob__Local__RootPath"] = Path.Combine(Path.GetTempPath(), "service-tests", "auth-startup");
        startInfo.Environment["Notifications__Provider"] = "Console";
        startInfo.Environment["Jobs__Provider"] = "InMemory";
        startInfo.Environment["Locks__Provider"] = "InMemory";
        startInfo.Environment["Auth__Jwt__SigningKeys__0__Kid"] = TestSigningKeys.DefaultKid;
        startInfo.Environment["Auth__Jwt__SigningKeys__0__Status"] = "Active";
        startInfo.Environment["Auth__Jwt__SigningKeys__0__PrivateKeyBase64"] = TestSigningKeys.DefaultPrivateKeyBase64;
        startInfo.Environment["Auth__Links__ConfirmEmailUrl"] = "https://app.example.test/confirm-email";
        startInfo.Environment["Auth__Links__ResetPasswordUrl"] = "https://app.example.test/reset-password";
        foreach (var (key, value) in variables)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Host process.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The Host did not exit within {_timeout}; it should fail at startup.");
        }

        return (process.ExitCode, await stdout + await stderr);
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
}
