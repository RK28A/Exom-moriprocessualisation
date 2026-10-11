using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NightClubPatcher.Gui;

/// <summary>Error dialog with the message, expandable technical details and Copy / Open logs buttons.</summary>
public static class ErrorDialog
{
    public static void Show(Window? owner, string title, string message, string? details = null, string? logsFolder = null,
        bool isError = true)
    {
        var res = Application.Current.Resources;
        var win = new Window
        {
            Title = title,
            Width = 640,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = (Brush)res["Bg"],
        };
        if (owner != null) win.Owner = owner;
        App.ApplyChrome(win);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = (isError ? "✗  " : "✓  ") + title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)res[isError ? "Danger" : "Ok"],
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)res["Fg"],
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (!string.IsNullOrWhiteSpace(details))
        {
            root.Children.Add(new Expander
            {
                IsExpanded = !isError,
                Header = new TextBlock { Text = isError ? "Technical details" : "Details", Foreground = (Brush)res["Muted"] },
                Margin = new Thickness(0, 0, 0, 12),
                Content = new TextBox
                {
                    Text = details,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    Height = 260,
                    Background = (Brush)res["Panel"],
                    Foreground = (Brush)res["Fg"],
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 6, 0, 0),
                },
            });
        }

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Copy details" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{title}\n{message}\n\n{details}"); }
            catch { /* clipboard busy */ }
        };
        buttons.Children.Add(copy);
        if (logsFolder != null)
        {
            var logs = new Button { Content = "Open folder" };
            logs.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(logsFolder) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            buttons.Children.Add(logs);
        }
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90 };
        close.Click += (_, _) => win.Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        win.ShowDialog();
    }
}
