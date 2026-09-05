using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Datameter.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.DisplayName;
        AppTitleText.Text = AppInfo.DisplayName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));

        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        // Closing the window normally leaves Datameter running in the notification area: the
        // history it accumulates past Windows' own retention is only collected while it runs.
        // Quitting from the tray sets App.IsExiting, and that is what makes a close a close.
        AppWindow.Closing += (_, args) =>
        {
            if (App.IsExiting || !Page.RunInBackground) return;

            args.Cancel = true;
            AppWindow.Hide();
        };

        // The floating meter and the tray icon are separate windows, and either would keep the
        // process alive after this one has really closed.
        Closed += (_, _) => Page.Shutdown();
    }
}
