using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Drawing;
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

        public override SKRect EvaluateBounds(RenderContext ctx)
        {
            return new SKRect(0, 0, 0, 0);
        }

        public override string EvaluateColor(RenderContext ctx)
        {
            return "#FFFFFFFF";
        }

        public override SKSize EvaluateSize(RenderContext ctx)
        {
            return new SKSize(0, 0);
        }

        public override string EvaluateText(RenderContext ctx)
        {
            return "";
        }

        public override (string, string) EvaluateTextAndColor(RenderContext ctx)
        {
            return (EvaluateText(ctx), EvaluateColor(ctx));
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
