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
        RestoreWindowPlacement();
    }

    private void RestoreWindowPlacement()
    {
        var (width, height, left, top, maximized) = _vm.WindowPlacement;

        Width = width;
        Height = height;

        // Only honour a saved position if it still lands on a screen that exists. Unplugging
        // a second monitor would otherwise reopen the window at coordinates nobody can see.
        if (!double.IsNaN(left) && !double.IsNaN(top) && IsOnAScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (maximized) WindowState = WindowState.Maximized;
    }

    private static bool IsOnAScreen(double left, double top, double width, double height)
    {
        // Compare against the full virtual desktop, which spans every monitor. A window is
        // reachable as long as a decent strip of its title bar is inside it.
        var minX = SystemParameters.VirtualScreenLeft;
        var minY = SystemParameters.VirtualScreenTop;
        var maxX = minX + SystemParameters.VirtualScreenWidth;
        var maxY = minY + SystemParameters.VirtualScreenHeight;

        var visibleLeft = Math.Max(left, minX);
        var visibleRight = Math.Min(left + width, maxX);
        var visibleTop = Math.Max(top, minY);
        var visibleBottom = Math.Min(top + height, maxY);

        return visibleRight - visibleLeft >= 200 && visibleBottom - visibleTop >= 100;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // RegisterHotKey needs a real window handle, which only exists from here on.
        _vm.AttachWindow(this);

        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Tell the view model whether anything it draws is actually on screen.
    ///
    /// A soundboard is used minimised, behind a game, driven by hotkeys. The level meters
    /// cost a COM call per device on a 60ms timer, and there is no reason to pay it while
    /// the window is not visible. Playback and hotkeys keep working either way.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        _vm.UiVisible = WindowState != WindowState.Minimized && IsVisible;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _vm.UiVisible = WindowState != WindowState.Minimized && IsVisible;
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

        // Marked handled either way. Letting an unrecognised drop bubble on gives no
        // useful behaviour and only leaves the drag looking like it half worked.
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _vm.AddFiles(paths);
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

    /// <summary>
    /// Ask the button for its clip length just before its menu appears.
    ///
    /// The trim sliders need a range, and the range is the clip's duration, which is only
    /// knowable by opening the file. Doing it here means one header read per button, on
    /// demand, rather than 206 of them at startup.
    /// </summary>
    private void OnTileContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundButtonViewModel vm) _ = vm.EnsureDurationAsync();
    }

    // ---- Seeking through the playing clip ------------------------------------------

    /// <summary>
    /// A drag on the seek bar has started, so the timer must stop writing the position.
    ///
    /// Without this the tick 17 times a second would keep yanking the thumb back to where
    /// the audio actually is, and the bar would be impossible to aim.
    /// </summary>
    private void OnSeekStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _vm.BeginSeek();

    private void OnSeekFinished(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _vm.EndSeek();

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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // RestoreBounds holds the normal size even while maximised, which is what should be
        // restored next launch. Using ActualWidth here would save the maximised dimensions
        // and the window would never return to its old size.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        _vm.SaveWindowPlacement(
            bounds.Width, bounds.Height, bounds.Left, bounds.Top,
            WindowState == WindowState.Maximized);

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Dispose saves the config, so window placement recorded in OnClosing lands with it.
        _vm.Dispose();
        base.OnClosed(e);
    }
}
