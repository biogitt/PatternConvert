using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace PatternConversionTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Keep the running-status log scrolled to the most recent entry so the
    // latest messages stay visible without manual scrolling.
    private void StatusLogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            box.ScrollToEnd();
    }
}