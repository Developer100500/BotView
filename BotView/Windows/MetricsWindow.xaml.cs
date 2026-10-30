using System.Windows;

namespace BotView.Windows
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
