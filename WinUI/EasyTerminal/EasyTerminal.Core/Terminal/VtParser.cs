using System.Text;

namespace EasyTerminal.Core.Terminal;

public interface IVtHandler
{
    void Print(ReadOnlySpan<int> codepoints);
    void Execute(byte control);
    void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<bool> isSubParameter, byte privateMarker, byte intermediate, byte final);
    void EscDispatch(byte intermediate, byte final);
    void OscDispatch(string payload);
}

/// <summary>
/// DEC ANSI state machine (after Paul Williams' model) with a UTF-8 decoder and an ASCII fast path.
/// One instance holds partial-sequence state across chunk boundaries; it is not thread safe.
/// </summary>
public sealed class VtParser
{
    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIgnore, Osc, OscEscape, StringIgnore, StringIgnoreEscape }

    private const int MaxParams = 32;
    private const int MaxStringLength = 1 << 20;

    private readonly IVtHandler _handler;
    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _sub = new bool[MaxParams];
    private readonly int[] _printBuffer = new int[4096];
    private State _state = State.Ground;
    private int _paramCount;
    private bool _paramStarted;
    private byte _private;
    private byte _intermediate;
    private int _utf8Remaining;
    private int _utf8Codepoint;
    private int _utf8Min;
    private byte[] _stringBytes = new byte[256];
    private int _stringByteCount;

    public VtParser(IVtHandler handler) => _handler = handler;

    public void Write(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (_state == State.Ground)
            {
                i = ParseGround(data, i);
                continue;
            }
            Step(data[i++]);
        }
    }

    private int ParseGround(ReadOnlySpan<byte> data, int i)
    {
        int n = 0;
        var buffer = _printBuffer;
        while (i < data.Length)
        {
            byte b = data[i];
            if (_utf8Remaining > 0)
            {
                if ((b & 0xC0) == 0x80)
                {
                    _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
                    i++;
                    if (--_utf8Remaining == 0)
                    {
                        int cp = _utf8Codepoint;
                        bool valid = cp >= _utf8Min && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF);
                        buffer[n++] = valid ? cp : 0xFFFD;
                    }
                }
                else
                {
                    // Truncated sequence: emit a replacement and reprocess this byte.
                    _utf8Remaining = 0;
                    buffer[n++] = 0xFFFD;
                }
            }
            else if (b is >= 0x20 and < 0x7F)
            {
                buffer[n++] = b;
                i++;
            }
            else if (b >= 0xC2 && b <= 0xF4)
            {
                i++;
                if (b < 0xE0) { _utf8Remaining = 1; _utf8Codepoint = b & 0x1F; _utf8Min = 0x80; }
                else if (b < 0xF0) { _utf8Remaining = 2; _utf8Codepoint = b & 0x0F; _utf8Min = 0x800; }
                else { _utf8Remaining = 3; _utf8Codepoint = b & 0x07; _utf8Min = 0x10000; }
            }
            else if (b >= 0x80)
            {
                buffer[n++] = 0xFFFD;
                i++;
            }
            else
            {
                if (n > 0) { _handler.Print(buffer.AsSpan(0, n)); n = 0; }
                i++;
                Step(b);
                if (_state != State.Ground) return i;
            }

            if (n >= buffer.Length - 1) { _handler.Print(buffer.AsSpan(0, n)); n = 0; }
        }
        if (n > 0) _handler.Print(buffer.AsSpan(0, n));
        return i;
    }

    private void Step(byte b)
    {
        // String states consume C0 differently; handle them before the global controls.
        switch (_state)
        {
            case State.Osc:
                if (b == 0x07 || b == 0x9C) { FinishOsc(); return; }
                if (b == 0x1B) { _state = State.OscEscape; return; }
                if (_stringByteCount < MaxStringLength)
                {
                    if (_stringByteCount == _stringBytes.Length) Array.Resize(ref _stringBytes, _stringBytes.Length * 2);
                    _stringBytes[_stringByteCount++] = b;
                }
                return;
            case State.OscEscape:
                FinishOsc();
                if (b == (byte)'\\') return;
                _state = State.Escape;
                ClearSequence();
                Step(b);
                return;
            case State.StringIgnore:
                if (b == 0x1B) _state = State.StringIgnoreEscape;
                else if (b is 0x9C or 0x07) _state = State.Ground;
                return;
            case State.StringIgnoreEscape:
                _state = b == (byte)'\\' ? State.Ground : State.StringIgnore;
                return;
        }

        if (b is 0x18 or 0x1A) { _state = State.Ground; return; }
        if (b == 0x1B) { _state = State.Escape; ClearSequence(); return; }
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b == 0x7F) return;

        switch (_state)
        {
            case State.Ground:
                return;
            case State.Escape:
                if (b is >= 0x20 and <= 0x2F) { _intermediate = b; _state = State.EscapeIntermediate; return; }
                switch (b)
                {
                    case (byte)'[': _state = State.CsiEntry; ClearSequence(); return;
                    case (byte)']': _state = State.Osc; _stringByteCount = 0; return;
                    case (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_': _state = State.StringIgnore; return;
                }
                _state = State.Ground;
                _handler.EscDispatch(0, b);
                return;
            case State.EscapeIntermediate:
                if (b is >= 0x20 and <= 0x2F) { _intermediate = b; return; }
                _state = State.Ground;
                _handler.EscDispatch(_intermediate, b);
                return;
            case State.CsiEntry:
                if (b is >= 0x3C and <= 0x3F) { _private = b; _state = State.CsiParam; return; }
                _state = State.CsiParam;
                goto case State.CsiParam;
            case State.CsiParam:
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    if (!_paramStarted) StartParam(false);
                    int idx = _paramCount - 1;
                    if (idx < MaxParams) _params[idx] = Math.Min(_params[idx] * 10 + (b - '0'), 65535);
                    return;
                }
                if (b == (byte)';')
                {
                    if (!_paramStarted) StartParam(false);
                    _paramStarted = false;
                    return;
                }
                if (b == (byte)':')
                {
                    if (!_paramStarted) StartParam(false);
                    StartParam(true);
                    return;
                }
                if (b is >= 0x20 and <= 0x2F) { _intermediate = b; return; }
                if (b is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                    int count = Math.Min(_paramCount, MaxParams);
                    _handler.CsiDispatch(_params.AsSpan(0, count), _sub.AsSpan(0, count), _private, _intermediate, b);
                    return;
                }
                _state = State.CsiIgnore;
                return;
            case State.CsiIgnore:
                if (b is >= 0x40 and <= 0x7E) _state = State.Ground;
                return;
        }
    }

    private void StartParam(bool sub)
    {
        if (_paramCount < MaxParams)
        {
            _params[_paramCount] = 0;
            _sub[_paramCount] = sub;
        }
        _paramCount++;
        _paramStarted = true;
    }

    private void ClearSequence()
    {
        _paramCount = 0;
        _paramStarted = false;
        _private = 0;
        _intermediate = 0;
    }

    private void FinishOsc()
    {
        _state = State.Ground;
        _handler.OscDispatch(Encoding.UTF8.GetString(_stringBytes, 0, _stringByteCount));
    }
}
