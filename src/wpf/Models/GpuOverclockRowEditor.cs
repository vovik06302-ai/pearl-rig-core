using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PearlRigCore.Models;

/// <summary>Editable per-GPU overclock row that stays synchronized with the comma-separated miner drafts.</summary>
public sealed class GpuOverclockRowEditor : INotifyPropertyChanged
{
    private string _coreClock = "Авто";
    private string _memoryClock = "Авто";
    private string _powerLimit = "Авто";
    private string _kernel = "Авто";
    private string _fan = "Авто";
    private string _powerRangeBadge = "Диапазон PL —";
    private bool _suppressCallbacks;

    public required int Id { get; init; }
    public required string Name { get; init; }
    public Action? ChangedCallback { get; init; }

    public string PowerRangeBadge
    {
        get => _powerRangeBadge;
        set { if (_powerRangeBadge != value) { _powerRangeBadge = value; OnPropertyChanged(); } }
    }

    public string CoreClock
    {
        get => _coreClock;
        set => SetField(ref _coreClock, value);
    }

    public string MemoryClock
    {
        get => _memoryClock;
        set => SetField(ref _memoryClock, value);
    }

    public string PowerLimit
    {
        get => _powerLimit;
        set => SetField(ref _powerLimit, value);
    }

    public string Kernel
    {
        get => _kernel;
        set => SetField(ref _kernel, value);
    }

    public string Fan
    {
        get => _fan;
        set => SetField(ref _fan, value);
    }

    public void UpdateSilently(string coreClock, string memoryClock, string powerLimit, string kernel, string fan, string powerRangeBadge)
    {
        _suppressCallbacks = true;
        try
        {
            if (_coreClock != coreClock) { _coreClock = coreClock; OnPropertyChanged(nameof(CoreClock)); }
            if (_memoryClock != memoryClock) { _memoryClock = memoryClock; OnPropertyChanged(nameof(MemoryClock)); }
            if (_powerLimit != powerLimit) { _powerLimit = powerLimit; OnPropertyChanged(nameof(PowerLimit)); }
            if (_kernel != kernel) { _kernel = kernel; OnPropertyChanged(nameof(Kernel)); }
            if (_fan != fan) { _fan = fan; OnPropertyChanged(nameof(Fan)); }
            if (_powerRangeBadge != powerRangeBadge) { _powerRangeBadge = powerRangeBadge; OnPropertyChanged(nameof(PowerRangeBadge)); }
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        if (!_suppressCallbacks)
            ChangedCallback?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
