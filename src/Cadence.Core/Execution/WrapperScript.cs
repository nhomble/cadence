using Cadence.Core.TaskXml;

namespace Cadence.Core.Execution;

/// <summary>
/// The inline PowerShell wrapper (PLAN Appendix A, verified under Task Scheduler) and the Task Scheduler action that launches it.
/// The script reads the run spec from its own task's metadata, so nothing user-supplied is ever placed in the action's Arguments.
/// DO NOT EDIT <see cref="TemplateV1"/>: any change needs a <see cref="Version"/> bump and a re-run of the Phase 10 end-to-end tests.
/// [CP-A] Checkpoint A amended v1 before any task was ever registered with it (so no bump): dates are formatted with the
/// invariant culture (a Thai or Hijri user calendar made every holiday date miss), and the output read waits at most 10 s after
/// the program exits (a program that starts another one, e.g. <c>start "" notepad</c> in a .bat, kept the pipes open and hung the
/// wrapper until that program closed, so IgnoreNew then skipped the job's next runs). The fixture
/// <c>tests\Cadence.Tests\Fixtures\wrapper-v1.ps1</c> and PLAN Appendix A hold the same text.
/// </summary>
public static class WrapperScript
{
    /// <summary>Appendix A, verbatim (one statement group per line). <see cref="Build"/> joins the trimmed lines with single spaces.</summary>
    public const string TemplateV1 = """
        $ErrorActionPreference='Stop';
        $tp='{TASKPATH}';
        $rr='{RUNSROOT}';
        $ic=[Globalization.CultureInfo]::InvariantCulture;
        $w0=(Get-Process -Id $PID).StartTime;
        $rec=[ordered]@{v=1;wv=1;task=$tp;runId=$w0.ToString('yyyyMMdd-HHmmss-fff',$ic);wrapperStart=$w0.ToString('o');start=$null;end=$null;exit=$null;status='launchError';pid=$null;outBytes=0;errBytes=0;msg=$null};
        $dir=Join-Path $env:LOCALAPPDATA ($rr+'\runs\_unknown');
        $code=1;
        try{
        $svc=New-Object -ComObject Schedule.Service; $svc.Connect();
        $doc=$svc.GetFolder('\').GetTask($tp).Definition.RegistrationInfo.Documentation;
        $m=($doc -split [char]10,2)[1] | ConvertFrom-Json;
        $r=$m.run;
        $dir=Join-Path $env:LOCALAPPDATA ($rr+'\runs\'+$m.id);
        [void][IO.Directory]::CreateDirectory($dir);
        $today=[DateTime]::Now.ToString('yyyy-MM-dd',$ic);
        $skip=$false;
        if($r.skip){if($today -gt [string]$r.skip.until){$rec.msg='holiday list expired; ran anyway'}else{$h=$r.skip.dates.PSObject.Properties[$today]; if($h){$skip=$true; $rec.status='skipped'; $rec.msg='holiday: '+[string]$h.Value; $rec.end=[DateTime]::Now.ToString('o'); $code=0}}};
        if(-not $skip){
        $psi=New-Object Diagnostics.ProcessStartInfo;
        $psi.FileName=[Environment]::ExpandEnvironmentVariables([string]$r.exe);
        $psi.Arguments=[Environment]::ExpandEnvironmentVariables([string]$r.args);
        if($r.cwd){$psi.WorkingDirectory=[Environment]::ExpandEnvironmentVariables([string]$r.cwd)};
        $psi.UseShellExecute=$false;
        $cap=[bool]$r.capture;
        if($cap){$psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true; $psi.CreateNoWindow=$true; $enc=[Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage); if($r.encoding -eq 'utf8'){$enc=New-Object Text.UTF8Encoding $false}; $psi.StandardOutputEncoding=$enc; $psi.StandardErrorEncoding=$enc};
        if($r.env){foreach($kv in $r.env.PSObject.Properties){$psi.EnvironmentVariables[$kv.Name]=[string]$kv.Value}};
        $rec.start=[DateTime]::Now.ToString('o');
        $p=[Diagnostics.Process]::Start($psi); $rec.pid=$p.Id;
        if($cap){$o=$p.StandardOutput.ReadToEndAsync(); $e=$p.StandardError.ReadToEndAsync()};
        $ms=-1; if([int]$r.timeoutSec -gt 0){$ms=[int]$r.timeoutSec*1000};
        if($p.WaitForExit($ms)){$p.WaitForExit(); $code=$p.ExitCode; if($code -eq 0){$rec.status='ok'}else{$rec.status='fail'}}
        else{& taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null; $p.WaitForExit(); $code=$p.ExitCode; $rec.status='timeout'};
        $rec.exit=$code;
        $rec.end=[DateTime]::Now.ToString('o');
        if($cap){
        $cut=262144;
        $so=''; $se=''; if([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($o,$e),10000)){$so=$o.Result; $se=$e.Result}else{if($o.IsCompleted){$so=$o.Result}; if($e.IsCompleted){$se=$e.Result}; $rec.msg='output not fully captured: a program it started still holds the output open'};
        $rec.outBytes=$so.Length; $rec.errBytes=$se.Length;
        if($so.Length -gt $cut){$so=$so.Substring($so.Length-$cut)};
        if($se.Length -gt $cut){$se=$se.Substring($se.Length-$cut)};
        if($so.Length -gt 0){[IO.File]::WriteAllText((Join-Path $dir ($rec.runId+'.out.txt')),$so)};
        if($se.Length -gt 0){[IO.File]::WriteAllText((Join-Path $dir ($rec.runId+'.err.txt')),$se)}
        }
        }
        }catch{$rec.msg=$_.Exception.Message; if(-not $rec.end){$rec.end=[DateTime]::Now.ToString('o')}; [void][IO.Directory]::CreateDirectory($dir)}
        finally{
        $jf=Join-Path $dir 'runs.jsonl';
        [IO.File]::AppendAllText($jf,(($rec | ConvertTo-Json -Compress)+[char]10));
        if((Get-Item -LiteralPath $jf).Length -gt 2097152){$keep=Get-Content -LiteralPath $jf -Tail 4000 -Encoding UTF8; [IO.File]::WriteAllLines($jf,[string[]]$keep)};
        Get-ChildItem -LiteralPath $dir -Filter '*.txt' | Sort-Object Name -Descending | Select-Object -Skip 400 | Remove-Item -Force -ErrorAction SilentlyContinue
        };
        exit $code
        """;

    /// <summary>The wrapper version (`wv` in the metadata). The script text above is frozen for this version.</summary>
    public const int Version = 1;

    const string HiddenCommand = @"%SystemRoot%\System32\conhost.exe";
    const string PowerShellExe = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    const string PowerShellFlags = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command";

    // \Folder\Task name   (folder: letters and digits; task: the characters TaskNaming keeps, 1..80)
    // [CP-A] Also: no leading/trailing space, no double space (powershell.exe rejoins its -Command arguments with single spaces,
    // so '$tp=...' would no longer name the task) and no trailing dot (Windows strips it from the task's file name).
    static readonly System.Text.RegularExpressions.Regex TaskPathPattern =
        new(@"^\\[A-Za-z0-9]+\\(?=[A-Za-z0-9 _.,()+-]{1,80}\z)(?![ ])(?!.*[ ]{2})(?!.*[ .]\z).+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static readonly System.Text.RegularExpressions.Regex RunsRootPattern =
        new(@"^[A-Za-z0-9]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when the path is safe to embed inside the script's single-quoted PowerShell string.</summary>
    public static bool ValidateTaskPath(string? taskPath) =>
        taskPath is not null && TaskPathPattern.IsMatch(taskPath);

    public static bool ValidateRunsRoot(string? runsRoot) =>
        runsRoot is not null && RunsRootPattern.IsMatch(runsRoot);

    // [Phase 8] The exact prefix BuildAction writes (hidden: after "--headless <powershell> "), up to and including `$tp='...'; $rr='`.
    static readonly System.Text.RegularExpressions.Regex ActionPrefixPattern = new(
        @"^(?:--headless %SystemRoot%\\System32\\WindowsPowerShell\\v1\.0\\powershell\.exe )?-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \$ErrorActionPreference='Stop'; \$tp='(?<tp>[^']*)'; \$rr='",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the action is the Cadence wrapper as <see cref="BuildAction"/> writes it (anchored on the exact prefix and the
    /// matching executable); <paramref name="taskPath"/> is the embedded `$tp='...'`.
    /// </summary>
    public static bool TryGetTaskPath(ExecActionModel? action, out string taskPath)
    {
        taskPath = "";
        if (action?.Arguments is null) return false;
        System.Text.RegularExpressions.Match m = ActionPrefixPattern.Match(action.Arguments);
        if (!m.Success) return false;
        bool hidden = action.Arguments.StartsWith("--headless ", StringComparison.Ordinal);
        string expected = hidden ? HiddenCommand : PowerShellExe;
        if (!string.Equals(action.Command, expected, StringComparison.OrdinalIgnoreCase)) return false;
        taskPath = m.Groups["tp"].Value;
        return true;
    }

    /// <summary>The loose test Untrack uses: the arguments contain the wrapper's `$tp='` (even if the rest was edited).</summary>
    public static bool LooksLikeWrapper(string? arguments) =>
        arguments is not null && arguments.Contains("$tp='", StringComparison.Ordinal);

    /// <summary>The template with its lines trimmed and joined by single spaces (placeholders still in place).</summary>
    public static string JoinedTemplate()
    {
        var parts = new List<string>();
        foreach (string line in TemplateV1.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join(' ', parts);
    }

    /// <summary>The one-line script for a task: `{TASKPATH}` and `{RUNSROOT}` substituted after validation.</summary>
    public static string Build(string taskPath, string runsRoot)
    {
        if (!ValidateTaskPath(taskPath))
            throw new ArgumentException(@"The task path must look like \Folder\Task name and use only letters, digits, spaces and _.,()+- (name up to 80 characters).", nameof(taskPath));
        if (!ValidateRunsRoot(runsRoot))
            throw new ArgumentException("The runs-root name must be letters and digits only.", nameof(runsRoot));
        return JoinedTemplate().Replace("{TASKPATH}", taskPath, StringComparison.Ordinal).Replace("{RUNSROOT}", runsRoot, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hidden: conhost --headless runs PowerShell (output is captured by the script; conhost itself always exits 0).
    /// Visible: PowerShell directly, so the console is shown and the real exit code reaches Task Scheduler.
    /// The only `%` in the result are the two %SystemRoot% references, which Task Scheduler expands on purpose.
    /// </summary>
    public static ExecActionModel BuildAction(string taskPath, string runsRoot, bool hidden)
    {
        string script = Build(taskPath, runsRoot);
        return hidden
            ? new ExecActionModel(HiddenCommand, "--headless " + PowerShellExe + " " + PowerShellFlags + " " + script, null)
            : new ExecActionModel(PowerShellExe, PowerShellFlags + " " + script, null);
    }
}
