using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace InfoPanel.Models
{
    public partial class GroupDisplayItem : DisplayItem
    {
        public ObservableCollection<DisplayItem> DisplayItems { get; } = [];

        [ObservableProperty]
        private int _displayItemsCount;

        [ObservableProperty]
        private bool _isExpanded = true;

        [ObservableProperty]
        private bool _isLocked = false;

        public GroupDisplayItem()
        {
            // Initialize count
            DisplayItemsCount = DisplayItems.Count;

            // Subscribe to collection changes
            DisplayItems.CollectionChanged += OnDisplayItemsChanged;
        }

        private void OnDisplayItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
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
            throw new NotImplementedException();
        }

        public override string EvaluateColor()
        {
            throw new NotImplementedException();
        }

        public override SKSize EvaluateSize()
        {
            throw new NotImplementedException();
        }

        public override string EvaluateText()
        {
            throw new NotImplementedException();
        }

        public override (string, string) EvaluateTextAndColor()
        {
            throw new NotImplementedException();
        }

        public override void SetProfileGuid(Guid profileGuid)
        {
            ProfileGuid = profileGuid;
            foreach (var displayItem in DisplayItems)
            {
                displayItem.SetProfileGuid(profileGuid);
            }
        }
    }
}
