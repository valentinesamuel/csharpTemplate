using System.Collections.Concurrent;

namespace Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;

/// <summary>Ordered record of pipeline steps, shared by fixtures, the recording logger and the unit of work fake.</summary>
public sealed class TraceLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public void Add(string entry) => _entries.Enqueue(entry);
}
