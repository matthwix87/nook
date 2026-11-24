using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nook.App.ViewModels;

namespace Nook.App.Views;

public partial class CodingView : UserControl
{
    CodingViewModel? _vm;

    public CodingView()
    {
        InitializeComponent();
        Composer.AddHandler(KeyDownEvent, OnComposerKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    void OnComposerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm != null)
        {
            e.Handled = true;
            if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.ScrollRequested -= ScrollToEnd;
        _vm = DataContext as CodingViewModel;
        if (_vm == null) return;
        _vm.ScrollRequested += ScrollToEnd;
        _vm.PickFolder = PickFolderAsync;
    }

    async Task<string?> PickFolderAsync()
    {
        // Uses the XDG portal / GTK dialog on Linux and the shell dialog on Windows.
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || !sp.CanPickFolder) return null;
        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the workspace folder", AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    void ScrollToEnd() => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
}
