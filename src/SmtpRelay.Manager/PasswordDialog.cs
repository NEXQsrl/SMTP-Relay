using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmtpRelay.Manager;

/// <summary>Small modal dialog that asks for a new password twice, in the style of the console.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _first = new() { Margin = new Thickness(0, 0, 0, 14) };
    private readonly PasswordBox _second = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _error;
    private readonly Func<string, string?> _validate;

    /// <param name="validate">Returns what is wrong with the password, or null when it can be used.</param>
    public PasswordDialog(Window owner, string title, string message, Func<string, string?> validate)
    {
        _validate = validate;
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

        root.Children.Add(new TextBlock { Text = "New password", Style = (Style)FindResource("Label") });
        root.Children.Add(_first);
        root.Children.Add(new TextBlock { Text = "Type it again", Style = (Style)FindResource("Label") });
        root.Children.Add(_second);

        _error = new TextBlock
        {
            Foreground = (Brush)FindResource("Danger"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Change password", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _first.Focus();
    }

    public string Password => _first.Password;

    private void Accept()
    {
        var problem = _first.Password != _second.Password
            ? "The two passwords are different."
            : _validate(_first.Password);
        if (problem is null)
        {
            DialogResult = true;
            return;
        }

        _error.Text = problem;
        _error.Visibility = Visibility.Visible;
        _first.Focus();
    }
}
