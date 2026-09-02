using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Patchboard.Models;

namespace Patchboard.Services
{
    /// <summary>
    /// Global hotkeys that keep firing while a fullscreen game holds focus.
    ///
    /// Built on RegisterHotKey rather than a WH_KEYBOARD_LL hook, deliberately. A low
    /// level hook sits in the input path of every keystroke on the machine, which adds
    /// latency to the game itself and is the exact signature anti-cheat looks for.
    /// RegisterHotKey asks Windows to route one combination to our window and leaves
    /// every other key untouched, so a soundboard binding cannot be mistaken for an
    /// input cheat.
    ///
    /// Thread affinity: Windows binds a hotkey to the thread that registered it, and
    /// only that thread can unregister it. Everything here therefore runs on the
    /// window's dispatcher thread, marshalling if a caller arrives from elsewhere.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int WmHotkey = 0x0312;

        /// <summary>
        /// MOD_NOREPEAT. OR-ed into every registration because without it the keyboard
        /// auto-repeat delivers WM_HOTKEY over and over while the key is held down, and
        /// on a soundboard that machine-guns dozens of overlapping copies of one clip
        /// from a single leaned-on key.
        /// </summary>
        private const uint ModNoRepeat = 0x4000;

        // RegisterHotKey reserves ids 0x0000 to 0xBFFF for applications. Starting at
        // 9000 stays inside that range while avoiding the low numbers that WPF command
        // plumbing and sample code tend to pick.
        private const int FirstNativeId = 9000;
        private const int LastNativeId = 0xBFFF;

        private const int ErrorHotkeyAlreadyRegistered = 1409;
        private const int ErrorInvalidWindowHandle = 1400;

        /// <summary>Caller's string id to the int Win32 wants. Assignments are permanent.</summary>
        private readonly Dictionary<string, int> _nativeIds = new(StringComparer.Ordinal);

        /// <summary>The reverse direction, used to name the id that arrives in WM_HOTKEY.</summary>
        private readonly Dictionary<int, string> _names = [];

        /// <summary>Native ids Windows currently holds for us, so teardown is exact.</summary>
        private readonly HashSet<int> _live = [];

        private HwndSource? _source;
        private HwndSourceHook? _hook;
        private Dispatcher? _dispatcher;
        private IntPtr _handle;
        private int _nextNativeId = FirstNativeId;
        private bool _disposed;

        /// <summary>Raised on the UI thread with the id that was registered.</summary>
        public event Action<string>? HotkeyPressed;

        /// <summary>
        /// Hook the window's message loop. Call once, from the UI thread.
        ///
        /// Safe from the constructor as well as from SourceInitialized: EnsureHandle
        /// creates the HWND if the window has not been shown yet, and RegisterHotKey
        /// needs a real handle before any binding can be made.
        /// </summary>
        public void Attach(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Attaching twice would install a second hook and fire every press twice.
            if (_source is not null) return;

            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException(
                    "The window has no HwndSource. Attach must run on the thread that owns the window.");

            _handle = handle;
            _source = source;
            _dispatcher = window.Dispatcher;
            _hook = OnWindowMessage;
            source.AddHook(_hook);
        }

        /// <summary>
        /// Bind a combination, replacing whatever <paramref name="id"/> had before.
        ///
        /// Returns false with a readable <paramref name="error"/> when Windows refuses
        /// the combination, which usually means another running application already owns
        /// it. That is an ordinary outcome on a machine running Discord and a game, not
        /// an exceptional one, so it never throws.
        /// </summary>
        public bool Register(string id, Hotkey hotkey, out string? error)
        {
            error = null;
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(hotkey);

            if (!hotkey.IsSet)
            {
                // Clearing a binding is a normal edit. Drop the old one and report
                // success, so the caller does not show the user an error for it.
                Unregister(id);
                return true;
            }

            if (_source is null)
            {
                error = "Hotkeys are not attached to a window yet. Call Attach first.";
                return false;
            }

            var dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    error = "The window is closing.";
                    return false;
                }

                // An out parameter cannot cross a lambda, so the result comes back as a
                // tuple instead.
                var (marshalledOk, marshalledError) = dispatcher.Invoke(() =>
                {
                    var inner = Register(id, hotkey, out var innerError);
                    return (inner, innerError);
                });

                error = marshalledError;
                return marshalledOk;
            }

            var nativeId = AssignNativeId(id);
            if (nativeId < 0)
            {
                error = "No hotkey slots left.";
                return false;
            }

            if (_live.Remove(nativeId))
                NativeHotkeys.UnregisterHotKey(_handle, nativeId);

            // HotkeyModifiers is declared with the MOD_ALT, MOD_CONTROL, MOD_SHIFT and
            // MOD_WIN values, so it goes straight through with no translation table.
            var modifiers = (uint)hotkey.Modifiers | ModNoRepeat;

            if (NativeHotkeys.RegisterHotKey(_handle, nativeId, modifiers, hotkey.VirtualKey))
            {
                _live.Add(nativeId);
                return true;
            }

            error = Describe(Marshal.GetLastPInvokeError(), hotkey);
            return false;
        }

        /// <summary>Release one binding. Silent when the id was never bound.</summary>
        public void Unregister(string id)
        {
            if (_disposed || string.IsNullOrWhiteSpace(id)) return;
            if (MarshalToDispatcher(() => Unregister(id))) return;

            if (!_nativeIds.TryGetValue(id, out var nativeId)) return;
            if (!_live.Remove(nativeId)) return;

            NativeHotkeys.UnregisterHotKey(_handle, nativeId);
        }

        /// <summary>
        /// Release every binding. The id assignments survive, so a rebind pass gives the
        /// same button the same native id and nothing accumulates across a rebuild.
        /// </summary>
        public void UnregisterAll()
        {
            if (MarshalToDispatcher(UnregisterAll)) return;

            foreach (var nativeId in _live)
            {
                // Per device of failure: one id Windows has already reclaimed must not
                // stop the rest from being released.
                NativeHotkeys.UnregisterHotKey(_handle, nativeId);
            }

            _live.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;

            UnregisterAll();
            _disposed = true;

            if (_source is not null && _hook is not null)
            {
                try
                {
                    _source.RemoveHook(_hook);
                }
                catch (Exception)
                {
                    // The window may already be destroyed, which removes the hook anyway.
                }
            }

            _source = null;
            _hook = null;
            _dispatcher = null;
            _handle = IntPtr.Zero;
            HotkeyPressed = null;
        }

        /// <summary>
        /// The window procedure hook. Runs on the dispatcher thread that owns the
        /// window, which is why <see cref="HotkeyPressed"/> needs no marshalling and
        /// subscribers may touch the view model directly.
        /// </summary>
        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey) return IntPtr.Zero;

            // ToInt32 throws when the pointer does not fit; the hotkey id always does,
            // so read the full width and narrow without a check.
            var nativeId = unchecked((int)wParam.ToInt64());

            if (!_live.Contains(nativeId) || !_names.TryGetValue(nativeId, out var id))
                return IntPtr.Zero;

            handled = true;

            try
            {
                HotkeyPressed?.Invoke(id);
            }
            catch (Exception)
            {
                // An exception escaping a window procedure takes the message pump with
                // it, and with the pump goes the whole window. One misbehaving handler
                // must only cost that one key press.
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Returns the id's permanent native number, allocating on first use.
        /// Returns -1 once the application range is exhausted.
        /// </summary>
        private int AssignNativeId(string id)
        {
            if (_nativeIds.TryGetValue(id, out var existing)) return existing;
            if (_nextNativeId > LastNativeId) return -1;

            var nativeId = _nextNativeId++;
            _nativeIds[id] = nativeId;
            _names[nativeId] = id;
            return nativeId;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the window's thread when the caller is on
        /// another one. Returns true when the caller should stop, either because the
        /// work has been handed over or because the dispatcher is shutting down and
        /// process teardown will reclaim the hotkeys regardless.
        /// </summary>
        private bool MarshalToDispatcher(Action action)
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) return false;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return true;

            try
            {
                dispatcher.Invoke(action);
            }
            catch (Exception)
            {
                // The dispatcher can begin shutting down between the check and the call.
            }

            return true;
        }

        private static string Describe(int lastError, Hotkey hotkey) => lastError switch
        {
            ErrorHotkeyAlreadyRegistered =>
                $"{hotkey} is already taken by another application. Pick a different combination.",
            ErrorInvalidWindowHandle =>
                "The window that owns the hotkeys has gone away.",
            _ =>
                $"Windows refused {hotkey} (error {lastError}).",
        };
    }

    internal static class NativeHotkeys
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

// KeyNames sits in Patchboard.Models, not Patchboard.Services, because Hotkey.ToString
// calls it and a model should not have to reach into a service namespace to render
// itself. Two block namespaces rather than a file-scoped one plus a block: the compiler
// rejects mixing the two forms in one file.
namespace Patchboard.Models
{
    /// <summary>
    /// Turns Win32 virtual-key codes into short labels for the button corners, and turns
    /// a WPF key press into a storable <see cref="Hotkey"/> for the bind prompt.
    /// </summary>
    public static class KeyNames
    {
        /// <summary>MAPVK_VK_TO_CHAR.</summary>
        private const uint MapVirtualKeyToChar = 2;

        /// <summary>
        /// Keys whose label is a word rather than the character they type. Anything not
        /// in here is either a letter, a digit, a numpad key, a function key, or
        /// punctuation whose meaning depends on the keyboard layout.
        /// </summary>
        private static readonly Dictionary<uint, string> Named = new()
        {
            [0x03] = "Break",
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0C] = "Clear",
            [0x0D] = "Enter",
            [0x10] = "Shift",
            [0x11] = "Ctrl",
            [0x12] = "Alt",
            [0x13] = "Pause",
            [0x14] = "CapsLock",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x29] = "Select",
            [0x2A] = "Print",
            [0x2B] = "Execute",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x2F] = "Help",
            [0x5B] = "LWin",
            [0x5C] = "RWin",
            [0x5D] = "Menu",
            [0x5F] = "Sleep",
            [0x6A] = "Num*",
            [0x6B] = "Num+",
            [0x6C] = "NumSep",
            [0x6D] = "Num-",
            [0x6E] = "Num.",
            [0x6F] = "Num/",
            [0x90] = "NumLock",
            [0x91] = "ScrollLock",
            [0xA0] = "LShift",
            [0xA1] = "RShift",
            [0xA2] = "LCtrl",
            [0xA3] = "RCtrl",
            [0xA4] = "LAlt",
            [0xA5] = "RAlt",
            [0xA6] = "BrowserBack",
            [0xA7] = "BrowserForward",
            [0xA8] = "BrowserRefresh",
            [0xA9] = "BrowserStop",
            [0xAA] = "BrowserSearch",
            [0xAB] = "BrowserFavourites",
            [0xAC] = "BrowserHome",
            [0xAD] = "Mute",
            [0xAE] = "VolDown",
            [0xAF] = "VolUp",
            [0xB0] = "NextTrack",
            [0xB1] = "PrevTrack",
            [0xB2] = "StopMedia",
            [0xB3] = "PlayPause",
            [0xB4] = "Mail",
            [0xB5] = "MediaSelect",
        };

        /// <summary>Short human label for a virtual-key code, e.g. 0x74 becomes "F5".</summary>
        public static string Describe(uint virtualKey)
        {
            if (virtualKey == 0) return "";

            if (Named.TryGetValue(virtualKey, out var name)) return name;

            // 0 to 9 and A to Z use their own ASCII values as virtual-key codes, so they
            // need no table entry.
            if (virtualKey is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A))
                return ((char)virtualKey).ToString();

            if (virtualKey is >= 0x60 and <= 0x69)
                return "Num" + (char)('0' + (virtualKey - 0x60));

            if (virtualKey is >= 0x70 and <= 0x87)
                return "F" + (virtualKey - 0x6F);

            // Punctuation and the OEM keys move around between keyboard layouts, so ask
            // Windows what this key actually types instead of hardcoding a US keyboard.
            uint mapped;
            try
            {
                mapped = NativeKeyboard.MapVirtualKeyW(virtualKey, MapVirtualKeyToChar);
            }
            catch (Exception)
            {
                mapped = 0;
            }

            // The character is in the low word. The top bit marks a dead key, an accent
            // waiting for the next keystroke, and the character underneath it is still
            // the right thing to show.
            var character = (char)(mapped & 0xFFFF);
            if (character != '\0' && !char.IsControl(character) && !char.IsWhiteSpace(character))
                return char.ToUpperInvariant(character).ToString();

            return $"Key0x{virtualKey:X2}";
        }

        /// <summary>
        /// Convert a WPF key press into a storable hotkey for the bind prompt.
        /// Returns null when the press cannot stand on its own, so holding Shift while
        /// reaching for a key does not bind Shift.
        /// </summary>
        public static Hotkey? FromWpfKey(Key key, ModifierKeys modifiers)
        {
            // WPF reports Key.System while Alt is held and puts the real key in
            // KeyEventArgs.SystemKey. The caller is expected to have resolved that, so
            // anything still arriving as System would store a binding that can never
            // fire. Refuse it rather than save it.
            if (key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
                return null;

            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey <= 0) return null;

            if (IsModifier((uint)virtualKey)) return null;

            var mapped = HotkeyModifiers.None;
            if (modifiers.HasFlag(ModifierKeys.Control)) mapped |= HotkeyModifiers.Control;
            if (modifiers.HasFlag(ModifierKeys.Shift)) mapped |= HotkeyModifiers.Shift;
            if (modifiers.HasFlag(ModifierKeys.Alt)) mapped |= HotkeyModifiers.Alt;
            if (modifiers.HasFlag(ModifierKeys.Windows)) mapped |= HotkeyModifiers.Win;

            return new Hotkey { Modifiers = mapped, VirtualKey = (uint)virtualKey };
        }

        /// <summary>
        /// Filtered on the virtual-key code rather than the WPF <see cref="Key"/> so it
        /// covers LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt, LWin and
        /// RWin in one test, along with the side-agnostic codes Windows also uses.
        /// </summary>
        private static bool IsModifier(uint virtualKey) => virtualKey
            is 0x10 or 0x11 or 0x12   // VK_SHIFT, VK_CONTROL, VK_MENU
            or 0x5B or 0x5C           // VK_LWIN, VK_RWIN
            or (>= 0xA0 and <= 0xA5); // the left and right specific shift, ctrl and alt
    }

    internal static class NativeKeyboard
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    }
}
