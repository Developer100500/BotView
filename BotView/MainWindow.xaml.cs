using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using BotView.Chart;

namespace BotView
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Point? lastMousePos = null;
        //private ChartView chartView;

        public MainWindow()
        {
            InitializeComponent();
            
            // Find the ChartView control
            //chartView = FindName("myChartView") as ChartView;
            if (chartView == null)
            {
                // If not found by name, find it in the visual tree
                chartView = FindVisualChild<ChartView>(this);
            }
            
            if (chartView != null)
            {
                // Add mouse event handlers
                chartView.MouseMove += ChartView_MouseMove;
                chartView.MouseWheel += ChartView_MouseWheel;
                chartView.MouseLeftButtonDown += ChartView_MouseLeftButtonDown;
                chartView.MouseLeftButtonUp += ChartView_MouseLeftButtonUp;
            }
        }

        private void ChartView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            lastMousePos = e.GetPosition(chartView);
            chartView.CaptureMouse();
        }

        private void ChartView_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            lastMousePos = null;
            chartView.ReleaseMouseCapture();
        }

        private void ChartView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && lastMousePos != null)
            {
                Point currentPos = e.GetPosition(chartView);
                double deltaX = currentPos.X - lastMousePos.Value.X;
                double deltaY = currentPos.Y - lastMousePos.Value.Y;
                
                // Pan the chart using pixel-based movement
                chartView.PanByPixels(deltaX, deltaY);
            }
            lastMousePos = e.GetPosition(chartView);
        }

        private void ChartView_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point mousePos = e.GetPosition(chartView);
            double zoomFactor = e.Delta > 0 ? 0.9 : 1.1; // Zoom in/out by 10%
            chartView.ZoomAtScreenPoint(mousePos.X, mousePos.Y, zoomFactor);
        }

        // Helper method to find a child control in the visual tree
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;
                
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }
    }
}