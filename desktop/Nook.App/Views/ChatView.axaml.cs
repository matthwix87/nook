using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Nook.App.ViewModels;

namespace Nook.App.Views;

public partial class ChatView : UserControl
{
    ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        // Enter sends, Shift+Enter adds a line (like the website). Tunnel so the TextBox doesn't eat it first.
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
        _vm = DataContext as ChatViewModel;
        if (_vm != null) _vm.ScrollRequested += ScrollToEnd;
    }

    void ScrollToEnd() => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
}
