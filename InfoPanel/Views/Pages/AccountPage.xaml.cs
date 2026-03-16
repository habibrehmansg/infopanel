using InfoPanel.ViewModels;
using System.Windows.Controls;

namespace InfoPanel.Views.Pages;

public partial class AccountPage : Page
{
    public AccountViewModel ViewModel { get; }

    public AccountPage(AccountViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }
}
