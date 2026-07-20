using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ComportMonitor;

public enum PortStatus
{
    Normal,
    Added,
    Removed,
}

public class PortInfo : INotifyPropertyChanged
{
    public PortInfo(string portName, int number)
    {
        PortName = portName;
        Number = number;
    }

    public string PortName { get; }
    public int Number { get; }

    private string _description = "";
    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    private string _pnpId = "";
    public string PnpId
    {
        get => _pnpId;
        set { if (SetField(ref _pnpId, value)) OnPropertyChanged(nameof(Tooltip)); }
    }

    private PortStatus _status;
    public PortStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(Tooltip)); }
    }

    /// <summary>행 hover 툴팁: 사용 중이면 "In use — " 접두. (배지 대체)</summary>
    public string Tooltip => IsBusy ? $"In use — {PnpId}" : PnpId;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
