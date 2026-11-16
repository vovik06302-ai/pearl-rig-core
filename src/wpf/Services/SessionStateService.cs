using System.IO;
using System.Text;
using System.Text.Json;
using PearlRigCore.Models;

namespace PearlRigCore.Services;

public sealed record PersistedSessionState(
    double SessionEnergyKwh,
    double SessionElectricityCostRub,
    IReadOnlyList<double> TotalHashrateHistory,
    IReadOnlyList<string> WatchdogEvents,
    DateTimeOffset SavedAt);

/// <summary>Persists session energy counters, chart history, and watchdog event log across restarts in %LOCALAPPDATA%\PearlRigControl\session-state.json.</summary>
public static class SessionStateService
{
    private static string GetStatePath() =>
        Path.Combine(RigConfig.GetAppDataDirectory(), "session-state.json");

    public static PersistedSessionState? Load()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<PersistedSessionState>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null) return null;
            return state with
            {
                SessionEnergyKwh = double.IsFinite(state.SessionEnergyKwh) && state.SessionEnergyKwh >= 0 ? state.SessionEnergyKwh : 0,
                SessionElectricityCostRub = double.IsFinite(state.SessionElectricityCostRub) && state.SessionElectricityCostRub >= 0 ? state.SessionElectricityCostRub : 0,
                TotalHashrateHistory = state.TotalHashrateHistory ?? [],
                WatchdogEvents = state.WatchdogEvents ?? []
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Не удалось загрузить состояние сессии: {ex.Message}");
            return null;
        }
    }

    public static async Task SaveAsync(PersistedSessionState state, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetStatePath();
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $"session-state.{Guid.NewGuid():N}.tmp");
            var payload = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tmp, payload, new UTF8Encoding(false), cancellationToken);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Не удалось сохранить состояние сессии: {ex.Message}");
        }
    }
}
