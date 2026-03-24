using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using SkiaSharp;
using System.Collections.ObjectModel;
using Serilog;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Wpf.Ui.Controls;
using Wpf.Ui;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for TextProperties.xaml
    /// </summary>
    /// 


    public partial class TextProperties : UserControl
    {
        private static readonly ILogger Logger = Log.ForContext<TextProperties>();
        private static bool _glowDisclaimerDontRemindThisSession;
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

        /// <summary>Blend modes for glow layer compositing (Skia enum names).</summary>
        public static ObservableCollection<string> GlowBlendModes { get; } =
        [
            "SrcOver",
            "Screen",
            "Plus",
            "Overlay",
            "SoftLight",
            "HardLight"
        ];

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
            LoadAllFontsAsync();
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
            Logger.Debug("OnCurrentFontChanged newValue: {NewValue}", e.NewValue);
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

                // Save current FontStyle before clearing to prevent it from being nullified
                string savedFontStyle = item.FontStyle;

                control.FontStyles.Clear();
                var styles = SKFontManager.Default.GetFontStyles(fontName);

                for (int i = 0; i < styles.Count; i++)
                {
                    control.FontStyles.Add(styles.GetStyleName(i));
                }


                if (control.FontStyles.Count > 0)
                {
                    // Try to restore saved style if it's valid for the new font
                    if (!string.IsNullOrEmpty(savedFontStyle) && control.FontStyles.Contains(savedFontStyle))
                    {
                        item.FontStyle = savedFontStyle;
                    }
                    else if (string.IsNullOrEmpty(item.FontStyle) || !control.FontStyles.Contains(item.FontStyle))
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
            Logger.Debug("OnCurrentFontStyleChanged newValue: {NewValue}", e.NewValue);
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

        private async void LoadAllFontsAsync()
        {
            var allFonts = await FontCache.GetFontsAsync();

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

        private async void GlowToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle || toggle.IsChecked != true)
                return;

            if (_glowDisclaimerDontRemindThisSession)
                return;

            var checkBox = new CheckBox
            {
                Content = "Don't remind me again during this session",
                IsChecked = false,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var content = new StackPanel();
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Using many glow effects in a panel may impact performance.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            });
            content.Children.Add(checkBox);

            var dialogService = App.GetService<IContentDialogService>();
            if (dialogService == null)
                return;

            var dialog = new ContentDialog
            {
                Title = "Glow effect",
                Content = content,
                PrimaryButtonText = "OK",
                PrimaryButtonAppearance = ControlAppearance.Primary
            };

            _ = await dialogService.ShowAsync(dialog, CancellationToken.None);

            if (checkBox.IsChecked == true)
                _glowDisclaimerDontRemindThisSession = true;
        }
    }
}
