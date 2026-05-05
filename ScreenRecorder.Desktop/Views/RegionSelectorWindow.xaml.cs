using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenRecorder.Desktop.Models;

namespace ScreenRecorder.Desktop.Views;

public partial class RegionSelectorWindow : Window
{
    private System.Windows.Point startPoint;
    private bool isDragging;

    public RegionSelectorWindow()
    {
        InitializeComponent();
    }

    public CaptureRegion? SelectedRegion { get; private set; }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        startPoint = e.GetPosition(this);
        isDragging = true;
        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, startPoint.X);
        Canvas.SetTop(SelectionRectangle, startPoint.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        RootCanvas.CaptureMouse();
    }

    private void RootCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        var x = Math.Min(startPoint.X, current.X);
        var y = Math.Min(startPoint.Y, current.Y);
        var w = Math.Abs(current.X - startPoint.X);
        var h = Math.Abs(current.Y - startPoint.Y);

        Canvas.SetLeft(SelectionRectangle, x);
        Canvas.SetTop(SelectionRectangle, y);
        SelectionRectangle.Width = w;
        SelectionRectangle.Height = h;
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        isDragging = false;
        RootCanvas.ReleaseMouseCapture();
        var current = e.GetPosition(this);

        var x = (int)Math.Min(startPoint.X, current.X);
        var y = (int)Math.Min(startPoint.Y, current.Y);
        var w = (int)Math.Abs(current.X - startPoint.X);
        var h = (int)Math.Abs(current.Y - startPoint.Y);

        if (w > 10 && h > 10)
        {
            SelectedRegion = new CaptureRegion(x, y, w, h);
            DialogResult = true;
        }
        else
        {
            SelectedRegion = null;
            DialogResult = false;
        }
    }
}
