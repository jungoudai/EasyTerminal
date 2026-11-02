using System.Runtime.InteropServices;

namespace EasyTerminal.Sessions;

/// <summary>
/// Late binding over a raw IDispatch pointer. The app registers CsWinRT's ComWrappers globally, so the
/// runtime's own COM marshalling would hand back WinRT wrappers instead of something <c>dynamic</c> can call.
/// </summary>
internal sealed unsafe class ComDispatch : IDisposable
{
    private const ushort MethodFlag = 1, GetFlag = 2, PutFlag = 4;
    private const int PropertyPutId = -3;
    private const ushort VtI2 = 2, VtI4 = 3, VtBstr = 8, VtDispatch = 9, VtBool = 11, VtI1 = 16, VtUi1 = 17, VtUi2 = 18, VtUi4 = 19, VtInt = 22, VtUint = 23;
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public nint Value, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public Variant* Args;
        public int* NamedArgs;
        public uint ArgCount, NamedCount;
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(Variant* variant);

    private nint _pointer;

    private ComDispatch(nint pointer) => _pointer = pointer;

    public nint Pointer => _pointer;

    public static ComDispatch FromUnknown(nint unknown)
    {
        Guid id = DispatchId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in id, out nint dispatch));
        return new ComDispatch(dispatch);
    }

    private int GetId(string name)
    {
        var vtable = *(nint**)_pointer;
        var call = (delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint, uint, int*, int>)vtable[5];
        fixed (char* text = name)
        {
            char* names = text;
            int id;
            Guid none = Guid.Empty;
            Marshal.ThrowExceptionForHR(call(_pointer, &none, &names, 1, 0x0409, &id));
            return id;
        }
    }

    private Variant Invoke(string name, ushort flags, object[] arguments)
    {
        int id = GetId(name);
        var vtable = *(nint**)_pointer;
        var call = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DispParams*, Variant*, void*, uint*, int>)vtable[6];

        var args = stackalloc Variant[Math.Max(arguments.Length, 1)];
        // IDispatch takes arguments last to first.
        for (int i = 0; i < arguments.Length; i++) args[arguments.Length - 1 - i] = ToVariant(arguments[i]);
        int putId = PropertyPutId;
        var parameters = new DispParams
        {
            Args = args,
            ArgCount = (uint)arguments.Length,
            NamedArgs = flags == PutFlag ? &putId : null,
            NamedCount = flags == PutFlag ? 1u : 0u,
        };
        Variant result = default;
        Guid none = Guid.Empty;
        // Exception details are not needed, and this lets the HRESULT speak for itself.
        int hr = call(_pointer, id, &none, 0x0409, flags, &parameters, &result, null, null);
        for (int i = 0; i < arguments.Length; i++) VariantClear(&args[i]);
        Marshal.ThrowExceptionForHR(hr);
        return result;
    }

    private static Variant ToVariant(object value) => value switch
    {
        string text => new Variant { Type = VtBstr, Value = Marshal.StringToBSTR(text) },
        bool flag => new Variant { Type = VtBool, Value = flag ? 0xFFFF : 0 },
        int number => new Variant { Type = VtI4, Value = number },
        uint number => new Variant { Type = VtUi4, Value = (nint)number },
        _ => throw new ArgumentException("不支持的参数类型：" + value.GetType()),
    };

    public void Set(string name, object value)
    {
        var result = Invoke(name, PutFlag, new[] { value });
        VariantClear(&result);
    }

    /// <summary>Sets a property that only newer clients have; older ones simply go without.</summary>
    public bool TrySet(string name, object value)
    {
        try
        {
            Set(name, value);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotImplementedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Call(string name, params object[] arguments)
    {
        var result = Invoke(name, MethodFlag, arguments);
        VariantClear(&result);
    }

    public int GetInt(string name)
    {
        var result = Invoke(name, GetFlag, Array.Empty<object>());
        int value = result.Type switch
        {
            VtI1 or VtI2 or VtI4 or VtInt or VtUi1 or VtUi2 or VtUi4 or VtUint or VtBool => (int)result.Value,
            _ => 0,
        };
        VariantClear(&result);
        return value;
    }

    public ComDispatch GetObject(string name)
    {
        var result = Invoke(name, GetFlag, Array.Empty<object>());
        if (result.Type != VtDispatch || result.Value == 0)
        {
            VariantClear(&result);
            throw new InvalidOperationException(name + " 不是对象");
        }
        // Ownership of the reference moves from the variant to the wrapper.
        return new ComDispatch(result.Value);
    }

    public void Dispose()
    {
        if (_pointer == 0) return;
        Marshal.Release(_pointer);
        _pointer = 0;
    }
}
