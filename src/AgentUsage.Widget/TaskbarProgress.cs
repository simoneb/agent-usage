using System.Runtime.InteropServices;

namespace AgentUsage.Widget;

/// <summary>
/// Drives the progress bar on this app's taskbar button via ITaskbarList3.
/// Called through raw vtable slots: NativeAOT has no built-in COM interface marshalling.
/// </summary>
internal sealed unsafe class TaskbarProgress : IDisposable
{
    // Slots after IUnknown(0-2), ITaskbarList(3-7), ITaskbarList2(8).
    private const int SlotHrInit = 3;
    private const int SlotSetProgressValue = 9;
    private const int SlotSetProgressState = 10;

    public enum State
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    private IntPtr _instance;

    private TaskbarProgress(IntPtr instance) => _instance = instance;

    public static TaskbarProgress? Create()
    {
        var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");   // CLSID_TaskbarList
        var iid = new Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");     // IID_ITaskbarList3

        if (Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_INPROC_SERVER,
                ref iid, out var instance) != 0 || instance == IntPtr.Zero)
            return null;

        var taskbar = new TaskbarProgress(instance);
        return taskbar.HrInit() == 0 ? taskbar : null;
    }

    private int HrInit()
    {
        var fn = (delegate* unmanaged<IntPtr, int>)Slot(SlotHrInit);
        return fn(_instance);
    }

    public void SetValue(IntPtr hwnd, ulong completed, ulong total)
    {
        if (_instance == IntPtr.Zero) return;

        var fn = (delegate* unmanaged<IntPtr, IntPtr, ulong, ulong, int>)Slot(SlotSetProgressValue);
        fn(_instance, hwnd, completed, total);
    }

    public void SetState(IntPtr hwnd, State state)
    {
        if (_instance == IntPtr.Zero) return;

        var fn = (delegate* unmanaged<IntPtr, IntPtr, int, int>)Slot(SlotSetProgressState);
        fn(_instance, hwnd, (int)state);
    }

    private IntPtr Slot(int index)
    {
        var vtable = *(IntPtr**)_instance;
        return vtable[index];
    }

    public void Dispose()
    {
        if (_instance == IntPtr.Zero) return;

        Marshal.Release(_instance);
        _instance = IntPtr.Zero;
    }
}
