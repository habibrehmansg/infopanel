using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Xml.Serialization;

namespace InfoPanel.Models
{
    public partial class GroupDisplayItem : DisplayItem
    {
        public ObservableCollection<DisplayItem> DisplayItems { get; } = [];

        /// <summary>
        /// Gets an immutable copy of the DisplayItems collection for thread-safe enumeration.
        /// This copy is updated whenever the DisplayItems collection changes.
        /// </summary>
        [XmlIgnore]
        public ImmutableList<DisplayItem> DisplayItemsCopy { get; private set; }

        [ObservableProperty]
        private int _displayItemsCount;

        [ObservableProperty]
        private bool _isExpanded = true;

        [ObservableProperty]
        private bool _isLocked = false;

        public GroupDisplayItem()
        {
            // Subscribe to collection changes first
            DisplayItems.CollectionChanged += OnDisplayItemsChanged;
            
            // Then create initial copy and set count
            DisplayItemsCopy = [.. DisplayItems];
            DisplayItemsCount = DisplayItems.Count;
        }

        private void OnDisplayItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            DisplayItemsCopy = [.. DisplayItems];
            DisplayItemsCount = DisplayItems.Count;
        }

        [RelayCommand]
        private void ToggleLock()
        {
            IsLocked = !IsLocked;
        }

        public override object Clone()
        {
            var clone = new GroupDisplayItem
            {
                Name = Name
            };

            foreach (var displayItem in DisplayItems)
            {
                clone.DisplayItems.Add((DisplayItem)displayItem.Clone());
            }

            return clone;
        }

        public override SKRect EvaluateBounds()
        {
            return new SKRect(0, 0, 0, 0);
        }

        public override string EvaluateColor()
        {
            return "#FFFFFFFF";
        }

        public override SKSize EvaluateSize()
        {
            return new SKSize(0, 0);
        }

        public override string EvaluateText()
        {
            return "";
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), EvaluateColor());
        }

        public override void SetProfile(Profile profile)
        {
            base.SetProfile(profile);
            ;
            foreach (var displayItem in DisplayItems)
            {
                displayItem.SetProfile(profile);
            }
        }
    }
}
