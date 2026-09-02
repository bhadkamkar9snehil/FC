using System.Text.Json;

namespace FC;

public sealed class StateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private AppState _state = new();
    private bool _dirty;

    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FC");
    public string StatePath => Path.Combine(DataDirectory, "state.json");
    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(StatePath))
        {
            _state = new AppState();
            await SaveUnlockedAsync();
            _dirty = false;
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(StatePath);
            _state = JsonSerializer.Deserialize<AppState>(json, _json) ?? new AppState();
            _dirty = false;
        }
        catch
        {
            var backup = Path.Combine(DataDirectory, $"state-corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Copy(StatePath, backup, true);
            _state = new AppState();
            await SaveUnlockedAsync();
            _dirty = false;
        }
    }

    public async Task<AppState> GetSnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_state, _json);
            return JsonSerializer.Deserialize<AppState>(json, _json)!;
        }
        finally { _gate.Release(); }
    }

    public async Task MutateAsync(Action<AppState> mutation, bool notify = true, bool persist = true)
    {
        await _gate.WaitAsync();
        try
        {
            mutation(_state);
            TrimActivity(_state);
            _dirty = true;
            if (persist)
            {
                await SaveUnlockedAsync();
                _dirty = false;
            }
        }
        finally { _gate.Release(); }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task AddActivityAsync(string kind, string message, bool persist = true) => MutateAsync(s =>
        s.Activity.Insert(0, new ActivityItem { Kind = kind, Message = message }), persist: persist);

    public async Task FlushAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_dirty) return;
            await SaveUnlockedAsync();
            _dirty = false;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveUnlockedAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = StatePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state, _json));
        File.Move(temp, StatePath, true);
    }

    private static void TrimActivity(AppState state)
    {
        if (state.Activity.Count > 500) state.Activity.RemoveRange(500, state.Activity.Count - 500);
    }
}
