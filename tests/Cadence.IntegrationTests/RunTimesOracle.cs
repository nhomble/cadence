using System.Runtime.InteropServices;

namespace Cadence.IntegrationTests;

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEMTIME
{
    public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
}

// IRegisteredTask, vtable order from taskschd.h; only GetRunTimes is called, earlier slots are placeholders.
[ComImport, Guid("9c86f320-dee3-4dd1-b972-a303f26b061e"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IRegisteredTaskRaw
{
    void _get_Name(); void _get_Path(); void _get_State(); void _get_Enabled(); void _put_Enabled();
    void _Run(); void _RunEx(); void _GetInstances(); void _get_LastRunTime(); void _get_LastTaskResult();
    void _get_NumberOfMissedRuns(); void _get_NextRunTime(); void _get_Definition(); void _get_Xml();
    void _GetSecurityDescriptor(); void _SetSecurityDescriptor(); void _Stop();
    [PreserveSig] int GetRunTimes(ref SYSTEMTIME start, ref SYSTEMTIME end, ref uint count, out IntPtr runTimes);
}

/// <summary>PLAN Appendix B. Test oracle only: right for CalendarTriggers on non-DST dates; wrong for TimeTrigger repetition ends and DST days.</summary>
public static class RunTimesOracle
{
    /// <summary>The HRESULT of the last call (0 = S_OK, 1 = S_FALSE fewer than max, 0x41305 = no time triggers).</summary>
    public static int LastHResult { get; private set; }

    // registeredTask = the late-bound COM object from folder.GetTask(name); the cast performs QueryInterface.
    public static List<DateTime> Get(object registeredTask, DateTime start, DateTime end, int max)
    {
        var t = (IRegisteredTaskRaw)registeredTask;
        var s = ToSt(start); var e = ToSt(end); uint c = (uint)max;
        int hr = t.GetRunTimes(ref s, ref e, ref c, out IntPtr p);
        LastHResult = hr;
        var list = new List<DateTime>();
        try
        {
            int size = Marshal.SizeOf<SYSTEMTIME>();
            for (int i = 0; i < c; i++)
            {
                var st = Marshal.PtrToStructure<SYSTEMTIME>(p + i * size);
                list.Add(new DateTime(st.Year, st.Month, st.Day, st.Hour, st.Minute, st.Second));
            }
        }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
        return list;
    }

    static SYSTEMTIME ToSt(DateTime d) => new() { Year = (ushort)d.Year, Month = (ushort)d.Month, Day = (ushort)d.Day, Hour = (ushort)d.Hour, Minute = (ushort)d.Minute, Second = (ushort)d.Second };
}
