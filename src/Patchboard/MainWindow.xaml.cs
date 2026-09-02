using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Patchboard.ViewModels;

namespace Patchboard;

public partial class MainWindow : Window
{
    /// <summary>
    /// DWMWA_USE_IMMERSIVE_DARK_MODE. Without it the title bar stays light while the rest
    /// of the window is near black, which looks broken rather than deliberate. WPF has no
    /// managed way to set this.
    /// </summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Private clipboard format for dragging a button to a new slot. Distinct from
    /// FileDrop so a reorder and a drop of new files can never be confused for each other.
    /// </summary>
    private const string SoundDragFormat = "Patchboard.SoundButton";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly MainViewModel _vm;

    private Point _dragOrigin;
    private SoundButtonViewModel? _dragCandidate;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // RegisterHotKey needs a real window handle, which only exists from here on.
        _vm.AttachWindow(this);

        ApplyDarkTitleBar();
    }

    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        try
        {
            // Fails harmlessly on Windows builds older than 1809. A light title bar is a
            // cosmetic loss, so it is never worth throwing over.
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    // ---- Dropping files onto the window -----------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // A reorder that misses every tile lands here. Do nothing rather than treating it
        // as a request to add files.
        if (e.Data.GetDataPresent(SoundDragFormat)) { e.Handled = true; return; }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _vm.AddFiles(paths);
        e.Handled = true;
    }

    // ---- Dragging a button to a new slot -----------------------------------------

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as SoundButtonViewModel;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        // Only promote a press into a drag once the pointer has travelled past the system
        // threshold. Without this, the small movement in an ordinary click would start a
        // drag and the sound would never play.
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _dragCandidate;
        _dragCandidate = null;

        if (sender is DependencyObject source)
            DragDrop.DoDragDrop(source, new DataObject(SoundDragFormat, dragged), DragDropEffects.Move);
    }

    private void OnTileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SoundDragFormat)
            ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnTileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SoundDragFormat))
        {
            if (e.Data.GetData(SoundDragFormat) is SoundButtonViewModel dragged
                && (sender as FrameworkElement)?.DataContext is SoundButtonViewModel target)
            {
                _vm.MoveSound(dragged, target);
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _vm.AddFiles(paths);
            e.Handled = true;
        }
    }

    // ---- Rename -------------------------------------------------------------------

    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // The box is only just being shown, so focus has to wait for layout to finish.
        Dispatcher.BeginInvoke(
            () =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm.ConfirmRenameCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _vm.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ---- Hotkey binding capture ----------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsBindingHotkey) return;

        // With Alt held, WPF reports Key.System and puts the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_vm.TryCompleteBind(key, Keyboard.Modifiers)) e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
