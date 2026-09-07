using Avalonia.Controls;

namespace AvaloniaDemo.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var viewModel = this.DataContext as ViewModels.MainViewModel;
            if (viewModel != null)
            {
                viewModel.Greeting = "Button Clicked!";
            }
        }
    }
}
