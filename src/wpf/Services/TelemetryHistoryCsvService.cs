using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PearlRigCore.Services;

public sealed record CsvHistoryEntry(
    DateTimeOffset Timestamp,
    double? TotalHashrateTHs,
    double? TotalPowerWatts,
    double? EfficiencyGhPerWatt,
    double? MaxCoreTempC,
    double? MaxVramTempC,
    double SessionEnergyKwh,
    double ActiveTariffRubPerKwh,
    double? TotalBalancePrl,
    double? PaidPrl,
    double? PrlRubRate,
    double? NetProfitRubPerDay,
    string EventNote);

/// <summary>Writes real financial and hardware telemetry snapshots and payout events to a local CSV file compatible with Excel.</summary>
public static class TelemetryHistoryCsvService
{
    private const string Header = "Дата и время;Хешрейт (TH/s);Мощность (Вт);Эффективность (GH/W);Макс. темп. ядра (°C);Макс. темп. GDDR6X (°C);Сессия (кВт·ч);Тариф (₽/кВт·ч);Баланс пула (PRL);Выплачено (PRL);Курс PRL/RUB;Чистая прибыль (₽/сут);Событие";

    public static string GetCsvFilePath()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history.csv");
    }

    public static async Task AppendEntryAsync(CsvHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var path = GetCsvFilePath();
        var exists = File.Exists(path);
        var sb = new StringBuilder();
        if (!exists)
            sb.AppendLine(Header);

        static string Num(double? val, string format = "0.00") =>
            val is double d && double.IsFinite(d) ? d.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

        var safeNote = (entry.EventNote ?? string.Empty).Replace(';', ',').Replace("\r", " ").Replace("\n", " ").Trim();
        sb.Append(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(';')
          .Append(Num(entry.TotalHashrateTHs, "0.00")).Append(';')
          .Append(Num(entry.TotalPowerWatts, "0.0")).Append(';')
          .Append(Num(entry.EfficiencyGhPerWatt, "0.00")).Append(';')
          .Append(Num(entry.MaxCoreTempC, "0")).Append(';')
          .Append(Num(entry.MaxVramTempC, "0")).Append(';')
          .Append(entry.SessionEnergyKwh.ToString("0.000", CultureInfo.InvariantCulture)).Append(';')
          .Append(entry.ActiveTariffRubPerKwh.ToString("0.00", CultureInfo.InvariantCulture)).Append(';')
          .Append(Num(entry.TotalBalancePrl, "0.00000000")).Append(';')
          .Append(Num(entry.PaidPrl, "0.00000000")).Append(';')
          .Append(Num(entry.PrlRubRate, "0.0000")).Append(';')
          .Append(Num(entry.NetProfitRubPerDay, "0.00")).Append(';')
          .AppendLine(safeNote);

        await File.AppendAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
    }

    public static void RevealInExplorer()
    {
        var path = GetCsvFilePath();
        if (!File.Exists(path))
            File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
