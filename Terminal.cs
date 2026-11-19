using System.Diagnostics;
using System.IO;

namespace ComportMonitor;

/// <summary>포트 메뉴의 "Open in terminal" 명령을 감지하고, 자리표시자를 바꿔 실행한다.</summary>
public static class Terminal
{
    /// <summary>설치된 터미널을 찾아 기본 명령을 만든다 (PuTTY → Tera Term 순). 없으면 null.</summary>
    public static string? Detect()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var putty = FirstExisting(Path.Combine(pf, "PuTTY", "putty.exe"),
                                  Path.Combine(pf86, "PuTTY", "putty.exe")) ?? FindOnPath("putty.exe");
        if (putty is not null) return $"\"{putty}\" -serial {{port}} -sercfg 115200,8,n,1,N";

        var teraterm = FirstExisting(Path.Combine(pf86, "teraterm5", "ttermpro.exe"),
                                     Path.Combine(pf, "teraterm5", "ttermpro.exe"),
                                     Path.Combine(pf86, "teraterm", "ttermpro.exe")) ?? FindOnPath("ttermpro.exe");
        if (teraterm is not null) return $"\"{teraterm}\" /C={{num}} /BAUD=115200";

        return null;
    }

    /// <summary>{port} → COM4, {num} → 4로 바꾼 뒤 실행 파일과 인수로 나눈다.</summary>
    public static (string File, string Args) Expand(string template, string portName, int number)
    {
        var cmd = template.Replace("{port}", portName).Replace("{num}", number.ToString()).Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int space = cmd.IndexOf(' ');
        return space < 0 ? (cmd, "") : (cmd[..space], cmd[(space + 1)..].Trim());
    }

    public static void Launch(string template, PortInfo port)
    {
        var (file, args) = Expand(template, port.PortName, port.Number);
        Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true });
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(dir.Trim(), exe);
                if (File.Exists(path)) return path;
            }
            catch (Exception) { }
        }
        return null;
    }
}
