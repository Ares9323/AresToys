using System.Globalization;
using System.Text.Json.Nodes;
using AresToys.App.Services.Zones;
using AresToys.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Makes a monitor use a Window snapping layout: the layout picked in the step is assigned to the monitor
/// under the cursor, the primary monitor or a numbered monitor. Writes through
/// <see cref="ZoneLayoutStore.SetAssignmentAsync"/>, the same path the layout editor's Apply uses.</summary>
public sealed class SetSnapLayoutTask : IPipelineTask
{
    public const string TaskId = "arestoys.window_snapping.set_layout";

    private readonly ZoneLayoutStore _store;
    private readonly ZoneMonitors _monitors;
    private readonly ILogger<SetSnapLayoutTask> _logger;

    public SetSnapLayoutTask(ZoneLayoutStore store, ZoneMonitors monitors, ILogger<SetSnapLayoutTask> logger)
    {
        _store = store;
        _monitors = monitors;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Set snapping layout";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    /// <summary>Which monitor a step names.</summary>
    public enum MonitorKind { Cursor, Primary, Number, Invalid }

    /// <summary>A parsed monitor choice. <see cref="Index"/> is 0 based and only meaningful for
    /// <see cref="MonitorKind.Number"/>.</summary>
    public readonly record struct MonitorSelector(MonitorKind Kind, int Index = 0);

    /// <summary>Reads the "monitor" config value: empty or missing means the monitor under the cursor, "cursor" and
    /// "primary" are the two words, a number from 1 to <paramref name="monitorCount"/> is that monitor (the numbering
    /// of the layout editor tiles). Anything else is invalid.</summary>
    public static MonitorSelector ParseMonitor(string? value, int monitorCount)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            return new MonitorSelector(MonitorKind.Cursor);
        }

        if (text.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            return new MonitorSelector(MonitorKind.Primary);
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= monitorCount)
        {
            return new MonitorSelector(MonitorKind.Number, number - 1);
        }

        return new MonitorSelector(MonitorKind.Invalid);
    }

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var layout = FindLayout(config?["layout"]?.GetValue<string>());
        if (layout is null)
        {
            context.Abort("The snapping layout no longer exists.");
            return;
        }

        var real = _monitors.Real();
        var monitor = Resolve(ParseMonitor(config?["monitor"]?.GetValue<string>(), real.Count), real);
        if (monitor is null)
        {
            context.Abort("That monitor is not available.");
            return;
        }

        // With zones spanning all monitors the assignment lives on the combined monitor, as in the layout editor.
        var spanned = _monitors.All() is [{ Id: ZoneMonitors.SpanId }];
        var key = spanned ? ZoneMonitors.SpanId : monitor.Id;
        if (string.Equals(_store.GetAssignment(key), layout.Id, StringComparison.Ordinal)) return;

        await _store.SetAssignmentAsync(key, layout.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Set snapping layout: '{Layout}' on {Monitor}", layout.Name, key);
    }

    // The editor's list holds layout names (a provider returns plain strings, so the id would show as a code), so a
    // name is tried first and an id after it, which keeps a hand written config with an id working.
    private ZoneLayout? FindLayout(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return _store.Layouts.FirstOrDefault(l => string.Equals(l.Name, value, StringComparison.Ordinal))
            ?? _store.Find(value);
    }

    private ZoneMonitor? Resolve(MonitorSelector selector, IReadOnlyList<ZoneMonitor> real)
    {
        switch (selector.Kind)
        {
            case MonitorKind.Cursor:
                return ZoneNative.GetCursorPos(out var cursor)
                    ? _monitors.FromPoint(cursor.X, cursor.Y)
                    : _monitors.Primary();
            case MonitorKind.Primary:
                return _monitors.Primary();
            case MonitorKind.Number:
                return real[selector.Index];
            default:
                return null;
        }
    }
}
