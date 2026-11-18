using System.Windows;
using System.Windows.Threading;
using SameEasDecoder.Services;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder;

public partial class App : Application
{
    private static readonly string[] ValueOptions = ["--device"];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        _ = Task.Run(() => Core.Geo.UsAtlas.Instance);

        var args = e.Args;
        string? file = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueOptions.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { file = args[i]; break; }
        }

        var settings = AppSettings.Load();
        bool listen = args.Contains("--listen") || settings.ListenOnLaunch;
        var vm = new MainViewModel(settings, Dispatcher);
        var window = new MainWindow(vm, settings);
        MainWindow = window;
        window.Show();
        vm.Initialize(file, listen, Option(args, "--device"), args.Contains("--fast"));
        if (args.Contains("--history")) vm.IsHistoryTab = true;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, e.Exception.Message, "SAME / EAS Decoder", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
