using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using dama_klient_app.Services;
using dama_klient_app.ViewModels;

namespace dama_klient_app;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Zde se volí konkrétní implementace klienta (UDP podle PROTOCOL.md).
        AppServices.Initialize(new GameClient());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(AppServices.GameClient)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
