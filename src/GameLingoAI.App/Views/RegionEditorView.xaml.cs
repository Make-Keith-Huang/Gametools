using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GameLingoAI.App.ViewModels;

namespace GameLingoAI.App.Views;

public partial class RegionEditorView : UserControl
{
    public RegionEditorView() => InitializeComponent();

    private RegionEditorViewModel? Editor => (DataContext as ShellViewModel)?.RegionEditor;

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        Editor?.SetPreviewViewport(eventArgs.NewSize.Width, eventArgs.NewSize.Height);

    private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        SelectSenderRegion(sender);
        Editor?.MoveSelectedByPreviewDips(eventArgs.HorizontalChange, eventArgs.VerticalChange);
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        SelectSenderRegion(sender);
        Editor?.ResizeSelectedByPreviewDips(eventArgs.HorizontalChange, eventArgs.VerticalChange);
    }

    private void RegionThumb_GotKeyboardFocus(object sender, RoutedEventArgs eventArgs) => SelectSenderRegion(sender);

    private void SelectSenderRegion(object sender)
    {
        if (sender is FrameworkElement { DataContext: EditableRegionViewModel region } && Editor is not null)
        {
            Editor.SelectedRegion = region;
        }
    }
}
