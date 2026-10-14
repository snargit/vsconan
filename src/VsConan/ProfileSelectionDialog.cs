using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using VsConan.Core;

namespace VsConan;

/// <summary>Lets the user pick which Conan host profiles to install, and the build profile.</summary>
internal sealed class ProfileSelectionDialog : DialogWindow
{
    private readonly List<CheckBox> _hostProfiles = new();
    private readonly ComboBox _buildProfile;

    public ProfileSelectionDialog(IReadOnlyList<string> profiles, VsConanSettings current)
    {
        Title = "Conan Profiles";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        HasMinimizeButton = false;
        HasMaximizeButton = false;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "Install dependencies for these host profiles:",
            Margin = new Thickness(0, 0, 0, 6),
        });

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };

        foreach (var profile in profiles)
        {
            var checkBox = new CheckBox
            {
                Content = profile,
                IsChecked = current.HostProfiles.Contains(profile),
                Margin = new Thickness(0, 2, 0, 2),
            };
            checkBox.Checked += (_, _) => ok.IsEnabled = AnySelected();
            checkBox.Unchecked += (_, _) => ok.IsEnabled = AnySelected();
            _hostProfiles.Add(checkBox);
            panel.Children.Add(checkBox);
        }

        panel.Children.Add(new TextBlock { Text = "Build profile (-pr:b):", Margin = new Thickness(0, 12, 0, 4) });
        _buildProfile = new ComboBox
        {
            ItemsSource = profiles,
            SelectedItem = profiles.Contains(current.BuildProfile) ? current.BuildProfile : profiles.FirstOrDefault(),
        };
        panel.Children.Add(_buildProfile);

        ok.IsEnabled = AnySelected();
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    public VsConanSettings GetSettings() => new()
    {
        HostProfiles = _hostProfiles.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList(),
        BuildProfile = (string)_buildProfile.SelectedItem,
    };

    private bool AnySelected() => _hostProfiles.Any(c => c.IsChecked == true);
}
