using System.Threading;
using System.Windows;

namespace ComportMonitor;

public partial class App : Application
{
    // 사용자 세션마다 하나만 실행한다. 두 번째로 실행하면 기존 위젯을 불러오고 바로 끝난다.
    // 위젯이 숨어 있고 트레이 아이콘까지 숨겨진 경우에도 exe를 다시 실행하면 복구된다.
    private const string MutexName = @"Local\ComportMonitor.SingleInstance";
    private const string ShowEventName = @"Local\ComportMonitor.Show";

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (Exception) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
                Dispatcher.InvokeAsync(main.ShowFromOtherInstance);
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
