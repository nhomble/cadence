namespace Cadence.App.Interop;

/// <summary>The registered window message a second Cadence start broadcasts so the running window comes to the front (wsl-tray pattern).</summary>
internal static class ShowMe
{
    public const string MessageName = "CADENCE_SHOWME";

    static readonly uint Message = Native.RegisterWindowMessage(MessageName);

    /// <summary>The message id to compare with <c>msg</c> in a window hook.</summary>
    public static uint Id => Message;

    public static void Broadcast() => Native.PostMessage(new IntPtr(0xFFFF), Message, IntPtr.Zero, IntPtr.Zero);
}
