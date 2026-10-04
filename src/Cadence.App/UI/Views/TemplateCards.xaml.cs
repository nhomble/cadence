using System.Windows.Controls;

namespace Cadence.App.UI.Views;

/// <summary>The grid of template cards (nine, three per row) shared by the empty state and the PLUS sheet. Its DataContext is the card list.</summary>
internal sealed partial class TemplateCards : UserControl
{
    public TemplateCards()
    {
        InitializeComponent();
    }
}
