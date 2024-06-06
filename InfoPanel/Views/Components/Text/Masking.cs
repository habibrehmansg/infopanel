using System;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel
{
   

    /// <summary>
    /// Provides masking behavior for any <see cref="TextBox"/>.
    /// </summary>
    public static class Masking
    {
        private static readonly DependencyPropertyKey _maskExpressionPropertyKey = DependencyProperty.RegisterAttachedReadOnly("MaskExpression",
                typeof(Regex),
                typeof(Masking),
                new FrameworkPropertyMetadata());

        /// <summary>
        /// Identifies the <see cref="Mask"/> dependency property.
        /// </summary>
        public static readonly DependencyProperty MaskProperty = DependencyProperty.RegisterAttached("Mask",
                typeof(string),
                typeof(Masking),
                new FrameworkPropertyMetadata(OnMaskChanged));

        /// <summary>
        /// Identifies the <see cref="MaskExpression"/> dependency property.
        /// </summary>
        public static readonly DependencyProperty MaskExpressionProperty = _maskExpressionPropertyKey.DependencyProperty;

        /// <summary>
        /// Gets the mask for a given <see cref="TextBox"/>.
        /// </summary>
        /// <param name="textBox">
        /// The <see cref="TextBox"/> whose mask is to be retrieved.
        /// </param>
        /// <returns>
        /// The mask, or <see langword="null"/> if no mask has been set.
        /// </returns>
        public static string GetMask(TextBox textBox)
        {
            if (textBox == null)
            {
                throw new ArgumentNullException("textBox");
            }

            return textBox.GetValue(MaskProperty) as string;
        }

        /// <summary>
        /// Sets the mask for a given <see cref="TextBox"/>.
        /// </summary>
        /// <param name="textBox">
        /// The <see cref="TextBox"/> whose mask is to be set.
        /// </param>
        /// <param name="mask">
        /// The mask to set, or <see langword="null"/> to remove any existing mask from <paramref name="textBox"/>.
        /// </param>
        public static void SetMask(TextBox textBox, string mask)
        {
            if (textBox == null)
            {
                throw new ArgumentNullException("textBox");
            }

            textBox.SetValue(MaskProperty, mask);
        }

        /// <summary>
        /// Gets the mask expression for the <see cref="TextBox"/>.
        /// </summary>
        /// <remarks>
        /// This method can be used to retrieve the actual <see cref="Regex"/> instance created as a result of setting the mask on a <see cref="TextBox"/>.
        /// </remarks>
        /// <param name="textBox">
        /// The <see cref="TextBox"/> whose mask expression is to be retrieved.
        /// </param>
        /// <returns>
        /// The mask expression as an instance of <see cref="Regex"/>, or <see langword="null"/> if no mask has been applied to <paramref name="textBox"/>.
        /// </returns>
        public static Regex GetMaskExpression(TextBox textBox)
        {
            if (textBox == null)
            {
                throw new ArgumentNullException("textBox");
            }

            return textBox.GetValue(MaskExpressionProperty) as Regex;
        }

        private static void SetMaskExpression(TextBox textBox, Regex regex)
        {
            textBox.SetValue(_maskExpressionPropertyKey, regex);
        }

        private static void OnMaskChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var textBox = dependencyObject as TextBox;
            var mask = e.NewValue as string;
            textBox.PreviewTextInput -= textBox_PreviewTextInput;
            textBox.PreviewKeyDown -= textBox_PreviewKeyDown;
            DataObject.RemovePastingHandler(textBox, Pasting);
            DataObject.RemoveCopyingHandler(textBox, NoDragCopy);
            CommandManager.RemovePreviewExecutedHandler(textBox, NoCutting);


            if (mask == null)
            {
                textBox.ClearValue(MaskProperty);
                textBox.ClearValue(MaskExpressionProperty);
            }
            else
            {
                textBox.SetValue(MaskProperty, mask);
                SetMaskExpression(textBox, new Regex(mask, RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace));
                textBox.PreviewTextInput += textBox_PreviewTextInput;
                textBox.PreviewKeyDown += textBox_PreviewKeyDown;
                DataObject.AddPastingHandler(textBox, Pasting);
                DataObject.AddCopyingHandler(textBox, NoDragCopy);
                CommandManager.AddPreviewExecutedHandler(textBox, NoCutting);
            }
        }

        private static void NoCutting(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command == ApplicationCommands.Cut)
            {
                e.Handled = true;
            }
        }

        private static void NoDragCopy(object sender, DataObjectCopyingEventArgs e)
        {
            if (e.IsDragDrop)
            {
                e.CancelCommand();
            }
        }

        private static void textBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            //MessageBox.Show("TextInput: " + textBox.Text);
            var maskExpression = GetMaskExpression(textBox);
            if (maskExpression == null)
            {
                return;
            }

            var proposedText = GetProposedText(textBox, e.Text);

            if (!maskExpression.IsMatch(proposedText))
            {
                e.Handled = true;
            }
        }

        private static void textBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            //MessageBox.Show("KeyDown: " + textBox.Text);
            var maskExpression = GetMaskExpression(textBox);

            if (maskExpression == null)
            {
                return;
            }

            string proposedText = null;

            //pressing space doesn't raise PreviewTextInput, reasons here http://social.msdn.microsoft.com/Forums/en-US/wpf/thread/446ec083-04c8-43f2-89dc-1e2521a31f6b?prof=required
            if (e.Key == Key.Space)
            {
                proposedText = GetProposedText(textBox, " ");
            }
            // Same story with backspace
            else if (e.Key == Key.Back)
            {
                proposedText = GetProposedTextBackspace(textBox);
            }

            if (proposedText != null && proposedText != string.Empty && !maskExpression.IsMatch(proposedText))
            {
                e.Handled = true;
            }

        }

        private static void Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = sender as TextBox;
            var maskExpression = GetMaskExpression(textBox);

            if (maskExpression == null)
            {
                return;
            }

            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var pastedText = e.DataObject.GetData(typeof(string)) as string;
                var proposedText = GetProposedText(textBox, pastedText);

                if (!maskExpression.IsMatch(proposedText))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private static string GetProposedTextBackspace(TextBox textBox)
        {
            var text = GetTextWithSelectionRemoved(textBox);
            if (textBox.SelectionStart > 0 && textBox.SelectionLength == 0)
            {
                text = text.Remove(textBox.SelectionStart - 1, 1);
            }

            return text;
        }


        private static string GetProposedText(TextBox textBox, string newText)
        {
            var text = GetTextWithSelectionRemoved(textBox);
            text = text.Insert(textBox.CaretIndex, newText);

            return text;
        }

        private static string GetTextWithSelectionRemoved(TextBox textBox)
        {
            var text = textBox.Text;

            if (textBox.SelectionStart != -1)
            {
                text = text.Remove(textBox.SelectionStart, textBox.SelectionLength);
            }
            return text;
        }
    }
}
