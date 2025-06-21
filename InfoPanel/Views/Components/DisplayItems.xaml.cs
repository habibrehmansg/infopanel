using GongSolutions.Wpf.DragDrop;
using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for DisplayItems.xaml
    /// </summary>
    public partial class DisplayItems : UserControl, IDropTarget
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayItems>();
        private static DisplayItem? SelectedItem { get { return SharedModel.Instance.SelectedItem; } }

        private CollectionViewSource? _displayItemsViewSource;
        private string _searchText = string.Empty;

        public DisplayItems()
        {
            DataContext = this;
            InitializeComponent();
            Loaded += DisplayItems_Loaded;
            Unloaded += DisplayItems_Unloaded;
        }

        private void DisplayItems_Loaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.PropertyChanged += Instance_PropertyChanged;
            
            // Get the CollectionViewSource from resources
            _displayItemsViewSource = FindResource("DisplayItemsViewSource") as CollectionViewSource;
            if (_displayItemsViewSource != null)
            {
                _displayItemsViewSource.Filter += DisplayItemsViewSource_Filter;
            }
        }

        private void DisplayItems_Unloaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.PropertyChanged -= Instance_PropertyChanged;
            
            // Unsubscribe from filter
            if (_displayItemsViewSource != null)
            {
                _displayItemsViewSource.Filter -= DisplayItemsViewSource_Filter;
            }
        }

        private void Instance_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedModel.Instance.SelectedItem))
            {
                Logger.Debug("SelectedItem changed");
                if (SelectedItem != null)
                {
                    var group = SharedModel.Instance.GetParent(SelectedItem);

                    if (group is not null)
                    {
                        if (!group.IsExpanded)
                        {
                            ListViewItems.ScrollIntoView(group);
                            group.IsExpanded = true;
                        }
                    }
                }
            }
            else if (e.PropertyName == nameof(SharedModel.Instance.SelectedProfile))
            {
                // Refresh the view when profile changes
                _displayItemsViewSource?.View?.Refresh();
            }
        }

        private void DisplayItemsViewSource_Filter(object sender, FilterEventArgs e)
        {
            if (e.Item is not DisplayItem item)
            {
                e.Accepted = false;
                return;
            }

            // If no search text, accept all items
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                e.Accepted = true;
                return;
            }

            // Apply search filter
            var searchLower = _searchText.ToLower();
            var searchTerms = searchLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (item is GroupDisplayItem groupItem)
            {
                // Check if group name matches all search terms
                bool groupMatches = MatchesAllTerms(groupItem.Name, searchTerms);

                // Check if any children match
                bool hasMatchingChildren = !groupMatches && groupItem.DisplayItems.Any(child =>
                    MatchesAllTerms(child.Name, searchTerms) ||
                    MatchesAllTerms(child.Kind, searchTerms));

                e.Accepted = groupMatches || hasMatchingChildren;
                
                // Expand groups that match the search
                if (e.Accepted)
                {
                    groupItem.IsExpanded = true;
                }
            }
            else
            {
                // Regular item - check name and kind
                e.Accepted = MatchesAllTerms(item.Name, searchTerms) ||
                           MatchesAllTerms(item.Kind, searchTerms);
            }
        }


        private static bool MatchesAllTerms(string text, string[] searchTerms)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var textLower = text.ToLower();
            return searchTerms.All(term => textLower.Contains(term));
        }

        private void TextBoxSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
                return;

            // Update the search text and refresh the view for live filtering
            _searchText = textBox.Text ?? string.Empty;
            _displayItemsViewSource?.View?.Refresh();
        }



        private void ScrollToView(DisplayItem displayItem)
        {
            var group = SharedModel.Instance.GetParent(displayItem);

            if (group is GroupDisplayItem groupItem)
            {
                group.IsExpanded = true;

                // Get the ListViewItem container for the group
                var groupContainer = ListViewItems.ItemContainerGenerator.ContainerFromItem(groupItem) as System.Windows.Controls.ListViewItem;
                if (groupContainer == null)
                    return;

                // Search visual tree for the Expander
                var expander = FindVisualChild<Expander>(groupContainer);
                if (expander == null)
                    return;

                // Search inside the Expander for the inner ListView
                var innerListView = FindVisualChild<System.Windows.Controls.ListView>(expander);
                if (innerListView != null)
                {
                    innerListView.ScrollIntoView(displayItem);
                }
            }
            else
            {
                ListViewItems.ScrollIntoView(displayItem);
            }
        }

        private void ButtonPushUp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is DisplayItem item)
            {
                _isHandlingSelection = true;

                try
                {
                    if (item is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var displayItem in groupDisplayItem.DisplayItems)
                        {
                            displayItem.Selected = false;
                        }
                    }

                    SharedModel.Instance.PushDisplayItemBy(item, -1);
                    ScrollToView(item);


                }
                finally
                {
                    _isHandlingSelection = false;
                }
            }
        }

        private void ButtonPushDown_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is DisplayItem item)
            {
                _isHandlingSelection = true;

                try
                {
                    if (item is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var displayItem in groupDisplayItem.DisplayItems)
                        {
                            displayItem.Selected = false;
                        }
                    }

                    SharedModel.Instance.PushDisplayItemBy(item, 1);
                    ScrollToView(item);
                }
                finally
                {
                    _isHandlingSelection = false;
                }
            }
        }

        private void ButtonPushBack_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is DisplayItem item)
            {
                _isHandlingSelection = true;

                try
                {
                    if (item is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var displayItem in groupDisplayItem.DisplayItems)
                        {
                            displayItem.Selected = false;
                        }
                    }

                    SharedModel.Instance.PushDisplayItemToTop(item);
                    ScrollToView(item);
                }
                finally
                {
                    _isHandlingSelection = false;
                }
            }
        }

        private void ButtonPushFront_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is DisplayItem item)
            {
                _isHandlingSelection = true;

                try
                {
                    if (item is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var displayItem in groupDisplayItem.DisplayItems)
                        {
                            displayItem.Selected = false;
                        }
                    }

                    SharedModel.Instance.PushDisplayItemToEnd(item);
                    ScrollToView(item);
                }
                finally
                {
                    _isHandlingSelection = false;
                }
            }
        }

        private void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.RemoveDisplayItem(SelectedItem);
            }
        }

        private async void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            await SharedModel.Instance.ReloadDisplayItems();
            _displayItemsViewSource?.View?.Refresh();
        }

        private void ButtonSave_Click(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
            SharedModel.Instance.SaveDisplayItems();
        }

        private void ButtonNewText_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new TextDisplayItem("Custom Text", selectedProfile)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color
                };
                SharedModel.Instance.AddDisplayItem(item);
            }


        }

        private void ButtonNewImage_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile != null)
            {
                var item = new ImageDisplayItem("Image", SharedModel.Instance.SelectedProfile);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewClock_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new ClockDisplayItem("Clock", selectedProfile)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color

                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewCalendar_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new CalendarDisplayItem("Calendar", selectedProfile)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color
                };
                SharedModel.Instance.AddDisplayItem(item);
            }

        }

        private void ButtonDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem is DisplayItem selectedItem)
            {
                var item = (DisplayItem)selectedItem.Clone();
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.PushDisplayItemTo(item, selectedItem);
                item.Selected = true;
            }
        }

        private bool _isHandlingSelection;

        private void ListViewItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isHandlingSelection || sender is not System.Windows.Controls.ListView listView)
                return;

            Logger.Debug("ListViewItems_SelectionChanged - {Count} SelectedItems", listView.SelectedItems.Count);

            _isHandlingSelection = true;
            try
            {

                if (listView.SelectedItems.Count == 0)
                {
                    return;
                }

                foreach (var selectedItem in listView.SelectedItems)
                {
                    if (selectedItem is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var item in groupDisplayItem.DisplayItems)
                        {
                            item.Selected = true;
                        }
                    }
                }

                var selectedItems = listView.SelectedItems.Cast<DisplayItem>().ToList();

                if (selectedItems.Count != 0)
                    listView.ScrollIntoView(selectedItems.Last());

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    return;
                }

                foreach (var item in SharedModel.Instance.DisplayItems)
                {
                    if (item != listView.SelectedItem)
                    {
                        if (item is GroupDisplayItem group)
                        {
                            foreach (var item1 in group.DisplayItems)
                            {
                                item1.Selected = false;
                            }
                        }
                        else
                        {
                            item.Selected = false;
                        }
                    }
                }
            }
            finally
            {
                SharedModel.Instance.NotifySelectedItemChange();
                _isHandlingSelection = false;
                Logger.Debug("ListViewItems_SelectionChanged - finally");
            }
        }

        private void ListViewGroupItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isHandlingSelection || sender is not System.Windows.Controls.ListView innerListView)
                return;

            Log.Debug("ListViewGroupItems_SelectionChanged - {Count} SelectedItems", innerListView.SelectedItems.Count);

            _isHandlingSelection = true;

            try
            {
                var selectedItems = innerListView.SelectedItems.Cast<DisplayItem>().ToList();

                if (selectedItems.Count != 0)
                    innerListView.ScrollIntoView(selectedItems.Last());

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    return;
                }

                foreach (var item in SharedModel.Instance.SelectedItems)
                {
                    if (!innerListView.SelectedItems.Contains(item))
                    {
                        item.Selected = false;
                    }
                }

            }
            finally
            {
                SharedModel.Instance.NotifySelectedItemChange();
                _isHandlingSelection = false;
                Log.Debug("ListViewGroupItems_SelectionChanged - finally");
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (e.ChangedButton != MouseButton.Left)
                return;

            if (sender is not Border border)
                return;

            var dataItem = border.DataContext;
            var listViewItem = FindAncestor<System.Windows.Controls.ListViewItem>(border);
            if (listViewItem == null)
                return;

            var listView = ItemsControl.ItemsControlFromItemContainer(listViewItem) as System.Windows.Controls.ListView;
            if (listView == null)
                return;

            // Handle modifier keys
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (shift)
            {
                // Select range from last selected to current
                int currentIndex = listView.Items.IndexOf(dataItem);
                int anchorIndex = listView.SelectedIndex;

                if (anchorIndex >= 0 && currentIndex >= 0)
                {
                    int start = Math.Min(anchorIndex, currentIndex);
                    int end = Math.Max(anchorIndex, currentIndex);

                    listView.SelectedItems.Clear();
                    for (int i = start; i <= end; i++)
                    {
                        if (listView.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.ListViewItem item)
                            item.IsSelected = true;
                    }
                }
            }
            else if (ctrl)
            {
                // Toggle selection
                listViewItem.IsSelected = !listViewItem.IsSelected;
            }
            else
            {
                // Normal click: clear others and select this
                listView.SelectedItems.Clear();
                listViewItem.IsSelected = true;
            }

            listViewItem.BringIntoView();
        }

        public static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                    return target;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void InnerListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!e.Handled)
            {
                e.Handled = true;

                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };

                var parent = ((Control)sender).Parent as UIElement;
                while (parent != null && !(parent is ScrollViewer))
                {
                    parent = VisualTreeHelper.GetParent(parent) as UIElement;
                }

                parent?.RaiseEvent(eventArg);
            }
        }

        private GroupDisplayItem? GetGroupFromCollection(object? collection)
        {
            if (collection == null || collection == SharedModel.Instance.DisplayItems)
                return null;

            // Check if the collection is a view of the main collection
            if (collection is ListCollectionView view && view.SourceCollection == SharedModel.Instance.DisplayItems)
                return null;

            foreach (var item in SharedModel.Instance.DisplayItems)
            {
                if (item is GroupDisplayItem group && group.DisplayItems == collection)
                    return group;
            }
            return null;
        }

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is DisplayItem sourceItem)
            {
                var targetItem = dropInfo.TargetItem as DisplayItem;

                // Don't allow dropping an item onto itself
                if (targetItem != null && sourceItem == targetItem)
                {
                    dropInfo.Effects = DragDropEffects.None;
                    return;
                }

                // Get parent groups
                var sourceParent = SharedModel.Instance.GetParent(sourceItem);
                var targetParentGroup = GetGroupFromCollection(dropInfo.TargetCollection);

                // Check if source item is from a locked group
                if (sourceParent is GroupDisplayItem sourceGroup && sourceGroup.IsLocked)
                {
                    // Allow reordering within the same locked group
                    if (targetParentGroup == sourceGroup)
                    {
                        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                        dropInfo.Effects = DragDropEffects.Move;
                        return;
                    }

                    // Don't allow dragging items out of locked groups
                    dropInfo.Effects = DragDropEffects.None;
                    return;
                }

                // Check if target is in a locked group
                if (targetParentGroup != null && targetParentGroup.IsLocked)
                {
                    // Don't allow dropping items into locked groups
                    dropInfo.Effects = DragDropEffects.None;
                    return;
                }

                // Check if we're dragging a group
                if (sourceItem is GroupDisplayItem)
                {
                    // If target is also a group, prevent drop
                    if (targetItem is GroupDisplayItem)
                    {
                        dropInfo.Effects = DragDropEffects.None;
                        return;
                    }

                    // Check if the target collection is not the main collection
                    // If it's not, then it must be a group's inner collection
                    if (dropInfo.TargetCollection != null &&
                        dropInfo.TargetCollection != SharedModel.Instance.DisplayItems &&
                        !(dropInfo.TargetCollection is ListCollectionView view && view.SourceCollection == SharedModel.Instance.DisplayItems))
                    {
                        // We're trying to drop a group inside another group
                        dropInfo.Effects = DragDropEffects.None;
                        return;
                    }
                }
                else
                {
                    // We're dragging a regular item (not a group)
                    // Allow dropping into groups (even empty ones)
                    if (targetItem is GroupDisplayItem groupItem)
                    {
                        // Check if the group is locked
                        if (groupItem.IsLocked)
                        {
                            dropInfo.Effects = DragDropEffects.None;
                            return;
                        }

                        // Allow dropping items into groups
                        dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                        dropInfo.Effects = DragDropEffects.Move;
                        return;
                    }
                }

                // Allow the drop for all other cases
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        private readonly DefaultDropHandler dropHandler = new();

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is DisplayItem sourceItem)
            {
                var targetItem = dropInfo.TargetItem as DisplayItem;

                // Don't allow dropping an item onto itself
                if (targetItem != null && sourceItem == targetItem)
                {
                    return;
                }

                // Get parent groups
                var sourceParent = SharedModel.Instance.GetParent(sourceItem);
                var targetParentGroup = GetGroupFromCollection(dropInfo.TargetCollection);

                // Check if source item is from a locked group
                if (sourceParent is GroupDisplayItem sourceGroup && sourceGroup.IsLocked)
                {
                    // Allow reordering within the same locked group
                    if (targetParentGroup == sourceGroup)
                    {
                        dropHandler.Drop(dropInfo);
                        return;
                    }

                    // Don't allow dragging items out of locked groups
                    return;
                }

                // Check if target is in a locked group
                if (targetParentGroup != null && targetParentGroup.IsLocked)
                {
                    // Don't allow dropping items into locked groups
                    return;
                }

                // Check if we're dragging a group
                if (sourceItem is GroupDisplayItem)
                {
                    // If target is also a group, prevent drop
                    if (targetItem is GroupDisplayItem)
                    {
                        return;
                    }

                    // Check if the target collection is not the main collection
                    // If it's not, then it must be a group's inner collection
                    if (dropInfo.TargetCollection != null &&
                        dropInfo.TargetCollection != SharedModel.Instance.DisplayItems &&
                        !(dropInfo.TargetCollection is ListCollectionView view && view.SourceCollection == SharedModel.Instance.DisplayItems))
                    {
                        // We're trying to drop a group inside another group
                        return;
                    }
                }
                else
                {
                    // We're dragging a regular item (not a group)
                    // Special handling for dropping into empty groups
                    if (targetItem is GroupDisplayItem groupItem)
                    {
                        // Check if the group is locked
                        if (groupItem.IsLocked)
                        {
                            return;
                        }

                        // Move the item into the group
                        SharedModel.Instance.RemoveDisplayItem(sourceItem);
                        groupItem.DisplayItems.Add(sourceItem);
                        return;
                    }
                }

                // Use the default drop handler for all other cases
                dropHandler.Drop(dropInfo);
            }
        }
    }
}
