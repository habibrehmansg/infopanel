using InfoPanel.ViewModels;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Pages;

public partial class AccountPage : Page
{
    public AccountViewModel ViewModel { get; }

    private readonly IContentDialogService _contentDialogService;
    private bool _isFirstLoad = true;

    public AccountPage(AccountViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        _contentDialogService = App.GetService<IContentDialogService>()
            ?? throw new System.InvalidOperationException("ContentDialogService is not registered.");

        InitializeComponent();

        Loaded += AccountPage_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void AccountPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestoreTask;

        // On first load, restore already fetched sessions — skip the duplicate refresh
        if (_isFirstLoad)
        {
            _isFirstLoad = false;
            return;
        }

        if (ViewModel.IsLoggedIn)
        {
            await ViewModel.RefreshSessionsCommand.ExecuteAsync(null);
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Sign Out",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Would you like to sign out of this PC only, or sign out everywhere?",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            },
            PrimaryButtonText = "This PC",
            PrimaryButtonAppearance = ControlAppearance.Caution,
            SecondaryButtonText = "Everywhere",
            SecondaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            CloseButtonAppearance = ControlAppearance.Primary
        };

        var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.LogoutCommand.ExecuteAsync(null);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ViewModel.LogoutAllCommand.ExecuteAsync(null);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsRefreshingSessions))
        {
            Dispatcher.Invoke(() =>
            {
                if (ViewModel.IsRefreshingSessions)
                    StartSpinAnimation();
                else
                    StopSpinAnimation();
            });
        }
    }

    private void StartSpinAnimation()
    {
        if (!IsLoaded) return;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(System.TimeSpan.FromSeconds(1)),
            RepeatBehavior = RepeatBehavior.Forever
        };

        RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopSpinAnimation()
    {
        if (!IsLoaded) return;

        RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        RefreshRotation.Angle = 0;
    }
}
