using GongSolutions.Wpf.DragDrop;
using InfoPanel.Models;
using System;
using Serilog;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wpf.Ui.Controls;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for DisplayItems.xaml
    /// </summary>
    public partial class DisplayItems : UserControl, IDropTarget
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayItems>();
        private static DisplayItem? SelectedItem { get { return SharedModel.Instance.SelectedItem; } }

        private readonly ObservableCollection<DisplayItem> _filteredDisplayItems = [];
        public ObservableCollection<DisplayItem> FilteredDisplayItems
        {
            get => _filteredDisplayItems;
        }

        private string _searchText = string.Empty;

        public DisplayItems()
        {
            DataContext = this;
            InitializeComponent();
            Loaded += DisplayItems_Loaded;

            // Initialize with all display items
            UpdateFilteredItems();
        }

        private void DisplayItems_Loaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.PropertyChanged += Instance_PropertyChanged;
        }

        private void Instance_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedModel.Instance.SelectedItem))
            {
                Logger.Debug("SelectedItem changed");
                if (SelectedItem != null)
                {
                    var group = SharedModel.Instance.GetParent(SelectedItem);

                    if (group is GroupDisplayItem)
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
                // Update filtered items when display items or profile changes
                UpdateFilteredItems();
            }
        }

        private void UpdateFilteredItems()
        {
            if (SharedModel.Instance.DisplayItems == null)
            {
                FilteredDisplayItems.Clear();
                return;
            }

            // Use the dispatcher to update UI on a background thread for large lists
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrWhiteSpace(_searchText))
                {
                    // Reuse existing collection to avoid recreating
                    FilteredDisplayItems.Clear();
                    foreach (var item in SharedModel.Instance.DisplayItems)
                    {
                        FilteredDisplayItems.Add(item);

                        // Reset expanded state to default for groups (collapsed by default)
                        if (item is GroupDisplayItem group && !group.DisplayItems.Any(child => child.Selected))
                        {
                            group.IsExpanded = false;
                        }
                    }
                }
                else
                {
                    // Filter items based on search text
                    var searchLower = _searchText.ToLower();
                    var searchTerms = searchLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    
                    FilteredDisplayItems.Clear();

                    foreach (var item in SharedModel.Instance.DisplayItems)
                    {
                        if (item is GroupDisplayItem group)
                        {
                            // Check if group name matches all search terms
                            bool groupMatches = MatchesAllTerms(group.Name, searchTerms);

                            // Only check children if group doesn't match
                            bool hasMatchingChildren = false;
                            if (!groupMatches)
                            {
                                hasMatchingChildren = group.DisplayItems.Any(child =>
                                    MatchesAllTerms(child.Name, searchTerms) ||
                                    MatchesAllTerms(child.Kind, searchTerms));
                            }

                            if (groupMatches || hasMatchingChildren)
                            {
                                FilteredDisplayItems.Add(group);
                                // Expand the group to show matching items
                                group.IsExpanded = true;
                            }
                        }
                        else
                        {
                            // Regular item - check name and kind
                            if (MatchesAllTerms(item.Name, searchTerms) ||
                                MatchesAllTerms(item.Kind, searchTerms))
                            {
                                FilteredDisplayItems.Add(item);
                            }
                        }
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static bool MatchesAllTerms(string text, string[] searchTerms)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var textLower = text.ToLower();
            return searchTerms.All(term => textLower.Contains(term));
        }

        private void TextBoxSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
        {
            if (sender is AutoSuggestBox autoSuggestBox)
            {
                var currentText = autoSuggestBox.Text ?? string.Empty;

                // If text is cleared, reset the filter immediately
                if (string.IsNullOrWhiteSpace(currentText))
                {
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        _searchText = string.Empty;
                        UpdateFilteredItems();
                    }
                    autoSuggestBox.ItemsSource = null;
                    return;
                }

                // Provide suggestions based on item names (but don't filter the list)
                var suggestions = new List<string>();
                var searchLower = currentText.ToLower();

                // Add matching item names as suggestions
                foreach (var item in SharedModel.Instance.DisplayItems)
                {
                    if (item is GroupDisplayItem group)
                    {
                        if (group.Name?.ToLowerInvariant().Contains(searchLower, StringComparison.InvariantCultureIgnoreCase) ?? false)
                            suggestions.Add(group.Name);

                        foreach (var child in group.DisplayItems)
                        {
                            if (child.Name?.ToLowerInvariant().Contains(searchLower, StringComparison.InvariantCultureIgnoreCase) ?? false)
                                suggestions.Add(child.Name);
                        }
                    }
                    else
                    {
                        if (item.Name?.ToLower().Contains(searchLower) ?? false)
                            suggestions.Add(item.Name);
                    }
                }

                autoSuggestBox.ItemsSource = suggestions.Distinct().Take(5).ToList();
            }
        }

        private void TextBoxSearch_SuggestionChosen(object sender, RoutedEventArgs e)
        {
            // The AutoSuggestBox automatically updates its Text property when a suggestion is chosen
            // We just need to update our search
            if (sender is AutoSuggestBox autoSuggestBox)
            {
                _searchText = autoSuggestBox.Text ?? string.Empty;
                UpdateFilteredItems();
            }
        }

        private void TextBoxSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is AutoSuggestBox autoSuggestBox)
            {
                _searchText = autoSuggestBox.Text ?? string.Empty;
                UpdateFilteredItems();
                e.Handled = true;
            }
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

        private void ButtonGroup_Click(object sender, RoutedEventArgs e)
        {
            var groupDisplayItem = new GroupDisplayItem
            {
                Name = "New Group"
            };

            var selectedItem = SharedModel.Instance.SelectedItem;

            SharedModel.Instance.AddDisplayItem(groupDisplayItem);

            if (selectedItem is DisplayItem)
            {
                SharedModel.Instance.PushDisplayItemTo(groupDisplayItem, selectedItem);
            }

            ListViewItems.ScrollIntoView(groupDisplayItem);
        }

        private void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.LoadDisplayItems();
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
            if (collection == null || collection == SharedModel.Instance.DisplayItems || collection == FilteredDisplayItems)
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
                        dropInfo.TargetCollection != FilteredDisplayItems)
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
                        dropInfo.TargetCollection != FilteredDisplayItems)
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
