using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsConan.Core;

namespace VsConan;

/// <summary>Lets the user pick which Conan host profiles to install, and the build profile.</summary>
internal sealed class ProfileSelectionDialog : DialogWindow
{
    private readonly List<CheckBox> _hostProfiles = new();
    private readonly ComboBox _buildProfile;

    /// <param name="firstRunProject">
    /// When set, the dialog is the one-off prompt shown when the folder is first opened: it explains
    /// what VsConan found and offers Install / Not now instead of OK / Cancel.
    /// </param>
    public ProfileSelectionDialog(IReadOnlyList<string> profiles, VsConanSettings current, ConanProject? firstRunProject = null)
    {
        Title = firstRunProject is null ? "Conan Profiles" : "VsConan: Install Conan Dependencies";
        Width = firstRunProject is null ? 360 : 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        HasMinimizeButton = false;
        HasMaximizeButton = false;
        UseVsTheme();

        var panel = new StackPanel { Margin = new Thickness(12) };
        if (firstRunProject is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"This folder has a {System.IO.Path.GetFileName(firstRunProject.ConanfilePath)} and a CMakeLists.txt. "
                    + "VsConan can install its Conan dependencies so Visual Studio's CMake integration can configure "
                    + "and build it. CMake is paused until you choose.\n\n"
                    + "You can change this later from Tools > Conan.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Install dependencies for these host profiles:",
            Margin = new Thickness(0, 0, 0, 6),
        });

        var ok = new Button { Content = firstRunProject is null ? "OK" : "Install", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };

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
        var cancel = new Button { Content = firstRunProject is null ? "Cancel" : "Not now", IsCancel = true, MinWidth = 75 };

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

    /// <summary>
    /// Follows the VS colour theme (e.g. dark mode). These are references to VS's own dialog
    /// resources, so the dialog also updates if the theme changes while it's open.
    /// </summary>
    private void UseVsTheme()
    {
        SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
        SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

        UseThemedStyle(typeof(Button), VsResourceKeys.ThemedDialogButtonStyleKey);
        UseThemedStyle(typeof(CheckBox), VsResourceKeys.ThemedDialogCheckBoxStyleKey);
        UseThemedStyle(typeof(ComboBox), VsResourceKeys.ThemedDialogComboBoxStyleKey);
    }

    /// <summary>Makes VS's themed style the implicit style for <paramref name="controlType"/> within this dialog.</summary>
    private void UseThemedStyle(Type controlType, object styleKey)
    {
        if (TryFindResource(styleKey) is Style style)
        {
            Resources[controlType] = style;
        }
    }
}
