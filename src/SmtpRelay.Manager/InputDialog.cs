using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmtpRelay.Manager;

/// <summary>Small modal dialog with one or two text fields, in the style of the console.</summary>
public sealed class InputDialog : Window
{
    private readonly List<TextBox> _boxes = new();

    public InputDialog(Window owner, string title, string message, params (string Label, string Value)[] fields)
    {
        Owner = owner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = owner.FontFamily;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Sub"),
            Margin = new Thickness(0, 0, 0, 16)
        });

        foreach (var (label, value) in fields)
        {
            root.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
            var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 14) };
            _boxes.Add(box);
            root.Children.Add(box);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Send", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            var first = _boxes.FirstOrDefault(b => b.Text.Length == 0) ?? _boxes.FirstOrDefault();
            first?.Focus();
        };
    }

    public string this[int index] => _boxes[index].Text.Trim();
}
