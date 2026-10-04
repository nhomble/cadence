using System.Windows;
using System.Windows.Controls;

namespace Cadence.App.UI.Views;

/// <summary>The content of a confirmation or message dialog. Raises <see cref="Finished"/> with the answer; the host window closes itself.</summary>
internal sealed partial class ConfirmPanel : UserControl
{
    readonly string? _word;

    /// <param name="title">The heading.</param>
    /// <param name="message">The explanation.</param>
    /// <param name="confirmText">The confirm button's text; null shows a single "OK" button (a message).</param>
    /// <param name="danger">Paints the confirm button Red.</param>
    /// <param name="typedWord">When set, the confirm button stays disabled until the user types exactly this word.</param>
    /// <param name="cancelText">The cancel button's text (default "Cancel").</param>
    public ConfirmPanel(string title, string message, string? confirmText, bool danger, string? typedWord = null, string? cancelText = null)
    {
        InitializeComponent();
        if (cancelText is not null) CancelButton.Content = cancelText;
        TitleText.Text = title;
        MessageText.Text = message;
        _word = typedWord;
        if (confirmText is null)
        {
            ConfirmButton.Content = "OK";
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else ConfirmButton.Content = confirmText;
        if (danger || typedWord is not null) ConfirmButton.Style = (Style)FindResource("DangerPrimary");
        if (typedWord is not null)
        {
            TypedBox.Visibility = Visibility.Visible;
            TypedHint.Text = "Type " + typedWord + " to confirm";
            ConfirmButton.IsEnabled = false;
        }
    }

    public event Action<bool>? Finished;

    public void FocusFirst()
    {
        if (_word is not null) TypedText.Focus();
        else (CancelButton.Visibility == Visibility.Visible ? CancelButton : ConfirmButton).Focus();
    }

    void OnTypedChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = string.Equals(TypedText.Text.Trim(), _word, StringComparison.Ordinal);

    void OnCancel(object sender, RoutedEventArgs e) => Finished?.Invoke(false);

    void OnConfirm(object sender, RoutedEventArgs e) => Finished?.Invoke(true);
}
