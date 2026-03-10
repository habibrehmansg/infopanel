using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for CommonProperties.xaml
    /// </summary>
    public partial class CommonProperties : UserControl
    {

        public CommonProperties()
        {
            InitializeComponent();
        }

        private void ButtonUp_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.Y -= SharedModel.Instance.MoveValue;
            //}

            foreach (var item in SharedModel.Instance.SelectedItems)
            {
                item.Y -= SharedModel.Instance.MoveValue;
            }
        }

        private void ButtonDown_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.Y += SharedModel.Instance.MoveValue;
            //}

            foreach (var item in SharedModel.Instance.SelectedItems)
            {
                item.Y += SharedModel.Instance.MoveValue;
            }
        }

        private void ButtonLeft_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.X -= SharedModel.Instance.MoveValue;
            //}

            foreach (var item in SharedModel.Instance.SelectedItems)
            {
                item.X -= SharedModel.Instance.MoveValue;
            }
        }

        private void ButtonRight_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.X += SharedModel.Instance.MoveValue;
            //}

            foreach (var item in SharedModel.Instance.SelectedItems)
            {
                item.X += SharedModel.Instance.MoveValue;
            }


        }

        private void ButtonMoveValue_Click(object sender, RoutedEventArgs e)
        {
            switch (SharedModel.Instance.MoveValue)
            {
                case 1:
                    SharedModel.Instance.MoveValue = 5;
                    break;
                case 5:
                    SharedModel.Instance.MoveValue = 10;
                    break;
                case 10:
                    SharedModel.Instance.MoveValue = 20;
                    break;
                case 20:
                    SharedModel.Instance.MoveValue = 1;
                    break;
                default:
                    SharedModel.Instance.MoveValue = 1;
                    break;
            }
        }

        private void NumberBoxX_TextChanged(object sender, TextChangedEventArgs e)
        {
            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
                {
                    displayItem.X = (int)newValue;
                }
            }
         }
                
        private void NumberBoxY_TextChanged(object sender, TextChangedEventArgs e)
        {
            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
                {
                    displayItem.Y = (int)newValue;
                }
            }
        }
    }
}
