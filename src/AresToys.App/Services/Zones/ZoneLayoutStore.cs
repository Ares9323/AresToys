using System.Text.Json;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Zones;

/// <summary>Owns the zone layouts and the monitor to layout assignments. Single write path to the settings keys
/// <c>zones.layouts</c> (user layouts only), <c>zones.assignments</c> and <c>zones.quickkeys</c>. Built-in layouts are never written.</summary>
public sealed class ZoneLayoutStore : IDisposable
{
    public const string KeyLayouts = "zones.layouts";
    public const string KeyAssignments = "zones.assignments";

    /// <summary>JSON object from a digit ("0" to "9") to a layout id: the quick key of each layout.</summary>
    public const string KeyQuickKeys = "zones.quickkeys";

    public const int MinQuickKey = 0;
    public const int MaxQuickKey = 9;

    private readonly ISettingsStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private List<ZoneLayout> _user = [];
    private Dictionary<string, string> _assignments = new(StringComparer.Ordinal);
    private Dictionary<int, string> _quickKeys = [];
    private IReadOnlyList<ZoneLayout> _all = ZoneLayoutPresets.BuiltIn;

    public ZoneLayoutStore(ISettingsStore store) => _store = store;

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Raised after layouts or assignments changed, on the thread that made the change.</summary>
    public event EventHandler? Changed;

    /// <summary>Why stored data was ignored on the last load (unreadable JSON or invalid layouts), or null.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Built-in layouts first, then user layouts.</summary>
    public IReadOnlyList<ZoneLayout> Layouts
    {
        get { lock (_gate) return _all; }
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
                    var error = layout is null ? "incomplete data" : GridLayout.Validate(layout.Grid);
                    if (layout is null || error is not null)
                    {
                        problem += $"Skipped a stored layout ({error}). ";
                    }
                    else if (IsBuiltInId(layout.Id) || user.Any(l => l.Id == layout.Id))
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

        var quickKeys = new Dictionary<int, string>();
        var quickKeysJson = await _store.GetAsync(KeyQuickKeys, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(quickKeysJson))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(quickKeysJson) ?? [];
                var known = Combine(user);
                foreach (var (digit, id) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    // Entries naming a missing layout, a bad digit, or a layout that already has a key are ignored.
                    if (!int.TryParse(digit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var key)
                        || key is < MinQuickKey or > MaxQuickKey
                        || string.IsNullOrEmpty(id)
                        || known.All(l => l.Id != id)
                        || quickKeys.ContainsValue(id))
                    {
                        continue;
                    }

                    quickKeys[key] = id;
                }
            }
            catch (JsonException ex)
            {
                problem += $"Stored quick keys are unreadable ({ex.Message}). ";
                quickKeys.Clear();
            }
        }

        lock (_gate)
        {
            _user = user;
            _assignments = assignments;
            _quickKeys = quickKeys;
            _all = Combine(user);
            LoadProblem = problem?.Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ZoneLayout? Find(string id)
    {
        lock (_gate) return _all.FirstOrDefault(l => l.Id == id);
    }

    public string? GetAssignment(string monitorId)
    {
        lock (_gate) return _assignments.TryGetValue(monitorId, out var id) ? id : null;
    }

    /// <summary>The quick key (0 to 9) of a layout, or null when it has none.</summary>
    public int? GetQuickKey(string layoutId)
    {
        lock (_gate)
        {
            foreach (var (key, id) in _quickKeys)
            {
                if (id == layoutId) return key;
            }

            return null;
        }
    }

    /// <summary>The id of the layout that has this quick key, or null when no layout does.</summary>
    public string? FindByQuickKey(int key)
    {
        lock (_gate) return _quickKeys.TryGetValue(key, out var id) ? id : null;
    }

    /// <summary>Give a layout (built-in or user) a quick key from 0 to 9, or clear it with null. A key belongs to one
    /// layout: giving it to this one takes it from whichever layout had it.</summary>
    public async Task SetQuickKeyAsync(string layoutId, int? key, CancellationToken ct)
    {
        if (Find(layoutId) is null) throw new ArgumentException($"Unknown layout '{layoutId}'.", nameof(layoutId));
        if (key is < MinQuickKey or > MaxQuickKey) throw new ArgumentOutOfRangeException(nameof(key), key, "A quick key is a digit from 0 to 9.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<int, string> next;
            lock (_gate) next = new Dictionary<int, string>(_quickKeys);

            foreach (var held in next.Where(kv => kv.Value == layoutId).Select(kv => kv.Key).ToList()) next.Remove(held);
            if (key is { } digit) next[digit] = layoutId;

            await PersistQuickKeysAsync(next, ct).ConfigureAwait(false);
            lock (_gate) _quickKeys = next;
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Insert or replace a user layout by id. Refuses built-in ids and invalid grids.</summary>
    public async Task SaveLayoutAsync(ZoneLayout layout, CancellationToken ct)
    {
        if (layout.IsBuiltIn || IsBuiltInId(layout.Id))
            throw new InvalidOperationException("Built-in layouts cannot be changed.");
        if (string.IsNullOrWhiteSpace(layout.Id))
            throw new ArgumentException("A layout needs an id.", nameof(layout));
        var error = GridLayout.Validate(layout.Grid);
        if (error is not null) throw new ArgumentException(error, nameof(layout));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<ZoneLayout> next;
            lock (_gate) next = [.. _user];

            int at = next.FindIndex(l => l.Id == layout.Id);
            if (at >= 0) next[at] = layout; else next.Add(layout);
            await PersistLayoutsAsync(next, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _user = next;
                _all = Combine(next);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove a user layout and any assignment pointing at it. Refuses built-in ids.</summary>
    public async Task DeleteLayoutAsync(string id, CancellationToken ct)
    {
        if (IsBuiltInId(id)) throw new InvalidOperationException("Built-in layouts cannot be deleted.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<ZoneLayout> next;
            Dictionary<string, string> assignments;
            lock (_gate)
            {
                next = _user.Where(l => l.Id != id).ToList();
                assignments = new Dictionary<string, string>(_assignments, StringComparer.Ordinal);
            }

            bool found;
            lock (_gate) found = next.Count != _user.Count;
            if (!found) return;

            var stale = assignments.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
            foreach (var key in stale) assignments.Remove(key);

            Dictionary<int, string> quickKeys;
            lock (_gate) quickKeys = new Dictionary<int, string>(_quickKeys);
            var staleKeys = quickKeys.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys) quickKeys.Remove(key);

            await PersistLayoutsAsync(next, ct).ConfigureAwait(false);
            if (stale.Count > 0) await PersistAssignmentsAsync(assignments, ct).ConfigureAwait(false);
            if (staleKeys.Count > 0) await PersistQuickKeysAsync(quickKeys, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _user = next;
                _all = Combine(next);
                if (stale.Count > 0) _assignments = assignments;
                if (staleKeys.Count > 0) _quickKeys = quickKeys;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Assign a layout (built-in or user) to a monitor. The monitor id is an opaque caller string.</summary>
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

    private static bool IsBuiltInId(string id) => ZoneLayoutPresets.BuiltIn.Any(l => l.Id == id);

    private static IReadOnlyList<ZoneLayout> Combine(List<ZoneLayout> user) => [.. ZoneLayoutPresets.BuiltIn, .. user];

    private Task PersistLayoutsAsync(List<ZoneLayout> user, CancellationToken ct)
        => _store.SetAsync(KeyLayouts, JsonSerializer.Serialize(user.Select(LayoutDto.From).ToList()), false, ct);

    private Task PersistAssignmentsAsync(Dictionary<string, string> map, CancellationToken ct)
        => _store.SetAsync(KeyAssignments, JsonSerializer.Serialize(map), false, ct);

    private Task PersistQuickKeysAsync(Dictionary<int, string> map, CancellationToken ct)
        => _store.SetAsync(KeyQuickKeys,
            JsonSerializer.Serialize(map.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value)),
            false, ct);

    private sealed class LayoutDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int[]? RowPercents { get; set; }
        public int[]? ColumnPercents { get; set; }
        public int[][]? CellMap { get; set; }
        public int Spacing { get; set; }
        public int SensitivityRadius { get; set; } = GridLayout.DefaultSensitivityRadius;

        public static LayoutDto From(ZoneLayout l) => new()
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
            if (string.IsNullOrWhiteSpace(Id) || RowPercents is null || ColumnPercents is null || CellMap is null || CellMap.Any(r => r is null))
                return null;
            var grid = new GridLayout(RowPercents, ColumnPercents, CellMap.Select(r => (IReadOnlyList<int>)r).ToArray(), Spacing, SensitivityRadius);
            return new ZoneLayout(Id, string.IsNullOrWhiteSpace(Name) ? Id : Name, grid, false);
        }
    }
}
