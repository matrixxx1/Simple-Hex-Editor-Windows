using System;
using System.Windows;

namespace SimpleHexEditor;

public partial class AboutWindow : Window
{
    public AboutWindow(TrialManager trialManager, string version)
    {
        InitializeComponent();
        VersionText.Text = version;
        LicenseText.Text = trialManager.IsFullVersion
            ? "Current license: Full"
            : $"Current license: Trial ({trialManager.DaysRemaining} days remaining)";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
