using System.Text.RegularExpressions;

namespace ComportMonitor;

/// <summary>PnP 장치 ID에서 VID·PID·인터페이스·시리얼을 뽑아 사람이 읽기 좋게 만든다.</summary>
public static class DeviceId
{
    public record Info(string Bus, string? Vid, string? Pid, string? Interface, string? Serial);

    private static readonly Regex VidPid = new(@"VID_([0-9A-F]{4})[&+]PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Mi = new(@"&MI_([0-9A-F]{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FtdiSerial = new(@"PID_[0-9A-F]{4}\+([^\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Info Parse(string? pnpId)
    {
        if (string.IsNullOrWhiteSpace(pnpId)) return new Info("", null, null, null, null);
        var parts = pnpId.Split('\\');
        string bus = parts[0].ToUpperInvariant();

        string? vid = null, pid = null, iface = null, serial = null;
        var m = VidPid.Match(pnpId);
        if (m.Success)
        {
            vid = m.Groups[1].Value.ToUpperInvariant();
            pid = m.Groups[2].Value.ToUpperInvariant();
        }
        var mi = Mi.Match(pnpId);
        if (mi.Success) iface = mi.Groups[1].Value;

        if (bus == "FTDIBUS")
        {
            // FTDIBUS\VID_0403+PID_6001+A50285BIA\0000 — 시리얼 뒤에 포트 문자(A, B…)가 붙는다
            var fs = FtdiSerial.Match(pnpId);
            if (fs.Success) serial = fs.Groups[1].Value;
        }
        else if (bus == "USB" && parts.Length >= 3 && iface is null)
        {
            // 시리얼 번호가 있는 장치는 인스턴스 ID가 곧 시리얼이고, 없으면 '&'가 섞인 생성 ID다
            var instance = parts[^1];
            if (instance.Length > 0 && !instance.Contains('&')) serial = instance;
        }
        return new Info(bus, vid, pid, iface, serial);
    }

    /// <summary>예: "VID 10C4 · PID EA60 · S/N 0002". 식별 정보가 없으면 연결 방식.</summary>
    public static string Describe(string? pnpId)
    {
        var i = Parse(pnpId);
        var items = new List<string>();
        if (i.Vid is not null) items.Add($"VID {i.Vid}");
        if (i.Pid is not null) items.Add($"PID {i.Pid}");
        if (i.Interface is not null) items.Add($"Interface {i.Interface}");
        if (i.Serial is not null) items.Add($"S/N {i.Serial}");
        if (items.Count > 0) return string.Join(" · ", items);
        return i.Bus switch
        {
            "" => "",
            "ACPI" => "Built-in port (ACPI)",
            "BTHENUM" => "Bluetooth serial port",
            _ => i.Bus,
        };
    }
}
