using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Cadence.App.UI.ViewModels;

namespace Cadence.App.UI.Views;

/// <summary>
/// The table of schedules (and of the other tasks). A <see cref="ListViewItem"/> has no Invoke pattern, but tools\acceptance.ps1 and tools\live-ui-agents.ps1
/// press a schedule by its name to open it, so every row's automation peer (made by the table's peer, which is where WPF creates item peers) offers Invoke = open.
/// </summary>
internal sealed class ScheduleGrid : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ScheduleGridAutomationPeer(this);
}

internal sealed class ScheduleGridAutomationPeer(ListView owner) : ListViewAutomationPeer(owner)
{
    protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new ScheduleRowAutomationPeer(item, this);
}

/// <summary>A row for UI Automation: named by the schedule (the container's AutomationProperties.Name), selectable, and Invoke opens it.</summary>
internal sealed class ScheduleRowAutomationPeer(object item, SelectorAutomationPeer table) : ListBoxItemAutomationPeer(item, table), IInvokeProvider
{
    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    public void Invoke()
    {
        if (Item is IOpenRow row && row.OpenCommand.CanExecute(null)) row.OpenCommand.Execute(null);
    }
}
