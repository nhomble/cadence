using Cadence.Mvvm;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The contract of the Cadence.Mvvm module (ObservableObject, RelayCommand, AsyncRelayCommand): what any replacement MVVM library would have to keep doing.</summary>
public static class MvvmTests
{
    sealed class Sample : ObservableObject
    {
        string _text = "";
        public string Text { get => _text; set => Set(ref _text, value); }
        public bool SetReturns(string value) => Set(ref _text, value, nameof(Text));
    }

    static void WaitFor(string what, Func<bool> condition)
    {
        // The command's continuation runs on a thread-pool thread here (no window, no dispatcher): look again until it has finished.
        for (int i = 0; i < 600 && !condition(); i++) Thread.Sleep(5);
        T.True(what, condition());
    }

    public static void SetRaisesOnceWithTheMemberNameAndNotForEqualValues()
    {
        var vm = new Sample();
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        vm.Text = "a";
        T.Eq("one notification for a change", 1, names.Count);
        T.Eq("it names the property (CallerMemberName)", "Text", names[0]);
        vm.Text = "a";
        T.Eq("none for an equal value", 1, names.Count);
        T.True("Set reports a change", vm.SetReturns("b"));
        T.True("Set reports no change", !vm.SetReturns("b"));
        T.Eq("two notifications in total", 2, names.Count);
    }

    public static void RelayCommandRunsAndAsksCanExecute()
    {
        int runs = 0;
        bool allowed = false;
        var cmd = new RelayCommand(() => runs++, () => allowed);
        T.True("cannot execute while the predicate says no", !cmd.CanExecute(null));
        allowed = true;
        T.True("can execute once it says yes", cmd.CanExecute(null));
        cmd.Execute(null);
        T.Eq("Execute ran the action", 1, runs);
        int raised = 0;
        cmd.CanExecuteChanged += (_, _) => raised++;
        cmd.RaiseCanExecuteChanged();
        T.Eq("RaiseCanExecuteChanged raises the event", 1, raised);
        T.True("no predicate means always executable", new RelayCommand(() => { }).CanExecute(null));
    }

    public static void AsyncRelayCommandIgnoresReentryWhileRunningAndRaisesCanExecuteChangedTwice()
    {
        var gate = new TaskCompletionSource();
        int starts = 0, raised = 0;
        var cmd = new AsyncRelayCommand(() => { starts++; return gate.Task; });
        cmd.CanExecuteChanged += (_, _) => raised++;
        cmd.Execute(null);
        T.True("it is running", cmd.IsRunning);
        T.True("a running command cannot execute again", !cmd.CanExecute(null));
        cmd.Execute(null);
        T.Eq("the second call (a double click) was ignored", 1, starts);
        T.Eq("CanExecuteChanged was raised once when it started", 1, raised);
        gate.SetResult();
        WaitFor("it finished", () => !cmd.IsRunning);
        WaitFor("CanExecuteChanged was raised again when it ended", () => raised == 2);
        T.True("it can execute again", cmd.CanExecute(null));
    }

    public static void AsyncRelayCommandRoutesExceptionsToOnErrorAndStaysUsable()
    {
        Exception? seen = null;
        var cmd = new AsyncRelayCommand(() => throw new InvalidOperationException("boom"), onError: ex => seen = ex);
        cmd.Execute(null);
        WaitFor("the exception reached onError", () => seen is not null && !cmd.IsRunning);
        T.Eq("it is the thrown exception", "boom", seen?.Message);
        T.True("the command is usable afterwards", cmd.CanExecute(null));
    }
}
