using System.Windows;

namespace ComportMonitor;

/// <summary>"Open in terminal"이 실행할 명령을 편집한다. 빈 값은 자동 감지를 뜻한다.</summary>
public partial class TerminalWindow : Window
{
    /// <summary>저장 결과. null이면 자동 감지.</summary>
    public string? Result { get; private set; }

    public TerminalWindow(string? current)
    {
        InitializeComponent();
        CommandBox.Text = current ?? "";
        UpdateStatus();
        Loaded += (_, _) =>
        {
            CommandBox.Focus();
            CommandBox.SelectAll();
        };
    }

    private void UpdateStatus()
    {
        if (CommandBox.Text.Trim().Length > 0)
        {
            StatusText.Text = "";
            return;
        }
        StatusText.Text = Terminal.Detect() is null
            ? "No terminal detected. Enter a command."
            : "Empty: uses the detected terminal.";
    }

    private void CommandBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateStatus();

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var detected = Terminal.Detect();
        if (detected is null)
        {
            StatusText.Text = "No PuTTY or Tera Term found.";
            return;
        }
        CommandBox.Text = detected;
        StatusText.Text = "Detected.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = CommandBox.Text.Trim();
        Result = text.Length == 0 ? null : text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
