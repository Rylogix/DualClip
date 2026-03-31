using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfGrid = System.Windows.Controls.Grid;
using WpfRowDefinition = System.Windows.Controls.RowDefinition;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfWrapPanel = System.Windows.Controls.WrapPanel;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushConverter = System.Windows.Media.BrushConverter;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace DualClip.App;

internal sealed class LogViewerWindow : Window
{
    private readonly System.Windows.Controls.TextBox _logTextBox;
    private readonly WpfTextBlock _subtitleBlock;

    public LogViewerWindow()
    {
        Title = "DualClip Logs";
        Width = 940;
        Height = 640;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (WpfBrush)new WpfBrushConverter().ConvertFromString("#0F1318")!;
        Foreground = (WpfBrush)new WpfBrushConverter().ConvertFromString("#EEF2FA")!;

        var root = new WpfGrid
        {
            Margin = new Thickness(16),
        };
        root.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new WpfRowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBlock = new WpfTextBlock
        {
            Text = "Session Logs",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        };
        WpfGrid.SetRow(titleBlock, 0);
        root.Children.Add(titleBlock);

        _subtitleBlock = new WpfTextBlock
        {
            Margin = new Thickness(0, 6, 0, 14),
            Foreground = (WpfBrush)new WpfBrushConverter().ConvertFromString("#98A7BF")!,
            TextWrapping = TextWrapping.Wrap,
        };
        RefreshSubtitle();
        WpfGrid.SetRow(_subtitleBlock, 1);
        root.Children.Add(_subtitleBlock);

        var contentGrid = new WpfGrid();
        contentGrid.RowDefinitions.Add(new WpfRowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });

        _logTextBox = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new WpfFontFamily("Cascadia Code"),
            FontSize = 12,
            Background = (WpfBrush)new WpfBrushConverter().ConvertFromString("#11161C")!,
            Foreground = (WpfBrush)new WpfBrushConverter().ConvertFromString("#FFFFFF")!,
            BorderBrush = (WpfBrush)new WpfBrushConverter().ConvertFromString("#2A3340")!,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
        };
        WpfGrid.SetRow(_logTextBox, 0);
        contentGrid.Children.Add(_logTextBox);

        var actions = new WpfWrapPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var refreshButton = new WpfButton
        {
            Content = "Refresh",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6),
        };
        refreshButton.Click += (_, _) => RefreshLogContent();
        actions.Children.Add(refreshButton);

        var copyPathButton = new WpfButton
        {
            Content = "Copy Log Path",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6),
        };
        copyPathButton.Click += CopyPathButton_Click;
        actions.Children.Add(copyPathButton);

        var downloadButton = new WpfButton
        {
            Content = "Download",
            Padding = new Thickness(14, 6, 14, 6),
        };
        downloadButton.Click += DownloadButton_Click;
        actions.Children.Add(downloadButton);

        WpfGrid.SetRow(actions, 1);
        contentGrid.Children.Add(actions);

        WpfGrid.SetRow(contentGrid, 2);
        root.Children.Add(contentGrid);

        Content = root;
        Loaded += (_, _) => RefreshLogContent();
    }

    private void RefreshLogContent()
    {
        RefreshSubtitle();
        _logTextBox.Text = AppLog.ReadCurrentLog();
        _logTextBox.CaretIndex = 0;
        _logTextBox.ScrollToHome();
    }

    private void RefreshSubtitle()
    {
        _subtitleBlock.Text = $"Current log file: {AppLog.LogFilePath}{Environment.NewLine}Session code: {AppLog.SessionCode}";
    }

    private void CopyPathButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(AppLog.LogFilePath);
            System.Windows.MessageBox.Show(this, "The current log file path was copied to the clipboard.", "Log Path Copied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Could Not Copy Log Path", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export DualClip Logs",
            Filter = "Text Files|*.txt|All Files|*.*",
            FileName = $"DualClip-log-{DateTime.Now:yyyyMMdd_HHmmss}-{AppLog.SessionCode}.txt",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await AppLog.ExportCurrentLogAsync(dialog.FileName);
            System.Windows.MessageBox.Show(this, "The current DualClip log was exported successfully.", "Logs Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Could Not Export Logs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
