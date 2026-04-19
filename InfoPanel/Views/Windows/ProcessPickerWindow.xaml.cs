using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace InfoPanel.Views.Windows
{
    public partial class ProcessPickerWindow : Window
    {
        public string? SelectedProcessName { get; private set; }

        private sealed record ProcessEntry(string ProcessName, int Id);

        public ProcessPickerWindow()
        {
            InitializeComponent();
            LoadProcesses();
            ProcessListView.SelectionChanged += (_, _) => OkButton.IsEnabled = ProcessListView.SelectedItem != null;
        }

        private void LoadProcesses()
        {
            try
            {
                var currentPid = Process.GetCurrentProcess().Id;
                var list = new List<ProcessEntry>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == currentPid) continue;
                        if (string.IsNullOrWhiteSpace(p.ProcessName)) continue;
                        list.Add(new ProcessEntry(p.ProcessName, p.Id));
                    }
                    catch { /* skip inaccessible */ }
                    finally
                    {
                        p.Dispose();
                    }
                }
                ProcessListView.ItemsSource = list.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not list processes: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadProcesses();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessListView.SelectedItem is ProcessEntry entry)
            {
                SelectedProcessName = entry.ProcessName;
                DialogResult = true;
            }
            Close();
        }

        private void ProcessListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProcessListView.SelectedItem != null)
                Ok_Click(sender, e);
        }
    }
}
