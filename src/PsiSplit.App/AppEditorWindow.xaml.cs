using System;
using System.IO;
using System.Windows;
using PsiSplit.Core.Models;

namespace PsiSplit.App;

public partial class AppEditorWindow : Window
{
    private readonly AppProfile _profile;

    public AppEditorWindow(AppProfile profile, bool isNew)
    {
        InitializeComponent();

        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        _profile = profile;
        HeaderText.Text = isNew ? "New app" : "Edit app";

        NameBox.Text = profile.Name;
        PathBox.Text = profile.Path;
        ArgsBox.Text = profile.Arguments;
        FullModeRadio.IsChecked = profile.Mode == TunnelMode.Full;
        FilteredModeRadio.IsChecked = profile.Mode != TunnelMode.Full;

        Loaded += (_, _) => NameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var path = PathBox.Text.Trim().Trim('"');

        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter an app name.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        if (path.Length == 0 || !File.Exists(path))
        {
            MessageBox.Show(this, "Specify an existing .exe file.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PathBox.Focus();
            return;
        }

        _profile.Name = name;
        _profile.Path = path;
        _profile.Arguments = ArgsBox.Text.Trim();
        _profile.Mode = FilteredModeRadio.IsChecked == true ? TunnelMode.Filtered : TunnelMode.Full;

        if (string.IsNullOrWhiteSpace(_profile.Id))
        {
            _profile.Id = Guid.NewGuid().ToString("N");
        }

        _profile.Normalize();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
