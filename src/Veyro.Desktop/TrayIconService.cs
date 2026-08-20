using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Veyro.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon notifyIcon;

    public TrayIconService(Window window, Action shutdown)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Veyro", null, (_, _) => ShowWindow(window));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => shutdown());

        notifyIcon = new Forms.NotifyIcon
        {
            Text = "Veyro",
            Icon = ExtractApplicationIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ShowWindow(window);

        window.Closing += (_, eventArgs) =>
        {
            if (System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == false)
            {
                eventArgs.Cancel = true;
                window.Hide();
                notifyIcon.ShowBalloonTip(
                    1800,
                    "Veyro continua disponível",
                    "Use o ícone da bandeja para reabrir o aplicativo.",
                    Forms.ToolTipIcon.Info);
            }
        };
    }

    private static Icon ExtractApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        return executable is null
            ? SystemIcons.Application
            : Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application;
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
    }
}
