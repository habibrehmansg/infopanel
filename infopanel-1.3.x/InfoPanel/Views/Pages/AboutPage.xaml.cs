using Flurl;
using Flurl.Http;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using static InfoPanel.Views.Pages.AboutPage;
using System.Diagnostics;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class AboutPage : Page
    {
        public AboutViewModel ViewModel
        {
            get;
        }

        public AboutPage(AboutViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }
    }
}
