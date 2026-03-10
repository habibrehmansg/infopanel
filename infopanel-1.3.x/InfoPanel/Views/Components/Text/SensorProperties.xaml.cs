using InfoPanel.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using static InfoPanel.Models.SensorDisplayItem;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for SensorProperties.xaml
    /// </summary>
    public partial class SensorProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
       DependencyProperty.Register("SensorDisplayItem", typeof(SensorDisplayItem), typeof(SensorProperties));

        public SensorDisplayItem SensorDisplayItem
        {
            get { return (SensorDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public SensorProperties()
        {
            InitializeComponent();
            ComboBoxValueType.ItemsSource = Enum.GetValues(typeof(SensorValueType)).Cast<SensorValueType>();
            //Masking.SetMask(TextBoxMultiplier, "^\\$?\\-?([1-9]{1}[0-9]{0,3}(\\,\\d{3})*(\\.\\d{0,3})?|[1-9]{1}\\d{0,}(\\.\\d{0,3})?|0(\\.\\d{0,3})?|(\\.\\d{1,3}))$|^\\-?\\$?([1-9]{1}\\d{0,3}(\\,\\d{3})*(\\.\\d{0,3})?|[1-9]{1}\\d{0,}(\\.\\d{0,3})?|0(\\.\\d{0,3})?|(\\.\\d{1,3}))$|^\\(\\$?([1-9]{1}\\d{0,3}(\\,\\d{3})*(\\.\\d{0,2})?|[1-9]{1}\\d{0,}(\\.\\d{0,3})?|0(\\.\\d{0,3})?|(\\.\\d{1,3}))\\)$");
        }

        private void NumberBoxPrecision_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SensorDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            if (double.TryParse(numBox.Text, out double newValue))
            {
                numBox.Value = newValue;
                SensorDisplayItem.Precision = (int)newValue;
            }
        }

        private void NumberBoxThreshold1_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SensorDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            if (double.TryParse(numBox.Text, out double newValue))
            {
                numBox.Value = newValue;
                SensorDisplayItem.Threshold1 = newValue;
            }
        }

        private void NumberBoxThreshold2_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SensorDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            if (double.TryParse(numBox.Text, out double newValue))
            {
                numBox.Value = newValue;
                SensorDisplayItem.Threshold2 = newValue;
            }
        }

        private void NumberBoxAddition_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SensorDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            if (double.TryParse(numBox.Text, out double newValue))
            {
                numBox.Value = newValue;
                SensorDisplayItem.AdditionModifier = newValue;
            }
        }

        //private void NumberBoxMultiplication_TextChanged(object sender, TextChangedEventArgs e)
        //{
        //    if (SensorDisplayItem == null)
        //    {
        //        return;
        //    }

        //    var numBox = ((NumberBox)sender);
        //    double newValue;
        //    if (double.TryParse(numBox.Text, out newValue))
        //    {
        //        numBox.Value = newValue;
        //        SensorDisplayItem.MultiplicationModifier = newValue;
        //    }
        //}

        //private void NumberBoxMultiplication_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        //{

        //    // Important; IsReadOnly & IsReadOnlyCaretVisible set to True!

        //    var numBox = ((NumberBox)sender);
        //    if (numBox is null || !numBox.IsLoaded)
        //        return;

        //    bool insertToggled = (e.KeyboardDevice.GetKeyStates(Key.Insert) == KeyStates.Toggled);

        //    // Handle Delete
        //    if (e.Key == Key.Delete)
        //    {
        //        int caretPos = numBox.CaretIndex;
        //        int delimiterPos = Array.IndexOf(numBox.Text.ToCharArray(), '.');
        //        int selectionLength = numBox.SelectionLength; // This is reset after manipulating the text it seems like, so we save it

        //        if (caretPos == numBox.Text.Length)
        //            return;

        //        if (caretPos == delimiterPos && selectionLength == 0)
        //        {
        //            numBox.CaretIndex++;
        //            caretPos = numBox.CaretIndex;
        //        }
        //        else if (numBox.SelectedText.Contains("."))
        //        {
        //            string[] leftAndRight = numBox.SelectedText.Split('.');
        //            int leftCount = leftAndRight[0].Length;
        //            int rightCount = leftAndRight[1].Length;
        //            numBox.Text = numBox.Text.Remove(caretPos, leftCount);
        //            int newDelimiterPos = Array.IndexOf(numBox.Text.ToCharArray(), '.');
        //            numBox.Text = numBox.Text.Remove(newDelimiterPos + 1, rightCount);
        //            e.Handled = true;
        //            return;
        //        }

        //        if (selectionLength > 0)
        //        {
        //            numBox.Text = numBox.Text.Remove(caretPos, selectionLength);
        //            numBox.CaretIndex = caretPos;
        //        }
        //        else
        //        {
        //            numBox.Text = numBox.Text.Remove(caretPos, 1);
        //            numBox.CaretIndex = caretPos;
        //        }
        //    }

        //    // Handle Backspace(Remove)
        //    if (e.Key == Key.Back)
        //    {
        //        int caretPos = numBox.CaretIndex;
        //        int delimiterPos = Array.IndexOf(numBox.Text.ToCharArray(), '.');
        //        int selectionLength = numBox.SelectionLength;

        //        if (caretPos == 0 && numBox.SelectionLength == 0)
        //            return;

        //        // Caret to the right of the delimiter, move it left one step
        //        if (caretPos - 1 == delimiterPos && selectionLength == 0)
        //        {
        //            numBox.CaretIndex--;
        //            caretPos = numBox.CaretIndex;
        //        }
        //        else if (numBox.SelectedText.Contains("."))
        //        {
        //            string[] leftAndRight = numBox.SelectedText.Split('.');
        //            int leftCount = leftAndRight[0].Length;
        //            int rightCount = leftAndRight[1].Length;
        //            numBox.Text = numBox.Text.Remove(caretPos, leftCount);
        //            int newDelimiterPos = Array.IndexOf(numBox.Text.ToCharArray(), '.');
        //            numBox.Text = numBox.Text.Remove(newDelimiterPos + 1, rightCount);
        //            e.Handled = true;
        //            return;
        //        }

        //        if (selectionLength > 0)
        //        {
        //            numBox.Text = numBox.Text.Remove(Math.Max(caretPos, 0), Math.Max(selectionLength, 1));
        //            numBox.CaretIndex = caretPos;
        //        }
        //        else
        //        {
        //            numBox.Text = numBox.Text.Remove(Math.Max(caretPos - 1, 0), Math.Max(selectionLength, 1));
        //            numBox.CaretIndex = caretPos - 1;
        //        }

        //        e.Handled = true;
        //        return;
        //    }

        //    // Handle Digit Keys
        //    if (e.Key >= Key.D0 && e.Key <= Key.D9)
        //    {
        //        int caretPos = numBox.CaretIndex;
        //        string value = ((int)e.Key - (int)Key.D0).ToString();
        //        if (insertToggled)
        //        {
        //            if (numBox.Text[caretPos] == '.' || numBox.Text[caretPos] == ',')
        //            {
        //                numBox.Text = numBox.Text.Insert(Math.Clamp(caretPos + 1, 0, numBox.Text.Length), value);
        //                numBox.CaretIndex = caretPos + 2;
        //            }
        //            else
        //            {
        //                numBox.Text = numBox.Text.Remove(caretPos, 1);
        //                numBox.Text = numBox.Text.Insert(Math.Clamp(caretPos, 0, numBox.Text.Length), value);
        //                numBox.CaretIndex = caretPos + 1;
        //            }

        //            e.Handled = true;
        //            return;
        //        }
        //        numBox.Text = numBox.Text.Insert(caretPos, value);
        //        numBox.CaretIndex = caretPos + 1;
        //        e.Handled = true;
        //    }

        //    // Handle Numpad Keys
        //    if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        //    {
        //        int caretPos = numBox.CaretIndex;
        //        string value = ((int)e.Key - (int)Key.NumPad0).ToString();
        //        if (insertToggled)
        //        {
        //            if (numBox.Text[caretPos] == '.' || numBox.Text[caretPos] == ',')
        //            {
        //                numBox.Text = numBox.Text.Insert(Math.Clamp(caretPos + 1, 0, numBox.Text.Length), value);
        //                numBox.CaretIndex = caretPos + 2;
        //            }
        //            else
        //            {
        //                numBox.Text = numBox.Text.Remove(caretPos, 1);
        //                numBox.Text = numBox.Text.Insert(Math.Clamp(caretPos, 0, numBox.Text.Length), value);
        //                numBox.CaretIndex = caretPos + 1;
        //            }

        //            e.Handled = true;
        //            return;
        //        }
        //        numBox.Text = numBox.Text.Insert(caretPos, value);
        //        numBox.CaretIndex = caretPos + 1;
        //        e.Handled = true;
        //    }
        //}
    }
}
