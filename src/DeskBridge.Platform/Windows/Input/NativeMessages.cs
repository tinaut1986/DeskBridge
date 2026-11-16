namespace DeskBridge.Platform.Windows.Input;

/// <summary>Mouse message ids as reported in <see cref="HookedMouse.Message"/>.</summary>
public static class NativeMessages
{
    public const int MouseMove = NativeInput.WM_MOUSEMOVE;
    public const int LeftDown = NativeInput.WM_LBUTTONDOWN, LeftUp = NativeInput.WM_LBUTTONUP;
    public const int RightDown = NativeInput.WM_RBUTTONDOWN, RightUp = NativeInput.WM_RBUTTONUP;
    public const int MiddleDown = NativeInput.WM_MBUTTONDOWN, MiddleUp = NativeInput.WM_MBUTTONUP;
    public const int XDown = NativeInput.WM_XBUTTONDOWN, XUp = NativeInput.WM_XBUTTONUP;
    public const int Wheel = NativeInput.WM_MOUSEWHEEL, HWheel = NativeInput.WM_MOUSEHWHEEL;
}
