using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using Wpf.Ui.Common.Interfaces;

namespace InfoPanel.ViewModels
{
    public class ProfilesViewModel: ObservableObject, INavigationAware
    {
        private Profile? _profile;

        public Profile? Profile
        {
            get { return _profile; }
            set
            {
                SetProperty(ref _profile, value);
            }
        }

        public void OnNavigatedFrom()
        {
        }

        public void OnNavigatedTo()
        {
        }
    }
}
