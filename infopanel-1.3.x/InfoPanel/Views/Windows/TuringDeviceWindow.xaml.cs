using InfoPanel.Models;
using InfoPanel.ViewModels;
using System.Windows;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Windows;

public partial class TuringDeviceWindow : FluentWindow
{
    public TuringDeviceWindow(TuringPanelDevice device)
    {
        InitializeComponent();
        
        var viewModel = new TuringDeviceWindowViewModel(device);
        DataContext = viewModel;
        
        Owner = Application.Current.MainWindow;
        
        Closing += (s, e) => viewModel.Cleanup();
    }
}