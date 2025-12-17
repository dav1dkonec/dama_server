using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace dama_klient_app.Views;

// UI herní obrazovky – vizualizace desky a panelu stavu (logika v GameViewModel).
public partial class GameView : UserControl
{
    public GameView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
