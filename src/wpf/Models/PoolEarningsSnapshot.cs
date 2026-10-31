namespace PearlRigCore.Models;

public sealed record PoolEarningsSnapshot(
    bool BalanceAvailable,
    bool RatesAvailable,
    bool IsPartial,
    string Message,
    DateTimeOffset FetchedAt,
    double? ConfirmedPrl,
    double? UnconfirmedPrl,
    double? TotalPrl,
    double? PayoutThresholdPrl,
    double? PaidPrl,
    double? PrlUsd,
    double? UsdRub,
    double? PrlRub);
