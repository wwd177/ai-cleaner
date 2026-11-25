using System.IO;
using System.Text;
using System.Windows;

namespace AICleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            var (passed, lines) = SafetyGuard.RunSelfTests();
            string summary = string.Join(Environment.NewLine, lines) + Environment.NewLine +
                             (passed ? "SELF_TEST_RESULT=PASS" : "SELF_TEST_RESULT=FAIL");

            if (e.Args.Length >= 2 && !string.IsNullOrWhiteSpace(e.Args[1]))
            {
                var dir = Path.GetDirectoryName(e.Args[1]);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(e.Args[1], summary, new UTF8Encoding(false));
            }
            Shutdown(passed ? 0 : 1);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
        {
            string report = SafetyGuard.BuildCliDryRunReport();
            if (e.Args.Length >= 2 && !string.IsNullOrWhiteSpace(e.Args[1]))
            {
                var dir = Path.GetDirectoryName(e.Args[1]);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(e.Args[1], report, new UTF8Encoding(true));
            }
            else
            {
                Console.WriteLine(report);
            }
            Shutdown(0);
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
