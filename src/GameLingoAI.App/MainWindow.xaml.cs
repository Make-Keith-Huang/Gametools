using System.Windows;
using GameLingoAI.App.ViewModels;

namespace GameLingoAI.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
