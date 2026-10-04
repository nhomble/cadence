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
