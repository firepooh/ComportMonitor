using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ComportMonitor;

/// <summary>예약 번호 목록의 한 행.</summary>
public class ReservedRow
{
    private static readonly Brush Green = Frozen("#4ADE80");
    private static readonly Brush Amber = Frozen("#FBBF24");
    private static readonly Brush Grey = Frozen("#6B7280");

    public ReservedRow(ComReservation.Entry entry)
    {
        PortName = $"COM{entry.Number}";
        (StateText, StateBrush) = entry.State switch
        {
            ComReservation.State.Connected => ("Connected", Green),
            ComReservation.State.Disconnected => ("Not connected", Amber),
            _ => ("Reserved, no device", Grey),
        };
        Device = entry.Device.Length > 0 ? entry.Device : "—";
    }

    public string PortName { get; }
    public string StateText { get; }
    public Brush StateBrush { get; }
    public string Device { get; }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>COM 번호 예약 현황을 보여 주고 정리 방법을 안내한다 (읽기 전용).</summary>
public partial class ReservedWindow : Window
{
    public ReservedWindow()
    {
        InitializeComponent();
        var entries = ComReservation.Read();
        Rows.ItemsSource = entries.Select(e => new ReservedRow(e)).ToList();

        int connected = entries.Count(e => e.State == ComReservation.State.Connected);
        int disconnected = entries.Count(e => e.State == ComReservation.State.Disconnected);
        int reservedOnly = entries.Count(e => e.State == ComReservation.State.ReservedOnly);
        SummaryText.Text = entries.Count == 0
            ? "No COM numbers are in use or reserved."
            : $"{entries.Count} COM numbers in use or reserved: {connected} connected, " +
              $"{disconnected} held by disconnected devices, {reservedOnly} reserved with no device.";
    }

    private void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
