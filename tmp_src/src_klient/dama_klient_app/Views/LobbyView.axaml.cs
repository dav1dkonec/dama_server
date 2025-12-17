using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace dama_klient_app.Views;

// UI pro lobby: seznam místností + akce (obnovit, vytvořit, připojit).
public partial class LobbyView : UserControl
{
    public LobbyView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
