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
        set { if (SetField(ref _description, value)) OnPropertyChanged(nameof(Tooltip)); }
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
        set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(BusyText));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    /// <summary>포트를 연 프로세스 (예: "UartTerminal (PID 5376)"). 모르면 빈 문자열.</summary>
    private string _owner = "";
    public string Owner
    {
        get => _owner;
        set
        {
            if (!SetField(ref _owner, value ?? "")) return;
            OnPropertyChanged(nameof(BusyText));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    /// <summary>점유 문구. 사용 중이 아니면 빈 문자열.</summary>
    public string BusyText => !IsBusy ? "" : Owner.Length > 0 ? $"In use by {Owner}" : "In use";

    /// <summary>사용자가 지정한 별칭 (COM 번호에 매칭). 빈 문자열이면 없음.</summary>
    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set
        {
            if (!SetField(ref _alias, value ?? "")) return;
            OnPropertyChanged(nameof(HasAlias));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    public bool HasAlias => !string.IsNullOrWhiteSpace(Alias);

    /// <summary>행 hover 툴팁: 장치명, VID/PID·시리얼, 점유 프로세스, 원본 장치 ID.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            var title = HasAlias
                ? (Description.Length > 0 ? $"{Alias} — {Description}" : Alias)
                : Description;
            if (title.Length > 0) lines.Add(title);
            var ids = DeviceId.Describe(PnpId);
            if (ids.Length > 0) lines.Add(ids);
            if (IsBusy) lines.Add(BusyText);
            if (PnpId.Length > 0) lines.Add(PnpId);
            return string.Join("\n", lines);
        }
    }

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
