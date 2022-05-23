using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
namespace GlobalHotkeys
{
    /// <summary>
    /// This class is thread-safe.
    /// It has a <typeparamref name="Dispose"/> method that you need to call
    /// (your hotkeys will be removed, and you can't create new ones).
    /// </summary>
    public static class HotkeyManager
    {
        private const uint WM_QUIT = 0x0012;
        private const uint WM_HOTKEY = 0x0312;
        private const uint WM_RegHotkey = 0x7001; // this is a custom message, not defined in win32
        private const uint WM_UnregHotkey = 0x7000; // this is a custom message, not defined in win32
        private const uint MOD_NOREPEAT = 0x4000;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        private static bool _disposed = false;
        private static readonly object _msgListenerLock = new();
        private static readonly object _disposeLock = new();
        private static readonly object _hotkeyLock = new();
        private static readonly object _hotkeyActionLock = new();

        private static Dictionary<ushort, Hotkey> _hotkeyIdPairs = new();

        private static Thread _msgListener = null;
        private static uint _msgListenerId = 0;
        private static uint MsgListenerId
        {
            get
            {
                if (_msgListener is null)
                {
                    lock (_msgListenerLock)
                    {
                        using ManualResetEvent mre = new(false);

                        if (_msgListener is null)
                        {
                            _msgListener = new Thread(() =>
                            {
                                _msgListenerId = NativeMethods.GetCurrentThreadId();
                                mre.Set();

                                MSG msg = default;
                                while (NativeMethods.GetMessage(ref msg, IntPtr.Zero, 0, 0) != 0)
                                {
                                    if (!_hotkeyIdPairs.TryGetValue((ushort)msg.wParam, out var hotkey)) continue;

                                    switch (msg.message)
                                    {
                                        case WM_HOTKEY:
                                            hotkey.HotkeyPressed.Invoke(new HotkeyEventArgs(hotkey.ModifierKeys, hotkey.Key, hotkey.Id, msg.time));
                                            break;

                                        case WM_RegHotkey:
                                            RegisterOrOverrideHotkey(hotkey);
                                            break;

                                        case WM_UnregHotkey:
                                            UnregisterHotkey(hotkey);
                                            break;
                                    }
                                }

                                static void RegisterOrOverrideHotkey(Hotkey hotkey)
                                {
                                    uint fsModifiers = hotkey.NoRepeat ? (uint)hotkey.ModifierKeys | MOD_NOREPEAT : (uint)hotkey.ModifierKeys;
                                    if (!NativeMethods.RegisterHotKey(IntPtr.Zero, hotkey.Id, fsModifiers, (uint)hotkey.Key))
                                    {
                                        int RegHotkeyErr = Marshal.GetLastWin32Error();
                                        if (RegHotkeyErr == ERROR_HOTKEY_ALREADY_REGISTERED)
                                        {
                                            UnregisterHotkey(hotkey);
                                            if (!NativeMethods.RegisterHotKey(IntPtr.Zero, hotkey.Id, fsModifiers, (uint)hotkey.Key))
                                            {
                                                throw new Win32Exception(Marshal.GetLastWin32Error());
                                            }
                                            return;
                                        }
                                        throw new Win32Exception(RegHotkeyErr);
                                    }
                                }

                                static void UnregisterHotkey(Hotkey hotkey)
                                {
                                    if (!NativeMethods.UnregisterHotKey(IntPtr.Zero, hotkey.Id))
                                    {
                                        throw new Win32Exception(Marshal.GetLastWin32Error());
                                    }
                                    hotkey.Dispose();
                                    _hotkeyIdPairs.Remove(hotkey.Id);
                                }
                            });
                            _msgListener.IsBackground = true;
                            _msgListener.SetApartmentState(ApartmentState.STA);
                            _msgListener.Start();
                        }

                        // Wait until the thread's id is set.
                        mre.WaitOne();
                    }
                }

                return _msgListenerId;
            }
        }

        /// <param name="noRepeat">If <typeparamref name="true"/>, then the keyboard auto-repeat does not yield multiple hotkey notifications.</param>
        /// <returns>The <typeparamref name="id"/> of the hotkey.</returns>
        public static int AddOrReplaceHotkey(KeyModifier modifierKeys, VirtualKey key, Action<HotkeyEventArgs> OnHotkeyPressed, bool noRepeat = true)
        {
            lock (_hotkeyLock)
            {
                var hotkey = new Hotkey(modifierKeys, key, OnHotkeyPressed, noRepeat);
                _hotkeyIdPairs.Add(hotkey.Id, hotkey);
                if (!NativeMethods.PostThreadMessage(MsgListenerId, WM_RegHotkey, new UIntPtr(hotkey.Id), IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return hotkey.Id;
            }
        }

        public static void RemoveHotkey(int hotkeyId)
        {
            if (!NativeMethods.PostThreadMessage(MsgListenerId, WM_UnregHotkey, new UIntPtr((ushort)hotkeyId), IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public static void SetHotkeyAction(int hotkeyId, Action<HotkeyEventArgs> OnHotkeyPressed)
        {
            lock (_hotkeyActionLock)
            {
                _hotkeyIdPairs[(ushort)hotkeyId].HotkeyPressed = OnHotkeyPressed;
            }
        }

        public static void Dispose()
        {
            if (_disposed) return;

            lock (_disposeLock)
            {
                if (_disposed) return;

                foreach (Hotkey hotkey in _hotkeyIdPairs.Values)
                {
                    RemoveHotkey(hotkey.Id);
                }
                if (_msgListener is not null)
                {
                    if (!NativeMethods.PostThreadMessage(_msgListenerId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    _msgListener.Join();
                    _msgListener = null;
                }
                _hotkeyIdPairs = null;

                _disposed = true;
            }
        }
    }
}
