using System.Windows;

namespace BotView.Views
{
    public partial class MetricsWindow : Window
    {
        public MetricsWindow(string metricsText)
        {
            InitializeComponent();
            MetricsTextBlock.Text = metricsText;
        }
    }
}
