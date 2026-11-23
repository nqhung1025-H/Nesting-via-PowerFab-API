$ErrorActionPreference = 'Stop'
$worker = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'src/ABE/ReadAbeReports.ps1'
$tokens = $null
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw ($errors | Out-String) }

# Exercise the worker's actual overload selection and await functions with a
# portable async fixture. Real WinRT PDF/OCR is checked by --reports-only on Windows.
Add-Type -TypeDefinition @'
using System;
using System.Threading;
using System.Threading.Tasks;
namespace HanlonAwaitFixture {
    public interface IAsyncAction { }
    public interface IAsyncOperation<T> { }
    public sealed class Action : IAsyncAction {
        public Task Task;
        public Action(Task task) { Task = task; }
    }
    public sealed class Operation<T> : IAsyncOperation<T> {
        public Task<T> Task;
        public Operation(Task<T> task) { Task = task; }
    }
    public static class Extensions {
        public static int ActionCalls, OperationCalls;
        public static Task AsTask(IAsyncAction action) {
            ActionCalls++; return ((Action)action).Task;
        }
        public static Task AsTask(IAsyncAction action, CancellationToken token) {
            throw new Exception("Wrong action overload");
        }
        public static Task<T> AsTask<T>(IAsyncOperation<T> operation) {
            OperationCalls++; return ((Operation<T>)operation).Task;
        }
        public static Task<T> AsTask<T>(Task<T> task) {
            throw new Exception("Wrong generic overload");
        }
        public static IAsyncAction Completed() { return new Action(Task.FromResult(true)); }
        public static IAsyncAction Delayed() { return new Action(Task.Delay(100)); }
        public static IAsyncOperation<int> Integer() { return new Operation<int>(Task.FromResult(42)); }
        public static IAsyncOperation<string> Text() { return new Operation<string>(Task.FromResult("report text")); }
        public static IAsyncAction FailedAction() {
            var source = new TaskCompletionSource<bool>();
            source.SetException(new InvalidOperationException("PDF render failure"));
            return new Action(source.Task);
        }
        public static IAsyncOperation<string> FailedOperation() {
            var source = new TaskCompletionSource<string>();
            source.SetException(new InvalidOperationException("OCR operation failure"));
            return new Operation<string>(source.Task);
        }
    }
}
'@
$source = [System.IO.File]::ReadAllText($worker)
$bridge = [regex]::Match($source, '(?s)# BEGIN ASYNC BRIDGE.*?# END ASYNC BRIDGE').Value
if ([string]::IsNullOrEmpty($bridge)) { throw 'Worker async bridge not found.' }
$bridge = $bridge.Replace('[System.WindowsRuntimeSystemExtensions]', '[HanlonAwaitFixture.Extensions]')
$bridge = $bridge.Replace('Windows.Foundation.IAsyncAction', 'HanlonAwaitFixture.IAsyncAction')
Invoke-Expression $bridge
$script:passed = 0
function Check([bool]$condition, [string]$name) {
    if (-not $condition) { throw $name }
    $script:passed++
}
Check ($errors.Count -eq 0) 'Embedded worker parses successfully'
Check (-not $asActionTask.IsGenericMethod) 'Action overload is non-generic'
Check ($asActionTask.GetParameters().Count -eq 1) 'Action overload has one parameter'
Check ($asActionTask.GetParameters()[0].ParameterType -eq [HanlonAwaitFixture.IAsyncAction]) 'Action overload accepts IAsyncAction'
Check ($asTask.IsGenericMethod) 'Result overload is generic'
Check ($asTask.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1') 'Result overload accepts IAsyncOperation'
$output = @(Await-Action ([HanlonAwaitFixture.Extensions]::Completed()))
Check ($output.Count -eq 0) 'Completed action adds no data to report JSON output'
Check ([HanlonAwaitFixture.Extensions]::ActionCalls -eq 1) 'Explicit action overload is invoked'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
Await-Action ([HanlonAwaitFixture.Extensions]::Delayed())
Check ($clock.ElapsedMilliseconds -ge 70) 'Render action is awaited before decoding'
Check ((Await-Result ([HanlonAwaitFixture.Extensions]::Integer()) ([int])) -eq 42) 'Integer async result is retained'
Check ((Await-Result ([HanlonAwaitFixture.Extensions]::Text()) ([string])) -eq 'report text') 'Text async result is retained'
Check ([HanlonAwaitFixture.Extensions]::OperationCalls -eq 2) 'Generic operation overload is invoked'
$reason = ''
try { Await-Action ([HanlonAwaitFixture.Extensions]::FailedAction()) }
catch { $reason = $_.Exception.GetBaseException().Message }
Check ($reason -eq 'PDF render failure') 'Underlying render failure is retained'
$reason = ''
try { Await-Result ([HanlonAwaitFixture.Extensions]::FailedOperation()) ([string]) }
catch { $reason = $_.Exception.GetBaseException().Message }
Check ($reason -eq 'OCR operation failure') 'Underlying OCR failure is retained'
Write-Output "PASS: $script:passed PowerShell async bridge checks (portable fixture; native WinRT requires Windows)."
