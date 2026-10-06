using System.Windows;
using OpenTeslaBuyer.App.ViewModels;

namespace OpenTeslaBuyer.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = Shell = new ShellViewModel();
        Closing += (_, _) => Shell.Shutdown();
    }

    public ShellViewModel Shell { get; }

    /// <summary>The diagnostics page (the original main screen).</summary>
    public MainViewModel ViewModel => Shell.Diagnostics;
}
