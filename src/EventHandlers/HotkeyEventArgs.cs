using System;

namespace GlobalHotkeys
{
    public class HotkeyEventArgs : EventArgs
    {
        public KeyModifier ModifierKeys { get; }
        public VirtualKey Key { get; }
        public ushort Id { get; }
        public uint Time { get; }

        public HotkeyEventArgs(KeyModifier modifierKeys, VirtualKey key, ushort id, uint time)
        {
            ModifierKeys = modifierKeys;
            Key = key;
            Id = id;
            Time = time;
        }
    }
}
