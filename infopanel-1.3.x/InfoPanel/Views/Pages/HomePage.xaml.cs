
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for DesignPage.xaml
    /// </summary>
    public partial class HomePage
    {
        public HomeViewModel ViewModel
        {
            get;
        }

        public HomePage()
        {
            DataContext = this;
            InitializeComponent();
        }

        public HomePage(HomeViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }
    }
}
