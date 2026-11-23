using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbePdfReportReader
    {
        public static IReadOnlyList<AbeReportPage> Read(IReadOnlyList<string> files,CancellationToken token)
        {
            if(!OperatingSystem.IsWindows())throw new InvalidOperationException("PDF report reading requires Windows PDF and OCR support.");
            string directory=Path.Combine(Path.GetTempPath(),"Hanlon-AbeReports",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string script=Path.Combine(directory,"ReadReports.ps1");
                using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("Hanlon.PowerFab.Nesting.ReadAbeReports.ps1")
                    ??throw new InvalidOperationException("The PDF report reader is missing from this build."))
                using(var output=File.Create(script))resource.CopyTo(output);
                string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
                var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
                    RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),
                    StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)??throw new InvalidOperationException("Could not start the Windows PDF report reader.");
                Task<string> outputText=process.StandardOutput.ReadToEndAsync(),errorText=process.StandardError.ReadToEndAsync();
                try
                {
                    process.StandardInput.Write(JsonSerializer.Serialize(files));process.StandardInput.Close();
                    var clock=Stopwatch.StartNew();
                    while(!process.WaitForExit(200))
                    {
                        token.ThrowIfCancellationRequested();
                        if(clock.Elapsed>TimeSpan.FromMinutes(10))throw new InvalidOperationException("PDF report reading timed out.");
                    }
                    token.ThrowIfCancellationRequested();
                    string text=outputText.GetAwaiter().GetResult();
                    if(process.ExitCode!=0)throw new InvalidOperationException("The Windows PDF/OCR reader failed: "+errorText.GetAwaiter().GetResult().Trim());
                    return JsonSerializer.Deserialize<List<AbeReportPage>>(text,LoadedNestResult.JsonOptions)
                        ??throw new InvalidDataException("The PDF reader returned no report pages.");
                }
                catch{if(!process.HasExited)process.Kill(true);throw;}
            }
            finally{try{Directory.Delete(directory,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}
