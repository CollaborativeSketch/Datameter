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

        // The floating meter is a window of its own, and an always-on-top window with nothing
        // behind it would keep the process alive after this one closes.
        Closed += (_, _) => Page.Shutdown();
    }
}
