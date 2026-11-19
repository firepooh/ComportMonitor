using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ComportMonitor;

/// <summary>
/// 사용 중인 COM 포트를 어떤 프로세스가 열고 있는지 찾는다.
/// 시스템 핸들 목록에서 다른 프로세스의 파일 핸들을 복제해, 시리얼 장치 핸들이면
/// 객체 이름(\Device\Silabser0 등)을 SERIALCOMM 매핑과 맞춰 본다.
/// 같은 사용자 권한으로 실행된 프로세스만 확인할 수 있다 (관리자 권한 앱은 알 수 없음).
/// </summary>
public static class PortOwnerFinder
{
    public record Owner(int Pid, string Name)
    {
        public override string ToString() => $"{Name} (PID {Pid})";
    }

    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    private const int ObjectNameInformation = 1;
    private const int FileFsDeviceInformation = 4;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;
    private const uint FILE_DEVICE_SERIAL_PORT = 0x1B;
    private const uint FILE_DEVICE_MODEM = 0x2B;
    private const uint FILE_DEVICE_UNKNOWN = 0x22;
    private const uint FILE_READ_DATA = 0x1;
    private const uint FILE_WRITE_DATA = 0x2;
    private const int HeaderSize = 16; // NumberOfHandles + Reserved (x64)
    private const int EntrySize = 40;  // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX (x64)
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int returnLength);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int cls, IntPtr buffer, int length, out int returnLength);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryVolumeInformationFile(IntPtr handle, out IoStatusBlock iosb,
        out FileFsDeviceInfo info, int length, int cls);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProcess, IntPtr srcHandle, IntPtr dstProcess,
        out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileFsDeviceInfo { public uint DeviceType; public uint Characteristics; }

    private readonly record struct Candidate(int Pid, IntPtr Process, IntPtr Handle);

    /// <summary>작업자 스레드 진행 상황. 감시 쪽이 Index가 멈춘 것을 보고 멈춤을 판단한다.</summary>
    private sealed class Scan
    {
        public volatile int Index;
        public volatile bool Done;
        public volatile bool Abandoned;
    }

    /// <summary>포트 이름 → 점유 프로세스. 찾지 못한 포트는 결과에 넣지 않는다.</summary>
    public static Dictionary<string, Owner> Find(IReadOnlyCollection<string> portNames)
    {
        var result = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        if (IntPtr.Size != 8 || portNames.Count == 0) return result; // 구조체 배치가 x64 기준

        var deviceToPort = ReadSerialComm(portNames);
        if (deviceToPort.Count == 0) return result;

        var found = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var processes = new Dictionary<int, IntPtr>();
        bool abandoned = false;
        IntPtr buffer = IntPtr.Zero;
        // 파일 객체의 형식 번호를 알아내려고 NUL 장치 핸들을 하나 열어 둔다
        IntPtr nul = CreateFile(@"\\.\NUL", 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        try
        {
            buffer = QueryHandles(out long count);
            if (buffer == IntPtr.Zero) return result;

            int self = Environment.ProcessId;
            int fileType = FindTypeIndex(buffer, count, self, nul);
            if (fileType < 0) return result;

            var candidates = new List<Candidate>();
            for (long i = 0; i < count; i++)
            {
                IntPtr e = buffer + (int)(HeaderSize + i * EntrySize);
                int pid = (int)Marshal.ReadIntPtr(e, 8);
                if (pid == self) continue;
                if ((Marshal.ReadInt16(e, 30) & 0xFFFF) != fileType) continue;
                uint access = (uint)Marshal.ReadInt32(e, 24);
                if ((access & (FILE_READ_DATA | FILE_WRITE_DATA)) == 0) continue;
                if (!processes.TryGetValue(pid, out var ph))
                {
                    ph = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    processes[pid] = ph;
                }
                if (ph != IntPtr.Zero) candidates.Add(new Candidate(pid, ph, Marshal.ReadIntPtr(e, 16)));
            }

            // 1차는 시리얼·모뎀 형식만 본다. 못 찾은 포트가 있으면 UNKNOWN 형식까지 넓힌다
            // (일부 제조사 드라이버는 장치 형식을 UNKNOWN으로 등록한다).
            abandoned |= RunScan(candidates, deviceToPort, found, includeUnknown: false);
            if (found.Count < deviceToPort.Count)
                abandoned |= RunScan(candidates, deviceToPort, found, includeUnknown: true);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (nul != InvalidHandle) CloseHandle(nul);
            // 멈춘 작업자가 남아 있으면 프로세스 핸들을 아직 쓸 수 있으므로 닫지 않는다 (드문 경우)
            if (!abandoned)
                foreach (var ph in processes.Values)
                    if (ph != IntPtr.Zero) CloseHandle(ph);
        }

        foreach (var (port, pid) in found)
            result[port] = new Owner(pid, ProcessName(pid));
        return result;
    }

    /// <summary>
    /// 이름 조회(NtQueryObject)는 동기 I/O가 걸린 핸들에서 멈출 수 있다. 작업자 스레드에서
    /// 돌리고, 한 핸들에서 300 ms 넘게 진행이 없으면 그 핸들을 건너뛴 새 작업자로 이어 간다.
    /// </summary>
    private static bool RunScan(List<Candidate> candidates, Dictionary<string, string> deviceToPort,
        ConcurrentDictionary<string, int> found, bool includeUnknown)
    {
        bool abandoned = false;
        int start = 0;
        var total = Stopwatch.StartNew();
        while (start < candidates.Count && total.ElapsedMilliseconds < 5000)
        {
            var scan = new Scan { Index = start };
            int from = start;
            var worker = new Thread(() => ScanWorker(candidates, from, scan, deviceToPort, found, includeUnknown))
            {
                IsBackground = true,
                Name = "PortOwnerScan",
            };
            worker.Start();

            int last = -1;
            var still = Stopwatch.StartNew();
            while (!scan.Done)
            {
                Thread.Sleep(10);
                int idx = scan.Index;
                if (idx != last)
                {
                    last = idx;
                    still.Restart();
                }
                else if (still.ElapsedMilliseconds > 300)
                {
                    scan.Abandoned = true;
                    abandoned = true;
                    break;
                }
            }
            if (scan.Done) break;
            start = scan.Index + 1; // 멈춘 핸들은 건너뛴다
        }
        return abandoned;
    }

    private static void ScanWorker(List<Candidate> candidates, int from, Scan scan,
        Dictionary<string, string> deviceToPort, ConcurrentDictionary<string, int> found, bool includeUnknown)
    {
        IntPtr self = GetCurrentProcess();
        for (int i = from; i < candidates.Count; i++)
        {
            if (scan.Abandoned) return;
            if (found.Count >= deviceToPort.Count) break;
            scan.Index = i;
            var c = candidates[i];
            if (!DuplicateHandle(c.Process, c.Handle, self, out var dup, 0, false, DUPLICATE_SAME_ACCESS))
                continue;
            try
            {
                // 장치 형식 조회는 I/O 관리자가 바로 답하므로 멈추지 않는다
                if (NtQueryVolumeInformationFile(dup, out _, out var dev, 8, FileFsDeviceInformation) != 0)
                    continue;
                bool serial = dev.DeviceType is FILE_DEVICE_SERIAL_PORT or FILE_DEVICE_MODEM;
                bool wanted = includeUnknown ? dev.DeviceType == FILE_DEVICE_UNKNOWN : serial;
                if (!wanted) continue;

                string? name = QueryName(dup);
                if (scan.Abandoned) return;
                if (name is not null && deviceToPort.TryGetValue(name, out var port))
                    found.TryAdd(port, c.Pid);
            }
            finally
            {
                CloseHandle(dup);
            }
        }
        scan.Done = true;
    }

    private static string? QueryName(IntPtr handle)
    {
        int size = 1024;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            int st = NtQueryObject(handle, ObjectNameInformation, buf, size, out int need);
            if ((st == STATUS_INFO_LENGTH_MISMATCH || st == STATUS_BUFFER_OVERFLOW) && need > size)
            {
                Marshal.FreeHGlobal(buf);
                size = need + 16;
                buf = Marshal.AllocHGlobal(size);
                st = NtQueryObject(handle, ObjectNameInformation, buf, size, out _);
            }
            if (st != 0) return null;
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
            int length = Marshal.ReadInt16(buf, 0) & 0xFFFF;
            IntPtr text = Marshal.ReadIntPtr(buf, 8);
            return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static IntPtr QueryHandles(out long count)
    {
        count = 0;
        int size = 1 << 22; // 4 MB부터 늘려 간다
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            int st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, size, out int need);
            if (st == 0)
            {
                count = Marshal.ReadIntPtr(buf, 0).ToInt64();
                return buf;
            }
            Marshal.FreeHGlobal(buf);
            if (st != STATUS_INFO_LENGTH_MISMATCH) return IntPtr.Zero;
            size = Math.Max(size * 2, need + (1 << 20));
        }
        return IntPtr.Zero;
    }

    private static int FindTypeIndex(IntPtr buffer, long count, int self, IntPtr ownHandle)
    {
        if (ownHandle == InvalidHandle) return -1;
        for (long i = 0; i < count; i++)
        {
            IntPtr e = buffer + (int)(HeaderSize + i * EntrySize);
            if ((int)Marshal.ReadIntPtr(e, 8) == self && Marshal.ReadIntPtr(e, 16) == ownHandle)
                return Marshal.ReadInt16(e, 30) & 0xFFFF;
        }
        return -1;
    }

    /// <summary>\Device\Silabser0 → COM4처럼 장치 객체 이름과 COM 이름을 잇는다 (관리자 권한 불필요).</summary>
    private static Dictionary<string, string> ReadSerialComm(IReadOnlyCollection<string> portNames)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return map;
            var wanted = new HashSet<string>(portNames, StringComparer.OrdinalIgnoreCase);
            foreach (var device in key.GetValueNames())
                if (key.GetValue(device) is string port && wanted.Contains(port))
                    map[device] = port;
        }
        catch (Exception) { }
        return map;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
