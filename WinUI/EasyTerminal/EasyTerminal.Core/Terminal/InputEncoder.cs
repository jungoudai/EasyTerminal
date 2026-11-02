using System.Text;

namespace EasyTerminal.Core.Terminal;

public enum TerminalKey
{
    Up, Down, Right, Left, Home, End, PageUp, PageDown, Insert, Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Enter, Tab, Backspace, Escape,
}

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

public enum MouseButton { Left = 0, Middle = 1, Right = 2, None = 3, WheelUp = 64, WheelDown = 65 }
public enum MouseAction { Press, Release, Move }

public static class InputEncoder
{
    public static string EncodeKey(TerminalKey key, KeyModifiers mods, bool appCursor)
    {
        int modParam = (int)mods + 1;

        switch (key)
        {
            case TerminalKey.Enter: return (mods & KeyModifiers.Alt) != 0 ? "\x1b\r" : "\r";
            case TerminalKey.Tab: return (mods & KeyModifiers.Shift) != 0 ? "\x1b[Z" : "\t";
            case TerminalKey.Backspace:
                if ((mods & KeyModifiers.Control) != 0) return (mods & KeyModifiers.Alt) != 0 ? "\x1b\x08" : "\x08";
                return (mods & KeyModifiers.Alt) != 0 ? "\x1b\x7f" : "\x7f";
            case TerminalKey.Escape: return "\x1b";
        }

        char? arrow = key switch
        {
            TerminalKey.Up => 'A',
            TerminalKey.Down => 'B',
            TerminalKey.Right => 'C',
            TerminalKey.Left => 'D',
            TerminalKey.Home => 'H',
            TerminalKey.End => 'F',
            _ => null,
        };
        if (arrow is char letter)
        {
            if (mods != KeyModifiers.None) return $"\x1b[1;{modParam}{letter}";
            return appCursor ? $"\x1bO{letter}" : $"\x1b[{letter}";
        }

        char? ss3 = key switch { TerminalKey.F1 => 'P', TerminalKey.F2 => 'Q', TerminalKey.F3 => 'R', TerminalKey.F4 => 'S', _ => null };
        if (ss3 is char f)
            return mods == KeyModifiers.None ? $"\x1bO{f}" : $"\x1b[1;{modParam}{f}";

        int code = key switch
        {
            TerminalKey.Insert => 2,
            TerminalKey.Delete => 3,
            TerminalKey.PageUp => 5,
            TerminalKey.PageDown => 6,
            TerminalKey.F5 => 15,
            TerminalKey.F6 => 17,
            TerminalKey.F7 => 18,
            TerminalKey.F8 => 19,
            TerminalKey.F9 => 20,
            TerminalKey.F10 => 21,
            TerminalKey.F11 => 23,
            TerminalKey.F12 => 24,
            _ => 0,
        };
        if (code == 0) return "";
        return mods == KeyModifiers.None ? $"\x1b[{code}~" : $"\x1b[{code};{modParam}~";
    }

    /// <summary>Control byte for Ctrl+char, or null when the combination has no C0 mapping.</summary>
    public static byte? ControlByte(char c)
    {
        c = char.ToUpperInvariant(c);
        if (c is >= 'A' and <= 'Z') return (byte)(c - 'A' + 1);
        return c switch
        {
            ' ' or '2' or '@' => 0,
            '[' or '3' => 0x1B,
            '\\' or '4' => 0x1C,
            ']' or '5' => 0x1D,
            '^' or '6' => 0x1E,
            '_' or '-' or '7' => 0x1F,
            '8' or '?' => 0x7F,
            _ => null,
        };
    }

    public static byte[] EncodeMouse(MouseEncodingMode encoding, MouseButton button, MouseAction action, int col, int row, KeyModifiers mods)
    {
        int code = (int)button;
        if ((mods & KeyModifiers.Shift) != 0) code |= 4;
        if ((mods & KeyModifiers.Alt) != 0) code |= 8;
        if ((mods & KeyModifiers.Control) != 0) code |= 16;
        if (action == MouseAction.Move) code |= 32;

        if (encoding == MouseEncodingMode.Sgr)
        {
            char suffix = action == MouseAction.Release ? 'm' : 'M';
            return Encoding.ASCII.GetBytes($"\x1b[<{code};{col + 1};{row + 1}{suffix}");
        }

        // Legacy encodings cannot name which button was released.
        if (action == MouseAction.Release) code = (code & ~3) | 3;
        var sb = new StringBuilder("\x1b[M");
        sb.Append((char)(code + 32));
        if (encoding == MouseEncodingMode.Utf8)
        {
            sb.Append((char)Math.Min(col + 33, 2047));
            sb.Append((char)Math.Min(row + 33, 2047));
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("\x1b[M")) { (byte)(code + 32), (byte)Math.Min(col + 33, 255), (byte)Math.Min(row + 33, 255) };
        return bytes.ToArray();
    }

    public static string PrepareForPaste(string text, bool bracketed)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!bracketed) return text;
        // A pasted end marker would let clipboard content break out of bracketed mode.
        return "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
    }
}
