using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    public partial class StarRatingDisplay : UserControl
    {
        public static readonly DependencyProperty RatingProperty =
            DependencyProperty.Register(nameof(Rating), typeof(double), typeof(StarRatingDisplay),
                new PropertyMetadata(0.0, OnRatingChanged));

        public double Rating
        {
            get => (double)GetValue(RatingProperty);
            set => SetValue(RatingProperty, value);
        }

        private static readonly Brush FilledBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

        public StarRatingDisplay()
        {
            InitializeComponent();
        }

        private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StarRatingDisplay control)
                control.UpdateStars();
        }

        private void UpdateStars()
        {
            var stars = new[] { Star1, Star2, Star3, Star4, Star5 };
            var emptyBrush = (Brush)FindResource("TextFillColorTertiaryBrush");

            for (int i = 0; i < 5; i++)
            {
                var diff = Rating - i;

                if (diff >= 0.875)
                {
                    stars[i].Symbol = SymbolRegular.Star20;
                    stars[i].Filled = true;
                    stars[i].Foreground = FilledBrush;
                }
                else if (diff >= 0.625)
                {
                    stars[i].Symbol = SymbolRegular.StarThreeQuarter20;
                    stars[i].Filled = false;
                    stars[i].Foreground = FilledBrush;
                }
                else if (diff >= 0.375)
                {
                    stars[i].Symbol = SymbolRegular.StarHalf20;
                    stars[i].Filled = false;
                    stars[i].Foreground = FilledBrush;
                }
                else if (diff >= 0.125)
                {
                    stars[i].Symbol = SymbolRegular.StarOneQuarter20;
                    stars[i].Filled = false;
                    stars[i].Foreground = FilledBrush;
                }
                else
                {
                    stars[i].Symbol = SymbolRegular.Star20;
                    stars[i].Filled = false;
                    stars[i].Foreground = emptyBrush;
                }
            }
        }
    }
}
