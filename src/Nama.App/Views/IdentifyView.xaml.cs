using System.Windows.Controls;
using System.Windows.Input;
using Nama.App.ViewModels;

namespace Nama.App.Views;

public partial class IdentifyView : UserControl
{
    public IdentifyView() => InitializeComponent();

    /// <summary>Double-clicking a match confirms it, which is the fastest path through this step.</summary>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not IdentifyViewModel viewModel) return;
        if (viewModel.ConfirmCommand.CanExecute(null)) viewModel.ConfirmCommand.Execute(null);
    }
}
