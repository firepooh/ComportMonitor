using System.Runtime.InteropServices;

namespace ComportMonitor;

/// <summary>
/// 포트 점유 여부를 액세스 권한 0의 CreateFile로 판별한다.
/// 시리얼 드라이버는 장치 수준 단일 오픈만 허용하므로 권한 0으로도 점유가 정확히
/// 판별되고, 포트를 초기화하지 않아 DTR/RTS 부작용(보드 리셋)이 최소화된다.
/// </summary>
public static class PortProbe
{
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool IsBusy(string portName)
    {
        IntPtr h = CreateFile(@"\\.\" + portName, 0, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h != InvalidHandle)
        {
            CloseHandle(h);
            return false;
        }
        int err = Marshal.GetLastWin32Error();
        return err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION;
    }
}
