using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GpuxMine.App.Controls;

/// <summary>
/// Lets the wheel keep working when one scrolling area sits inside another.
/// </summary>
/// <remarks>
/// <para>
/// A ScrollViewer swallows every wheel notch it receives, including the ones it
/// cannot act on. Put a list inside a page that also scrolls — which is what
/// this app does on every screen — and the wheel stops dead the moment the
/// pointer is over the list: the inner one has nowhere left to go and the outer
/// one never hears about it. The page looks frozen, and the only way down is to
/// drag the scrollbar by hand.
/// </para>
/// <para>
/// Attaching this forwards the notch to the parent when, and only when, the
/// inner area genuinely cannot use it: it is already at the top and the wheel
/// says up, already at the bottom and the wheel says down, or it has no
/// scrollable height at all. Anything the inner area can act on is left alone,
/// so a long list still scrolls by itself.
/// </para>
/// <para>
/// Applied per element rather than as an implicit style on ScrollViewer,
/// because that would also catch the ones inside ComboBox and ListBox
/// templates, where this behaviour is wrong.
/// </para>
/// </remarks>
public static class WheelScroll
{
    public static readonly DependencyProperty BubbleProperty =
        DependencyProperty.RegisterAttached(
            "Bubble", typeof(bool), typeof(WheelScroll),
            new PropertyMetadata(false, OnBubbleChanged));

    public static void SetBubble(DependencyObject element, bool value) =>
        element.SetValue(BubbleProperty, value);

    public static bool GetBubble(DependencyObject element) =>
        (bool)element.GetValue(BubbleProperty);

    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        // Preview, not the bubbling event: by the time the ordinary MouseWheel
        // event runs, the ScrollViewer has already marked it handled.
        viewer.PreviewMouseWheel -= Forward;
        if (e.NewValue is true) viewer.PreviewMouseWheel += Forward;
    }

    private static void Forward(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;

        bool nothingToScroll = viewer.ScrollableHeight <= 0;
        bool atTop = e.Delta > 0 && viewer.VerticalOffset <= 0;
        bool atBottom = e.Delta < 0 && viewer.VerticalOffset >= viewer.ScrollableHeight;

        // The inner area can use this one. Leave it be.
        if (!nothingToScroll && !atTop && !atBottom) return;

        e.Handled = true;

        if (VisualTreeHelper.GetParent(viewer) is not UIElement parent) return;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = viewer,
        });
    }
}
