using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace RescueDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "RescueDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // Show dates and sizes in the user's regional format (WPF bindings default to en-US).
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        Theme.Start(this);
        base.OnStartup(e);
    }
}
