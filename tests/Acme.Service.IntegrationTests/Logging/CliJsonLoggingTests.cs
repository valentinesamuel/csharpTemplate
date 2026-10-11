using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Observability;
using Acme.Service.Testing.Logging;
using Serilog;
using Shouldly;

namespace Acme.Service.IntegrationTests.Logging;

/// <summary>
/// JSON logging of the command verbs and the bootstrap logger (plan Phase 3, criteria 4 to 6). The process-level tests
/// run the built Host with <c>dotnet Acme.Service.Host.dll</c> and read its stdout. Runs in a non-parallel collection
/// because it uses the Serilog log context and temporary loggers.
/// </summary>
[Collection(CliJsonLoggingGroup.Name)]
public sealed partial class CliJsonLoggingTests
{
    private const string HostAssemblyName = "Acme.Service.Host.dll";
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void BeginInvocation_EventsInsideScope_ShareGeneratedCorrelationIdAndOutsideHaveNone()
    {
        var capture = new JsonLogCapture("acme-service", "Testing");
        using (var logger = LoggingSetup.ConfigureConsole(new LoggerConfiguration(), "acme-service", "Testing")
                   .WriteTo.Sink(capture)
                   .CreateLogger())
        {
            logger.Information("Before invocation");
            using (CliLogContext.BeginInvocation())
            {
                logger.Information("First line of invocation one");
                logger.Error("Second line of invocation one");
            }

            using (CliLogContext.BeginInvocation())
            {
                logger.Information("Line of invocation two");
            }

            logger.Information("After invocation");
        }

        var events = capture.Parse();
        events.Count.ShouldBe(5);
        events.ShouldAllBe(e => e.EnumerateObject().Count() == LoggingConfigurationTests.Keys.Length);

        events[0].GetProperty("correlationId").ValueKind.ShouldBe(JsonValueKind.Null);
        events[4].GetProperty("correlationId").ValueKind.ShouldBe(JsonValueKind.Null);

        var first = events[1].GetProperty("correlationId").GetString();
        var second = events[3].GetProperty("correlationId").GetString();
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        CorrelationIdPattern().IsMatch(first).ShouldBeTrue(first);
        CorrelationIdPattern().IsMatch(second).ShouldBeTrue(second);
        events[2].GetProperty("correlationId").GetString().ShouldBe(first);
        second.ShouldNotBe(first);

        // The correlation id is a reserved name: never repeated in meta.
        events[1].GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MigrateVerb_UnknownOption_WritesJsonErrorWithCorrelationIdAndExits2()
    {
        var result = await RunHostAsync(["migrate", "--force"]);

        result.ExitCode.ShouldBe(MigrateCommand.UsageExitCode, result.Describe());
        var lines = result.JsonLines();
        lines.ShouldNotBeEmpty(result.Describe());
        foreach (var root in lines)
        {
            root.EnumerateObject().Select(p => p.Name).ShouldBe(LoggingConfigurationTests.Keys, result.Describe());
            root.GetProperty("level").GetString().ShouldBe("Error", result.Describe());
            var correlationId = root.GetProperty("correlationId").GetString();
            correlationId.ShouldNotBeNull(result.Describe());
            CorrelationIdPattern().IsMatch(correlationId).ShouldBeTrue(result.Describe());
            root.GetProperty("service").GetString().ShouldBe("acme-service", result.Describe());
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebHost_BlankServiceName_WritesFatalJsonLineAndExits1()
    {
        var result = await RunHostAsync(["--environment", "Production", "--Observability:ServiceName="]);

        result.ExitCode.ShouldBe(1, result.Describe());
        var lines = result.JsonLines();
        var fatal = lines.Where(l => l.GetProperty("level").GetString() == "Fatal").ToList();
        fatal.Count.ShouldBe(1, result.Describe());
        fatal[0].EnumerateObject().Select(p => p.Name).ShouldBe(LoggingConfigurationTests.Keys, result.Describe());
        fatal[0].GetProperty("message").GetString().ShouldNotBeNull().ShouldStartWith("Invalid configuration", Case.Sensitive, result.Describe());
        fatal[0].GetProperty("environment").GetString().ShouldBe("Production", result.Describe());
        fatal[0].GetProperty("correlationId").ValueKind.ShouldBe(JsonValueKind.Null, result.Describe());
    }

    private static async Task<ProcessResult> RunHostAsync(IReadOnlyList<string> args)
    {
        var hostPath = LocateHost();
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var variable in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "Observability__ServiceName" })
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Host process.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(_processTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var partial = new ProcessResult(-1, await stdout, await stderr);
            throw new TimeoutException($"Host did not exit within {_processTimeout}.{Environment.NewLine}{partial.Describe()}");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>The Host dll copied to the test output folder (H-11), else the Host project's own build output.</summary>
    private static string LocateHost()
    {
        var local = Path.Combine(AppContext.BaseDirectory, HostAssemblyName);
        if (File.Exists(local) && File.Exists(Path.ChangeExtension(local, ".runtimeconfig.json")))
        {
            return local;
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var fallback = Path.Combine(
            LoggingConfigurationTests.RepositoryRoot(), "src", "Host", "Acme.Service.Host", "bin", configuration, "net10.0", HostAssemblyName);
        File.Exists(fallback).ShouldBeTrue($"Host not found at '{local}' or '{fallback}'.");
        return fallback;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex CorrelationIdPattern();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        /// <summary>Every non-empty stdout line, parsed strictly (fails on any non-JSON line).</summary>
        public IReadOnlyList<JsonElement> JsonLines()
        {
            var lines = StandardOutput.Split('\n').Where(line => line.Length > 0).ToList();
            foreach (var line in lines)
            {
                line.ShouldNotContain('\r', Describe());
            }

            return [.. lines.Select(JsonLogCapture.ParseLine)];
        }

        public string Describe() =>
            $"exit code {ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{StandardError}";
    }
}

/// <summary>Runs <see cref="CliJsonLoggingTests"/> alone: the log context and temporary loggers are process-wide concerns.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliJsonLoggingGroup
{
    public const string Name = "cli-json-logging";
}
