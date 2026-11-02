using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;

namespace PsiSplit.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            // Documentation preview: no user profile, singleton, tray, sockets,
            // tunnel, PAC recovery, registry writes, or application launches.
            base.OnStartup(e);
            var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "PsiSplitPreview", Guid.NewGuid().ToString("N")));
            var config = store.CreateDefault();
            config.MinimizeToTray = false;
            config.Apps.Add(new AppProfile
            {
                Name = "Chrome", Path = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", Mode = TunnelMode.Filtered
            });
            config.Apps.Add(new AppProfile
            {
                Name = "Windows PowerShell", Path = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", Mode = TunnelMode.Full
            });
            store.Save(config);
            var preview = new MainWindow(store, preview: true) { Title = "PsiSplit — Documentation preview" };
            MainWindow = preview;
            preview.Show();
            return;
        }

        // One instance only: two copies would fight over the same local ports
        // and the system PAC setting.
        _mutex = new Mutex(initiallyOwned: true, @"Local\PsiSplit.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("PsiSplit is already running.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show(
            "An error occurred:" + Environment.NewLine + Environment.NewLine +
            e.Exception.Message + Environment.NewLine + Environment.NewLine +
            "Details were written to %APPDATA%\\PsiSplit\\crash.log.",
            "PsiSplit",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => WriteCrash(e.ExceptionObject as Exception);

    private static void WriteCrash(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PsiSplit");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging must never throw.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // ignore
                }
            }

            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }
}
