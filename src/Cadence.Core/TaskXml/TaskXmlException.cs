namespace Cadence.Core.TaskXml;

/// <summary>Thrown by <see cref="TaskXmlReader"/> and <see cref="TaskXmlWriter"/>; the message is plain English.</summary>
public sealed class TaskXmlException : Exception
{
    public TaskXmlException(string message) : base(message) { }

    public TaskXmlException(string message, Exception inner) : base(message, inner) { }
}
