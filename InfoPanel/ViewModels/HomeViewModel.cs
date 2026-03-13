using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui;

namespace InfoPanel.ViewModels
{
    public class HomeViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private ICommand? _navigateCommand;

        public ICommand NavigateCommand => _navigateCommand ??= new RelayCommand<string>(OnNavigate);

        public HomeViewModel(INavigationService navigationService)
        {
            _navigationService = navigationService;
        }

        public Task OnNavigatedFromAsync()
        {
            return Task.CompletedTask;
        }

        public Task OnNavigatedToAsync()
        {
            return Task.CompletedTask;
        }

        private void OnNavigate(string? parameter)
        {
            switch (parameter)
            {
                case "navigate_to_profiles":
                    _navigationService.Navigate(typeof(Views.Pages.ProfilesPage));
                    return;
                case "navigate_to_design":
                    _navigationService.Navigate(typeof(Views.Pages.DesignPage));
                    return;
                case "navigate_to_updates":
                    _navigationService.Navigate(typeof(Views.Pages.UpdatesPage));
                    return;
                case "navigate_to_about":
                    _navigationService.Navigate(typeof(Views.Pages.AboutPage));
                    return;
                case "navigate_to_plugins":
                    _navigationService.Navigate(typeof(Views.Pages.PluginsPage));
                    return;
                case "navigate_to_settings":
                    _navigationService.Navigate(typeof(Views.Pages.SettingsPage));
                    return;
                default:
                    _navigationService.Navigate(typeof(Views.Pages.HomePage));
                    return;
            }
        }
    }
}
