using System.Collections.ObjectModel;
using Mendrain.Core.Services;
using Mendrain.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mendrain.Views;

/// <summary>A row in the saved-passwords dropdown. Holds no password, only its label.</summary>
public sealed record SavedPasswordRow(SavedPassword Entry, string Detail)
{
    public string RemoveName => $"Remove saved password for {Entry.Label}";

    /// <summary>Used as the list item's accessible name.</summary>
    public override string ToString() => $"{Entry.Label}, {Detail}";
}

/// <summary>
/// Asks for an archive password. The field combines the password box, a show/hide button and a dropdown
/// of saved passwords; picking one fills the field without ever showing the password in the list.
/// </summary>
public sealed partial class PasswordDialog : ContentDialog
{
    private readonly string _archiveName;
    private readonly SavedPasswordStore _store;
    private readonly ObservableCollection<SavedPasswordRow> _rows = [];

    /// <param name="reason">Why a previous password did not work; null on the first prompt.</param>
    public PasswordDialog(string archiveName, string? reason, string cancelText, SavedPasswordStore store)
    {
        InitializeComponent();
        _archiveName = archiveName;
        _store = store;
        CloseButtonText = cancelText;
        MessageText.Text = $"“{archiveName}” is encrypted. Enter its password to continue.";
        if (!string.IsNullOrEmpty(reason))
            ShowError(reason);
        SavedList.ItemsSource = _rows;
        LoadSaved();
        Opened += (_, _) => PasswordInput.Focus(FocusState.Programmatic);
    }

    public PasswordAnswer Answer => new(PasswordInput.Password, SaveCheck.IsChecked == true);

    private void LoadSaved()
    {
        _rows.Clear();
        // Passwords saved for this archive first; otherwise most recently saved first.
        foreach (var entry in _store.List().OrderByDescending(p => p.IsFor(_archiveName)))
            _rows.Add(new SavedPasswordRow(entry, Describe(entry)));

        SavedButton.IsEnabled = _rows.Count > 0;
        var name = _rows.Count > 0 ? "Saved passwords" : "Saved passwords (none saved yet)";
        AutomationProperties.SetName(SavedButton, name);
        ToolTipService.SetToolTip(SavedButton, name);

        SavedNoteText.Text = _store.Problem ?? "";
        SavedNoteText.Visibility = Format.ShowIf(_store.Problem);
    }

    private string Describe(SavedPassword entry)
    {
        if (entry.IsFor(_archiveName))
            return "Saved for this archive";
        var others = entry.Archives.Count - 1;
        return others > 0
            ? $"Also used for {Format.Count(others, "other archive", "other archives")}"
            : $"Saved {Format.Date(entry.Saved.LocalDateTime)}";
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- password field

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e) =>
        IsPrimaryButtonEnabled = PasswordInput.Password.Length > 0;

    private void PasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Like a combo box: Down arrow or F4 opens the saved passwords.
        if (e.Key is VirtualKey.Down or VirtualKey.F4 && _rows.Count > 0)
        {
            OpenSaved();
            e.Handled = true;
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        SetRevealed(PasswordInput.PasswordRevealMode != PasswordRevealMode.Visible);
        // After a mouse click, typing and Enter should go back to the password, not the button.
        if (RevealButton.FocusState == FocusState.Pointer)
            PasswordInput.Focus(FocusState.Programmatic);
    }

    private void SetRevealed(bool visible)
    {
        PasswordInput.PasswordRevealMode = visible ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        RevealSlash.Visibility = Format.Show(visible);
        var name = visible ? "Hide password" : "Show password";
        AutomationProperties.SetName(RevealButton, name);
        ToolTipService.SetToolTip(RevealButton, name);
    }

    // ---------------------------------------------------------------- saved passwords

    private void SavedButton_Click(object sender, RoutedEventArgs e) => OpenSaved();

    private void OpenSaved()
    {
        if (_rows.Count == 0)
            return;
        // Drop down under the whole field, as wide as the field.
        SavedList.Width = Math.Max(260, PasswordField.ActualWidth - 10);
        FlyoutBase.ShowAttachedFlyout(PasswordField);
    }

    private void SavedFlyout_Opened(object sender, object e) => FocusRow(0);

    private void FocusRow(int index) =>
        DispatcherQueue.TryEnqueue(() => (SavedList.ContainerFromIndex(index) as Control)?.Focus(FocusState.Programmatic));

    private void SavedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedPasswordRow row)
            UseSaved(row);
    }

    private void UseSaved(SavedPasswordRow row)
    {
        SavedFlyout.Hide();
        if (_store.Reveal(row.Entry.Id) is not { } password)
        {
            ShowError($"The saved password for “{row.Entry.Label}” could not be read. It may have been saved by another Windows account. Remove it from the list and enter the password instead.");
            PasswordInput.Focus(FocusState.Programmatic);
            return;
        }
        SetRevealed(false);
        PasswordInput.Password = password;
        // It is saved already; checking the box again only moves this archive to the top of that entry.
        SaveCheck.IsChecked = false;
        PasswordInput.Focus(FocusState.Programmatic);
    }

    private void SavedList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem { Content: SavedPasswordRow row })
        {
            Remove(row);
            e.Handled = true;
        }
    }

    private void RemoveSaved_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SavedPasswordRow row)
            Remove(row);
    }

    private void Remove(SavedPasswordRow row)
    {
        var index = _rows.IndexOf(row);
        if (!_store.Remove(row.Entry.Id))
        {
            SavedFlyout.Hide();
            ShowError("The saved password could not be removed.");
            return;
        }
        LoadSaved();
        if (_rows.Count == 0)
        {
            SavedFlyout.Hide();
            PasswordInput.Focus(FocusState.Programmatic);
        }
        else
        {
            FocusRow(Math.Min(index, _rows.Count - 1));
        }
    }
}
