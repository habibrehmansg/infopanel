using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Windows
{
    /// <summary>
    /// Interaction logic for ClosingWindow.xaml
    /// </summary>
    public partial class LoadingWindow : Window
    {
        public LoadingWindow()
        {
            InitializeComponent();
        }

        public void SetText(string text)
        {
            TextBlock.Text = text;  
        }
    }
}
