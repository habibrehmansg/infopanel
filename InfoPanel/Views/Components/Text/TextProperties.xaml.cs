using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for TextProperties.xaml
    /// </summary>
    /// 


    public partial class TextProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register("TextDisplayItem", typeof(TextDisplayItem), typeof(TextProperties));

        public static readonly DependencyProperty CurrentFontProperty =
        DependencyProperty.Register("CurrentFont", typeof(string), typeof(TextProperties),
            new PropertyMetadata(null, OnCurrentFontChanged));

        public static readonly DependencyProperty CurrentFontStyleProperty =
        DependencyProperty.Register("CurrentFontStyle", typeof(string), typeof(TextProperties),
            new PropertyMetadata(null, OnCurrentFontStyleChanged));

        public ObservableCollection<string> InstalledFonts { get; } = [];

        public ObservableCollection<string> FontStyles { get; } = [];

        public TextDisplayItem TextDisplayItem
        {
            get { return (TextDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public string CurrentFont
        {
            get { return (string)GetValue(CurrentFontProperty); }
            set { SetValue(CurrentFontProperty, value); }
        }
        public string CurrentFontStyle
        {
            get { return (string)GetValue(CurrentFontStyleProperty); }
            set { SetValue(CurrentFontStyleProperty, value); }
        }


        public TextProperties()
        {
            LoadAllFonts();
            InitializeComponent();

            SetBinding(CurrentFontProperty, new Binding
            {
                Path = new PropertyPath("TextDisplayItem.Font"),
                Source = this,
                Mode = BindingMode.OneWay
            });

            SetBinding(CurrentFontStyleProperty, new Binding
            {
                Path = new PropertyPath("TextDisplayItem.FontStyle"),
                Source = this,
                Mode = BindingMode.OneWay
            });

        }

        private static void OnCurrentFontChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Trace.WriteLine($"OnCurrentFontChanged newValue: {e.NewValue}");
            var control = (TextProperties)d;
            var item = (TextDisplayItem)control.GetValue(ItemProperty);

            if (item == null)
            {
                return;
            }

            if (e.NewValue is string fontName)
            {
                if (!control.InstalledFonts.Contains(fontName))
                {
                    var familyName = SkiaGraphics.ExtractBaseFamilyName(fontName);

                    if (!string.IsNullOrEmpty(familyName))
                    {
                        item.Font = familyName;
                    }

                    return;
                }

                control.FontStyles.Clear();
                var styles = SKFontManager.Default.GetFontStyles(fontName);

                for (int i = 0; i < styles.Count; i++)
                {
                    control.FontStyles.Add(styles.GetStyleName(i));
                }


                if (control.FontStyles.Count > 0)
                {
                    if (string.IsNullOrEmpty(item.FontStyle) || !control.FontStyles.Contains(item.FontStyle))
                    {
                        string requestedFont = "";
                        //legacy
                        if (item.Bold)
                        {
                            requestedFont = "Bold";
                        }

                        if (item.Italic)
                        {
                            if (!string.IsNullOrEmpty(requestedFont))
                            {
                                requestedFont += " ";
                            }

                            requestedFont += "Italic";
                        }

                        if (!string.IsNullOrEmpty(requestedFont))
                        {
                            if (control.FontStyles.Contains(requestedFont))
                            {
                                item.FontStyle = requestedFont;
                                return;
                            }
                        }

                        item.FontStyle = control.FontStyles[0];
                    }
                }
            }
        }

        private static void OnCurrentFontStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Trace.WriteLine($"OnCurrentFontStyleChanged newValue: {e.NewValue}");
            var control = (TextProperties)d;
            var item = (TextDisplayItem)control.GetValue(ItemProperty);

            if (item == null)
            {
                return;
            }

            if (control.FontStyles.Count > 0)
            {
                if (string.IsNullOrEmpty(item.FontStyle) || !control.FontStyles.Contains(item.FontStyle))
                {
                    string requestedFont = "";
                    //legacy
                    if (item.Bold)
                    {
                        requestedFont = "Bold";
                    }

                    if (item.Italic)
                    {
                        if (!string.IsNullOrEmpty(requestedFont))
                        {
                            requestedFont += " ";
                        }

                        requestedFont += "Italic";
                    }

                    if (!string.IsNullOrEmpty(requestedFont))
                    {
                        if (control.FontStyles.Contains(requestedFont))
                        {
                            item.FontStyle = requestedFont;
                            return;
                        }
                    }

                    item.FontStyle = control.FontStyles[0];
                }
            }
        }

        private void LoadAllFonts()
        {
            var allFonts = SKFontManager.Default.GetFontFamilies()
                .OrderBy(f => f)
                .ToList();

            foreach (var font in allFonts)
            {
                InstalledFonts.Add(font);
            }
        }

        private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TextDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                TextDisplayItem.FontSize = (int)newValue;
            }
        }
    }
}
