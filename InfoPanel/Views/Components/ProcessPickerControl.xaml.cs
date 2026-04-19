using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InfoPanel.Views.Components
{
    public partial class ProcessPickerControl : UserControl
    {
        private sealed record ProcessEntry(string ProcessName);

        private List<ProcessEntry> _allProcesses = new();

        public string? SelectedProcessName { get; private set; }

        public event EventHandler? SelectionChanged;
        public event EventHandler? ItemActivated;

        public ProcessPickerControl()
        {
            InitializeComponent();
            LoadProcesses();
        }

        private void LoadProcesses()
        {
            try
            {
                var currentPid = Process.GetCurrentProcess().Id;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == currentPid) continue;
                        if (string.IsNullOrWhiteSpace(p.ProcessName)) continue;
                        names.Add(p.ProcessName);
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
                _allProcesses = names
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new ProcessEntry(n))
                    .ToList();
                ApplyFilter();
            }
            catch
            {
                _allProcesses = new List<ProcessEntry>();
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            var filter = FilterTextBox?.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                ProcessListView.ItemsSource = _allProcesses;
            }
            else
            {
                ProcessListView.ItemsSource = _allProcesses
                    .Where(p => p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ProcessListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedProcessName = ProcessListView.SelectedItem is ProcessEntry entry ? entry.ProcessName : null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ProcessListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProcessListView.SelectedItem is ProcessEntry entry)
            {
                SelectedProcessName = entry.ProcessName;
                ItemActivated?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
