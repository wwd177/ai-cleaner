using System.IO;
using System.Text;
using System.Windows;

namespace AICleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (CliRunner.IsCliInvocation(e.Args))
        {
            int code = CliRunner.Run(e.Args);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(SafetyGuard.LogsDir);
                File.AppendAllText(Path.Combine(SafetyGuard.LogsDir, "error.log"),
                    $"[{DateTime.Now:O}] UI: {args.Exception}\n\n", new UTF8Encoding(false));
            }
            catch { }
            MessageBox.Show($"程序遇到异常：{args.Exception.Message}\n详情已写入 logs\\error.log", "AI 清理助手", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        string? qaDir = (e.Args.Length >= 2 && e.Args[0].Equals("--qa-capture", StringComparison.OrdinalIgnoreCase))
            ? e.Args[1]
            : null;
        var win = new MainWindow(qaDir);
        MainWindow = win;
        win.Show();
    }
}
