using System.Text.Json;
using System.Text.Json.Serialization;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Zones;

/// <summary>Owns the zone layouts and the monitor to layout assignments. Single write path to the settings keys
/// <c>zones.layouts</c> and <c>zones.assignments</c>. A fresh store holds no layout.</summary>
public sealed class ZoneLayoutStore : IDisposable
{
    public const string KeyLayouts = "zones.layouts";
    public const string KeyAssignments = "zones.assignments";

    private readonly ISettingsStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Dictionary<string, string> _assignments = new(StringComparer.Ordinal);
    private IReadOnlyList<ZoneLayout> _layouts = [];

    public ZoneLayoutStore(ISettingsStore store) => _store = store;

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Raised after layouts or assignments changed, on the thread that made the change.</summary>
    public event EventHandler? Changed;

    /// <summary>Why stored data was ignored on the last load (unreadable JSON or invalid layouts), or null.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>The saved layouts, in the order they were created. May be empty.</summary>
    public IReadOnlyList<ZoneLayout> Layouts
    {
        get { lock (_gate) return _layouts; }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        string? problem = null;
        var user = new List<ZoneLayout>();
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);

        var layoutsJson = await _store.GetAsync(KeyLayouts, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(layoutsJson))
        {
            try
            {
                var dtos = JsonSerializer.Deserialize<List<LayoutDto>>(layoutsJson) ?? [];
                foreach (var dto in dtos)
                {
                    var layout = dto?.ToLayout();
                    var error = layout is null ? "incomplete data" : ZoneLayout.Validate(layout);
                    if (layout is null || error is not null)
                    {
                        problem += $"Skipped a stored layout ({error}). ";
                    }
                    else if (user.Any(l => l.Id == layout.Id))
                    {
                        problem += $"Skipped stored layout '{layout.Id}' (duplicate id). ";
                    }
                    else
                    {
                        user.Add(layout);
                    }
                }
            }
            catch (JsonException ex)
            {
                problem += $"Stored layouts are unreadable ({ex.Message}). ";
                user.Clear();
            }
        }

        var assignmentsJson = await _store.GetAsync(KeyAssignments, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(assignmentsJson))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(assignmentsJson) ?? [];
                foreach (var (monitor, id) in map)
                {
                    if (!string.IsNullOrEmpty(id)) assignments[monitor] = id;
                }
            }
            catch (JsonException ex)
            {
                problem += $"Stored assignments are unreadable ({ex.Message}). ";
            }
        }

        lock (_gate)
        {
            _assignments = assignments;
            _layouts = user;
            LoadProblem = problem?.Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ZoneLayout? Find(string id)
    {
        lock (_gate) return _layouts.FirstOrDefault(l => l.Id == id);
    }

    public string? GetAssignment(string monitorId)
    {
        lock (_gate) return _assignments.TryGetValue(monitorId, out var id) ? id : null;
    }

    /// <summary>Insert or replace a layout by id. Refuses invalid layouts.</summary>
    public async Task SaveLayoutAsync(ZoneLayout layout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(layout.Id))
            throw new ArgumentException("A layout needs an id.", nameof(layout));
        var error = ZoneLayout.Validate(layout);
        if (error is not null) throw new ArgumentException(error, nameof(layout));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<ZoneLayout> next;
            lock (_gate) next = [.. _layouts];

            int at = next.FindIndex(l => l.Id == layout.Id);
            if (at >= 0) next[at] = layout; else next.Add(layout);
            await PersistLayoutsAsync(next, ct).ConfigureAwait(false);
            lock (_gate) _layouts = next;
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove a layout and any assignment pointing at it. Does nothing for an unknown id.</summary>
    public async Task DeleteLayoutAsync(string id, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<ZoneLayout> next;
            Dictionary<string, string> assignments;
            bool found;
            lock (_gate)
            {
                next = _layouts.Where(l => l.Id != id).ToList();
                found = next.Count != _layouts.Count;
                assignments = new Dictionary<string, string>(_assignments, StringComparer.Ordinal);
            }

            if (!found) return;

            var stale = assignments.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
            foreach (var key in stale) assignments.Remove(key);

            await PersistLayoutsAsync(next, ct).ConfigureAwait(false);
            if (stale.Count > 0) await PersistAssignmentsAsync(assignments, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _layouts = next;
                if (stale.Count > 0) _assignments = assignments;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Assign a layout to a monitor. The monitor id is an opaque caller string.</summary>
    public async Task SetAssignmentAsync(string monitorId, string layoutId, CancellationToken ct)
    {
        if (Find(layoutId) is null) throw new ArgumentException($"Unknown layout '{layoutId}'.", nameof(layoutId));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> next;
            lock (_gate)
            {
                next = new Dictionary<string, string>(_assignments, StringComparer.Ordinal) { [monitorId] = layoutId };
            }

            await PersistAssignmentsAsync(next, ct).ConfigureAwait(false);
            lock (_gate) _assignments = next;
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task PersistLayoutsAsync(List<ZoneLayout> user, CancellationToken ct)
        => _store.SetAsync(KeyLayouts, JsonSerializer.Serialize(user.Select(LayoutDto.From).ToList()), false, ct);

    private Task PersistAssignmentsAsync(Dictionary<string, string> map, CancellationToken ct)
        => _store.SetAsync(KeyAssignments, JsonSerializer.Serialize(map), false, ct);

    private sealed class LayoutDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }

        /// <summary>"canvas" for a canvas layout; null (not written) for a grid layout, so old data keeps its shape.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CanvasZoneDto>? Zones { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? RowPercents { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? ColumnPercents { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[][]? CellMap { get; set; }
        public int Spacing { get; set; }
        public int SensitivityRadius { get; set; } = GridLayout.DefaultSensitivityRadius;

        private const string CanvasKind = "canvas";

        public static LayoutDto From(ZoneLayout l) => l.Canvas is { } canvas
            ? new LayoutDto
            {
                Id = l.Id,
                Name = l.Name,
                Kind = CanvasKind,
                Zones = [.. canvas.Zones.Select(z => new CanvasZoneDto { Left = z.Left, Top = z.Top, Right = z.Right, Bottom = z.Bottom })],
                SensitivityRadius = canvas.SensitivityRadius,
            }
            : new LayoutDto
            {
                Id = l.Id,
                Name = l.Name,
                RowPercents = [.. l.Grid.RowPercents],
                ColumnPercents = [.. l.Grid.ColumnPercents],
                CellMap = l.Grid.CellMap.Select(r => r.ToArray()).ToArray(),
                Spacing = l.Grid.Spacing,
                SensitivityRadius = l.Grid.SensitivityRadius,
            };

        public ZoneLayout? ToLayout()
        {
            if (string.Equals(Kind, CanvasKind, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(Id) || Zones is null || Zones.Any(z => z is null)) return null;
                var canvas = new CanvasLayout([.. Zones.Select(z => new CanvasZone(z.Left, z.Top, z.Right, z.Bottom))], SensitivityRadius);
                return ZoneLayout.ForCanvas(Id, string.IsNullOrWhiteSpace(Name) ? Id : Name, canvas);
            }

            if (string.IsNullOrWhiteSpace(Id) || RowPercents is null || ColumnPercents is null || CellMap is null || CellMap.Any(r => r is null))
                return null;
            var grid = new GridLayout(RowPercents, ColumnPercents, CellMap.Select(r => (IReadOnlyList<int>)r).ToArray(), Spacing, SensitivityRadius);
            return new ZoneLayout(Id, string.IsNullOrWhiteSpace(Name) ? Id : Name, grid);
        }
    }

    private sealed class CanvasZoneDto
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }
}
