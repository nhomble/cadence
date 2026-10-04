using System.Runtime.InteropServices;

namespace Cadence.Scheduler.Com;

/// <summary>
/// One connected <c>Schedule.Service</c>, late bound (the ProgID is resolved at run time; there is no interop assembly).
/// Open one per gateway call on the calling thread and dispose it at the end. Never store it, and never hand a COM object to another thread.
/// </summary>
public sealed class TaskServiceSession : IDisposable
{
    object? _service;

    TaskServiceSession(object service, string connectedUserId)
    {
        _service = service;
        ConnectedUserId = connectedUserId;
    }

    /// <summary>"DOMAIN\user" (the COM <c>ConnectedUser</c> has no domain, so it is joined with <c>ConnectedDomain</c>).</summary>
    public string ConnectedUserId { get; }

    public dynamic Service => _service ?? throw new ObjectDisposedException(nameof(TaskServiceSession));

    public static TaskServiceSession Open()
    {
        Type? type;
        try { type = Type.GetTypeFromProgID("Schedule.Service"); }
        catch (COMException ex) { throw SchedulerException.FromCom(ex); }
        if (type is null)
            throw new SchedulerException(SchedulerErrorKind.Unavailable, "The Windows Task Scheduler COM class (Schedule.Service) is not registered on this machine.");

        object? service = null;
        try
        {
            service = Activator.CreateInstance(type) ?? throw new SchedulerException(SchedulerErrorKind.Unavailable, "Could not create Schedule.Service.");
            dynamic d = service;
            d.Connect();
            string domain = (string)d.ConnectedDomain;
            string user = (string)d.ConnectedUser;
            return new TaskServiceSession(service, string.IsNullOrEmpty(domain) ? user : domain + "\\" + user);
        }
        catch (Exception ex) when (SchedulerException.IsComFailure(ex))
        {
            ComRelease.Release(service);
            throw SchedulerException.FromCom(ex);
        }
        catch
        {
            ComRelease.Release(service);
            throw;
        }
    }

    /// <summary>The folder at <paramref name="path"/> (for example <c>\CadenceTest</c>), or null if it does not exist. The caller releases it.</summary>
    public object? TryGetFolder(string path)
    {
        try
        {
            dynamic d = Service;
            return (object)d.GetFolder(path);
        }
        catch (Exception ex) when (SchedulerException.IsComFailure(ex) && (uint)ex.HResult is 0x80070002 or 0x80070003)
        {
            return null;
        }
    }

    public void Dispose()
    {
        object? s = _service;
        _service = null;
        ComRelease.Release(s);
    }
}
