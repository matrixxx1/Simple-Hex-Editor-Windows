using System;
using System.IO;
using System.Windows;

namespace SimpleHexEditor;

public partial class LogViewerWindow : Window
{
    private readonly string _logPath;

    public LogViewerWindow(string logPath)
    {
        InitializeComponent();
        _logPath = logPath;
        RefreshLogs();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshLogs();
    }

    private void RefreshLogs()
    {
        if (File.Exists(_logPath))
            LogText.Text = File.ReadAllText(_logPath);
        else
            LogText.Text = $"No log entries yet. Expected log path: {_logPath}";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
