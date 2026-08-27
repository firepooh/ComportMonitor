using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ComportMonitor;

/// <summary>별칭 편집 창의 한 행 (COM 번호 ↔ 별칭).</summary>
public class AliasEntry : INotifyPropertyChanged
{
    public AliasEntry(int number, string portName, string description, string alias)
    {
        Number = number;
        PortName = portName;
        Description = description;
        _alias = alias;
    }

    public int Number { get; }
    public string PortName { get; }
    public string Description { get; }

    private string _alias;
    public string Alias
    {
        get => _alias;
        set
        {
            if (_alias == value) return;
            _alias = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Alias)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class AliasWindow : Window
{
    public ObservableCollection<AliasEntry> Entries { get; } = new();

    /// <param name="ports">현재 연결된 포트</param>
    /// <param name="saved">저장된 별칭 (COM 번호 → 별칭). 연결이 끊긴 포트도 유지·편집 가능</param>
    public AliasWindow(IEnumerable<PortInfo> ports, IReadOnlyDictionary<int, string> saved)
    {
        InitializeComponent();

        var connected = ports.Where(p => p.Status != PortStatus.Removed)
                             .OrderBy(p => p.Number).ToList();
        foreach (var p in connected)
            Entries.Add(new AliasEntry(p.Number, p.PortName, p.Description,
                saved.TryGetValue(p.Number, out var a) ? a : ""));

        // 지금은 빠져 있지만 별칭이 저장돼 있는 포트도 편집할 수 있게 표시
        var offline = saved.Keys.Except(connected.Select(p => p.Number)).OrderBy(n => n).ToList();
        foreach (var n in offline)
            Entries.Add(new AliasEntry(n, $"COM{n}", "(not connected)", saved[n]));

        Rows.ItemsSource = Entries;
        HintText.Text = Entries.Count == 0
            ? "No COM ports connected."
            : "Leave a field empty to remove its alias.";
    }

    /// <summary>저장 결과: COM 번호 → 별칭 (빈 값은 제외).</summary>
    public Dictionary<int, string>? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Entries
            .Where(en => !string.IsNullOrWhiteSpace(en.Alias))
            .ToDictionary(en => en.Number, en => en.Alias.Trim());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
