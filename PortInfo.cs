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
        set => SetField(ref _pnpId, value);
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
        set => SetField(ref _isBusy, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
