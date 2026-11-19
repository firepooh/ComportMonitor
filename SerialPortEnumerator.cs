using System.Management;
using System.Text.RegularExpressions;

namespace ComportMonitor;

public record PortEntry(string PortName, int Number, string Description, string PnpId);

/// <summary>
/// 장치 관리자의 "포트(COM & LPT)" 클래스와 동일한 목록을 WMI로 열거한다.
/// Win32_SerialPort는 USB CDC 장치를 누락하므로 Win32_PnPEntity를 사용해야 한다.
/// </summary>
public static class SerialPortEnumerator
{
    private const string PortsClassGuid = "{4d36e978-e325-11ce-bfc1-08002be10318}";
    private static readonly Regex ComRegex = new(@"\((COM(\d+))\)", RegexOptions.Compiled);

    /// <returns>WMI 오류 시 null (호출부에서 이전 목록 유지)</returns>
    public static List<PortEntry>? Enumerate()
    {
        var list = new List<PortEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE ClassGuid='{PortsClassGuid}'");
            // 결과 컬렉션과 각 객체는 COM 자원을 쥐고 있다. 바로 해제하지 않으면
            // 장치가 바뀔 때마다 쌓여, 오래 켜 두면 메모리가 늘어난다.
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var name = obj["Name"] as string;
                    if (string.IsNullOrEmpty(name)) continue;

                    var m = ComRegex.Match(name);
                    if (!m.Success) continue; // LPT 등 COM이 아닌 포트는 제외

                    var portName = m.Groups[1].Value;
                    var number = int.Parse(m.Groups[2].Value);
                    var description = name.Replace(m.Value, "").Trim();
                    var pnpId = obj["PNPDeviceID"] as string ?? "";
                    list.Add(new PortEntry(portName, number, description, pnpId));
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        return list.OrderBy(p => p.Number).ToList();
    }
}
