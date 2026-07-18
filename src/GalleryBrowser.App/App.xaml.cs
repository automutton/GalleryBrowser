using System.Diagnostics;
using System.Windows;

namespace GalleryBrowser;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var waitArgumentIndex = Array.FindIndex(
            e.Args,
            argument => string.Equals(argument, "--wait-for-pid", StringComparison.OrdinalIgnoreCase));
        if (waitArgumentIndex >= 0 &&
            waitArgumentIndex + 1 < e.Args.Length &&
            int.TryParse(e.Args[waitArgumentIndex + 1], out var processId) &&
            processId != Environment.ProcessId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                {
                    MessageBox.Show(
                        "復元前のGalleryBrowserが終了していないため、データベースの切り替えを中断しました。アプリをすべて終了してから、もう一度起動してください。",
                        "GalleryBrowser",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
            }
            catch (ArgumentException)
            {
                // The previous process has already exited.
            }
        }

        base.OnStartup(e);
    }
}
