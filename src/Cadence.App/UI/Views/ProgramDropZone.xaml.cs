using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cadence.App.UI.Views;

/// <summary>
/// The drop zone of the editor: a dashed area with a Browse button, in three looks. Normal; <see cref="IsDragOver"/> (a file is being dragged
/// over: Accent border and "Release to use this file"); <see cref="IsRejected"/> (the last file couldn't be used: Red border, the reason is shown by the
/// editor right under the zone). The window owns the drag events (<see cref="FileDrop"/> on the main view) and sets <c>IsDragOver</c> through the
/// view model; this control only draws, and accepts drops so the pointer shows the right cursor over it.
/// </summary>
internal sealed partial class ProgramDropZone : UserControl
{
    public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.Register(
        nameof(IsDragOver), typeof(bool), typeof(ProgramDropZone), new PropertyMetadata(false, (d, _) => ((ProgramDropZone)d).Apply()));

    public static readonly DependencyProperty IsRejectedProperty = DependencyProperty.Register(
        nameof(IsRejected), typeof(bool), typeof(ProgramDropZone), new PropertyMetadata(false, (d, _) => ((ProgramDropZone)d).Apply()));

    public ProgramDropZone()
    {
        InitializeComponent();
        AllowDrop = true;
    }

    public bool IsDragOver
    {
        get => (bool)GetValue(IsDragOverProperty);
        set => SetValue(IsDragOverProperty, value);
    }

    public bool IsRejected
    {
        get => (bool)GetValue(IsRejectedProperty);
        set => SetValue(IsRejectedProperty, value);
    }

    void Apply()
    {
        bool over = IsDragOver;
        bool rejected = IsRejected && !over;
        string stroke = over ? "BlueBrush" : rejected ? "RedBrush" : "BorderBrush";
        Dash.Stroke = (Brush)FindResource(stroke);
        Dash.Fill = (Brush)FindResource(over ? "HighlightBrush" : "SurfaceBrush");
        Icon.Stroke = (Brush)FindResource(over ? "BlueBrush" : rejected ? "RedBrush" : "DimTextBrush");
        Title.Text = over ? "Release to use this file" : "Drop a program, shortcut or script here";
        Title.Foreground = (Brush)FindResource(over ? "BlueBrush" : "FgBrush");
    }
}
