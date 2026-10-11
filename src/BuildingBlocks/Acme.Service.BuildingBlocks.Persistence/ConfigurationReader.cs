using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Minimal invariant-culture reader for scalar option values. This project has no configuration binder
/// (only the abstractions; plan §0.3), so options are read key by key. An unparsable value is reported as a
/// validation error naming the full key instead of being silently ignored (INV-17).
/// </summary>
internal static class ConfigurationReader
{
    public static void ReadTimeSpan(IConfigurationSection section, string key, Action<TimeSpan> assign, ICollection<string> errors)
    {
        var value = section[key];
        if (value is null)
        {
            return;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            assign(parsed);
        }
        else
        {
            errors.Add($"{section.Path}:{key} must be a time span (for example 00:00:02).");
        }
    }

    public static void ReadInt32(IConfigurationSection section, string key, Action<int> assign, ICollection<string> errors)
    {
        var value = section[key];
        if (value is null)
        {
            return;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            assign(parsed);
        }
        else
        {
            errors.Add($"{section.Path}:{key} must be an integer.");
        }
    }
}
