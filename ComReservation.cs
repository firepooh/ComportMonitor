using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ComportMonitor;

/// <summary>
/// COM 번호 예약 현황. Windows가 번호를 기억하는 COM Name Arbiter(ComDB)와
/// 포트 클래스 장치 목록(연결이 끊긴 장치 포함)을 맞춰 본다. 읽기만 하므로 관리자 권한이 필요 없다.
/// </summary>
public static class ComReservation
{
    public enum State { Connected, Disconnected, ReservedOnly }

    public record Entry(int Number, State State, string Device);

    private static readonly Guid PortsClass = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private static readonly Regex ComInName = new(@"\s*\(COM(\d+)\)", RegexOptions.Compiled);

    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DICS_FLAG_GLOBAL = 0x1;
    private const uint DIREG_DEV = 0x1;
    private const int KEY_READ = 0x20019;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data,
        uint property, out uint regType, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope,
        uint profile, uint keyType, int access);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// <summary>예약된 번호와 장치를 합쳐 번호순으로 돌려준다.</summary>
    public static List<Entry> Read()
    {
        var reserved = ReadComDb();
        var devices = ReadPortDevices();
        var numbers = new SortedSet<int>(reserved);
        numbers.UnionWith(devices.Keys);

        var list = new List<Entry>();
        foreach (var n in numbers)
        {
            if (devices.TryGetValue(n, out var d))
                list.Add(new Entry(n, d.Present ? State.Connected : State.Disconnected, d.Name));
            else
                list.Add(new Entry(n, State.ReservedOnly, ""));
        }
        return list;
    }

    /// <summary>ComDB 비트맵: 비트 i가 켜져 있으면 COM(i+1)이 예약된 상태다.</summary>
    public static HashSet<int> ReadComDb()
    {
        var set = new HashSet<int>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter");
            if (key?.GetValue("ComDB") is byte[] bits)
                for (int i = 0; i < bits.Length * 8; i++)
                    if ((bits[i / 8] & (1 << (i % 8))) != 0) set.Add(i + 1);
        }
        catch (Exception) { }
        return set;
    }

    private record Device(string Name, bool Present);

    private static Dictionary<int, Device> ReadPortDevices()
    {
        var map = new Dictionary<int, Device>();
        var cls = PortsClass;
        // DIGCF_PRESENT를 주지 않으면 연결이 끊긴(숨겨진) 장치까지 나온다
        IntPtr set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0);
        if (set == InvalidHandle) return map;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string name = Property(set, ref data, SPDRP_FRIENDLYNAME)
                              ?? Property(set, ref data, SPDRP_DEVICEDESC) ?? "";
                int? number = PortNumber(set, ref data) ?? NumberFromName(name);
                if (number is not int n) continue; // LPT 등 COM이 아닌 포트
                bool present = CM_Get_DevNode_Status(out _, out _, data.DevInst, 0) == 0;
                string display = ComInName.Replace(name, "").Trim();
                // 같은 번호가 여러 장치에 기록돼 있으면 연결된 장치를 우선한다
                if (!map.TryGetValue(n, out var prev) || (present && !prev.Present))
                    map[n] = new Device(display, present);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return map;
    }

    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out uint need);
        if (need == 0) return null;
        var buffer = new byte[need];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, need, out _)) return null;
        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static int? PortNumber(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        IntPtr hkey = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (hkey == InvalidHandle || hkey == IntPtr.Zero) return null;
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
        return key.GetValue("PortName") is string portName ? ParseCom(portName) : null;
    }

    private static int? ParseCom(string text) =>
        text.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.AsSpan(3), out var n)
            ? n : null;

    private static int? NumberFromName(string name)
    {
        var m = ComInName.Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
