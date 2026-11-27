using System.Windows;
using System.Windows.Input;
using MarketWatch.App.ViewModels;

namespace MarketWatch.App.Views;

public partial class SearchWindow : Window
{
    private readonly SearchDialogViewModel _vm;

    public SearchWindow(SearchDialogViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnSearchBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
