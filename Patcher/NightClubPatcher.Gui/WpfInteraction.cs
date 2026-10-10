using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Gui;

/// <summary>Blocking prompts marshalled to the UI thread; callable from background workflows.</summary>
public sealed class WpfInteraction : IUserInteraction
{
    private readonly Window _owner;

    public WpfInteraction(Window owner) => _owner = owner;

    public int Choose(string title, string message, IReadOnlyList<string> options) =>
        _owner.Dispatcher.Invoke(() => ChoiceDialog.Show(_owner, title, message, options));

    public void Notify(string title, string message) =>
        _owner.Dispatcher.Invoke(() => MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information));
}

/// <summary>Message box with arbitrary option buttons. Closing the window means "cancel" (-1).</summary>
public static class ChoiceDialog
{
    public static int Show(Window owner, string title, string message, IReadOnlyList<string> options)
    {
        var result = -1;
        var win = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Bg"],
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Fg"],
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["Fg"],
            Margin = new Thickness(0, 0, 0, 16),
        });
        // Long options (e.g. device labels) are listed vertically, short ones as a button row.
        var vertical = options.Any(o => o.Length > 32);
        Panel buttons = vertical
            ? new StackPanel()
            : new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = options[i],
                Margin = vertical ? new Thickness(0, 3, 0, 3) : new Thickness(6, 0, 0, 0),
                MinWidth = 90,
                HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            };
            if (i == 0) b.IsCancel = true;
            b.Click += (_, _) => { result = index; win.Close(); };
            buttons.Children.Add(b);
        }
        root.Children.Add(buttons);
        win.Content = root;
        win.ShowDialog();
        return result;
    }
}
