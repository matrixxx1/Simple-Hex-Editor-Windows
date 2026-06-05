using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SimpleHexEditor;

public partial class MainWindow : Window
{
    private readonly string[] _actions = new[] { "Open File", "Search Bytes", "Export Hex Notes" };
    private readonly string[] _features = new[] { "File byte inspection workflow", "Offset and hex preview concept", "Search surface", "Careful edit planning" };

    public MainWindow()
    {
        InitializeComponent();
        AddActivity("Initial Store app scaffold loaded.");
        AddActivity("Configured scope: " + string.Join("; ", _features.Take(2)) + ".");
    }

    private void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        var label = (sender as Button)?.Content?.ToString() ?? "Action";
        var stepNumber = Array.IndexOf(_actions, label) + 1;
        AddActivity(stepNumber > 0
            ? $"{label}: starter workflow step {stepNumber} queued."
            : $"{label}: starter workflow queued.");
    }

    private void AddActivity(string message)
    {
        ActivityList.Items.Insert(0, $"{DateTime.Now:t} - {message}");
        StatusText.Text = message;
    }
}