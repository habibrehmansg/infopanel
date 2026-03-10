using InfoPanel.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Simple class to hold format template data
    /// </summary>
    public class FormatTemplate
    {
        public string Display { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
    }

    /// <summary>
    /// Interaction logic for DateTimeProperties.xaml
    /// </summary>
    public partial class DateTimeProperties : UserControl
    {
        private readonly DispatcherTimer _previewTimer;
        private bool _isUpdatingFromTemplate = false;

        public static readonly DependencyProperty IsDateVisibleProperty =
            DependencyProperty.Register(nameof(IsDateVisible), typeof(bool), typeof(DateTimeProperties), 
                new PropertyMetadata(true));

        public static readonly DependencyProperty IsTimeVisibleProperty =
            DependencyProperty.Register(nameof(IsTimeVisible), typeof(bool), typeof(DateTimeProperties), 
                new PropertyMetadata(true));

        public bool IsDateVisible
        {
            get => (bool)GetValue(IsDateVisibleProperty);
            set => SetValue(IsDateVisibleProperty, value);
        }

        public bool IsTimeVisible
        {
            get => (bool)GetValue(IsTimeVisibleProperty);
            set => SetValue(IsTimeVisibleProperty, value);
        }

        public DateTimeProperties()
        {
            InitializeComponent();
            
            // Set up timer for live preview updates
            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _previewTimer.Tick += UpdatePreview;
            _previewTimer.Start();

            // Set up event handlers
            TextBoxFormat.TextChanged += OnFormatTextChanged;
            TemplateComboBox.SelectionChanged += OnTemplateSelected;
            DataContextChanged += OnDataContextChanged;
            
            // Initial preview update
            UpdatePreview(null, null);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Detect the type of DisplayItem and set visibility accordingly
            if (DataContext is ClockDisplayItem)
            {
                IsDateVisible = false;
                IsTimeVisible = true;
                UpdateTemplates(isClockMode: true);
            }
            else if (DataContext is CalendarDisplayItem)
            {
                IsDateVisible = true;
                IsTimeVisible = false;
                UpdateTemplates(isClockMode: false);
            }
            else
            {
                // Default: show both
                IsDateVisible = true;
                IsTimeVisible = true;
            }
        }

        private static readonly FormatTemplate[] ClockTemplates = [
                new FormatTemplate { Display = "Time 12-hour (3:45 PM)", Format = "h:mm tt" },
                new FormatTemplate { Display = "Time 12-hour with seconds (3:45:30 PM)", Format = "h:mm:ss tt" },
                new FormatTemplate { Display = "Time 24-hour (15:45)", Format = "HH:mm" },
                new FormatTemplate { Display = "Time 24-hour with seconds (15:45:30)", Format = "HH:mm:ss" },
                new FormatTemplate { Display = "Hour only (3 PM)", Format = "h tt" },
                new FormatTemplate { Display = "Custom...", Format = "" }
            ];

        private static readonly FormatTemplate[] CalendarTemplates = [
                new FormatTemplate { Display = "Short Date (12/25/24)", Format = "M/d/yy" },
                new FormatTemplate { Display = "Medium Date (Dec 25, 2024)", Format = "MMM d, yyyy" },
                new FormatTemplate { Display = "Long Date (December 25, 2024)", Format = "MMMM d, yyyy" },
                new FormatTemplate { Display = "Full Date (Monday, December 25, 2024)", Format = "dddd, MMMM d, yyyy" },
                new FormatTemplate { Display = "ISO Date (2024-12-25)", Format = "yyyy-MM-dd" },
                new FormatTemplate { Display = "European (25/12/2024)", Format = "dd/MM/yyyy" },
                new FormatTemplate { Display = "Custom...", Format = "" }
            ];


        private void UpdateTemplates(bool isClockMode)
        {
            // Update the templates based on the mode
            var template = isClockMode ? ClockTemplates : CalendarTemplates;
            TemplateComboBox.ItemsSource = template;
            TemplateComboBox.SelectedItem = template.Last(); // Select "Custom..." by default
        }

        private void OnFormatTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFromTemplate) return;
            
            UpdatePreview(null, null);
        }

        private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
        {
            if (TemplateComboBox.SelectedValue is string format && !string.IsNullOrEmpty(format))
            {
                _isUpdatingFromTemplate = true;
                TextBoxFormat.Text = format;
                _isUpdatingFromTemplate = false;
            }
        }

        private void FormatButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string formatCode)
            {
                var currentPos = TextBoxFormat.CaretIndex;
                TextBoxFormat.Text = TextBoxFormat.Text.Insert(currentPos, formatCode);
                TextBoxFormat.CaretIndex = currentPos + formatCode.Length;
                TextBoxFormat.Focus();
            }
        }

        private void UpdatePreview(object? sender, EventArgs? e)
        {
            try
            {
                var format = TextBoxFormat.Text;
                if (string.IsNullOrEmpty(format))
                {
                    PreviewText.Text = DateTime.Now.ToString();
                }
                else
                {
                    PreviewText.Text = DateTime.Now.ToString(format);
                }
                PreviewText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            }
            catch (FormatException)
            {
                PreviewText.Text = "Invalid format";
                PreviewText.Foreground = Brushes.Red;
            }
        }

    }
}