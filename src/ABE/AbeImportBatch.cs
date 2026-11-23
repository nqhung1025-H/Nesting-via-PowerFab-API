using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class AbeBatchItem
    {
        public string NcPath="",Sy2Path="",Status="";
        public AbeNestDocument? Document;
        public string Name=>Document?.Reference.ProgramName??Path.GetFileName(NcPath.Length>0?NcPath:Sy2Path);
    }

    internal sealed class AbeBatchRead
    {
        public List<AbeBatchItem> Items=new List<AbeBatchItem>();
        public int DuplicateSelections;
        public List<string> ReportWarnings = new List<string>();
    }

    internal static class AbeImportBatch
    {
        private static readonly string[] NcExtensions={"",".NC",".CNC",".GCD",".LCD",".TXT"};
        private static bool Same(string a,string b)=>StringComparer.OrdinalIgnoreCase.Equals(a,b);
        private static bool IsSy2(string p)=>Same(Path.GetExtension(p),".SY2");
        private static bool IsNc(string p)=>NcExtensions.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase);

        public static AbeBatchRead ReadFolder(string folder,CancellationToken token,Action<string>? progress=null,
            Func<IReadOnlyList<string>,CancellationToken,IReadOnlyList<AbeReportPage>>? reportReader=null)
        {
            var files=new List<string>();
            foreach(string path in Directory.EnumerateFiles(folder,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint}))
            {
                token.ThrowIfCancellationRequested();files.Add(path);
                if(files.Count>50000)throw new InvalidDataException("This folder contains over 50,000 files. Choose a smaller nesting folder.");
            }
            // Reports, DXFs and other workshop files are ignored. An extensionless
            // orphan is only treated as NC if it has an NC program header.
            var sy2Names=files.Where(IsSy2).Select(s=>Path.Combine(Path.GetDirectoryName(s)!,Path.GetFileNameWithoutExtension(s))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected=files.Where(f=>IsSy2(f)||IsNc(f)&&!sy2Names.Contains(Path.Combine(Path.GetDirectoryName(f)!,AbeNestImport.ProgramStem(f)))
                &&(Path.GetExtension(f).Length>0&&!Same(Path.GetExtension(f),".TXT")||LooksLikeNc(f))).ToList();
            return ReadFiles(selected,token,progress,reportReader,files.Where(f=>Same(Path.GetExtension(f),".PDF")));
        }

        private static bool LooksLikeNc(string path)
        {
            using var stream=File.OpenRead(path);byte[] bytes=new byte[8192];int count=stream.Read(bytes,0,bytes.Length);
            string header=Encoding.ASCII.GetString(bytes,0,count);
            return header.Contains("M100",StringComparison.OrdinalIgnoreCase)&&header.Contains("M102",StringComparison.OrdinalIgnoreCase);
        }

        public static AbeBatchRead ReadFiles(IEnumerable<string> selected,CancellationToken token,Action<string>? progress=null,
            Func<IReadOnlyList<string>,CancellationToken,IReadOnlyList<AbeReportPage>>? reportReader=null,IEnumerable<string>? reportFiles=null)
        {
            var result=new AbeBatchRead();var input=selected.Select(Path.GetFullPath).ToList();
            var allInput=input.Distinct(StringComparer.OrdinalIgnoreCase).ToList();result.DuplicateSelections=input.Count-allInput.Count;
            var pdfs=allInput.Where(f=>Same(Path.GetExtension(f),".PDF")).Concat(reportFiles??Array.Empty<string>()).ToList();
            var files=allInput.Where(f=>!Same(Path.GetExtension(f),".PDF")).ToList();
            var directoryFiles=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> Siblings(string path)
            {
                string dir=Path.GetDirectoryName(path)!;
                if(!directoryFiles.TryGetValue(dir,out var found))directoryFiles.Add(dir,found=Directory.EnumerateFiles(dir).Select(Path.GetFullPath).ToList());
                return found;
            }
            void AddIssue(string path,string error)=>result.Items.Add(new AbeBatchItem{NcPath=IsSy2(path)?"":path,Sy2Path=IsSy2(path)?path:"",Status=error});
            var pairs=new Dictionary<string,AbeBatchItem>(StringComparer.OrdinalIgnoreCase);
            foreach(string file in files.OrderBy(f=>IsSy2(f)?1:0).ThenBy(f=>f,StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if(!IsNc(file)&&!IsSy2(file)){AddIssue(file,"Unsupported input. Select NC programs or SY2 files.");continue;}
                    string nc,sy2;
                    if(IsSy2(file))
                    {
                        if(pairs.Values.Any(p=>Same(p.Sy2Path,file))){result.DuplicateSelections++;continue;}
                        string stem=Path.GetFileNameWithoutExtension(file);
                        var matches=Siblings(file).Where(p=>!IsSy2(p)&&IsNc(p)&&Same(AbeNestImport.ProgramStem(p),stem)).ToList();
                        if(matches.Count!=1){AddIssue(file,matches.Count==0?"Matching NC program is missing.":"Multiple NC programs match this SY2. Select the intended NC file explicitly.");continue;}
                        nc=matches[0];sy2=file;
                    }
                    else
                    {
                        var matches=Siblings(file).Where(p=>IsSy2(p)&&Same(Path.GetFileNameWithoutExtension(p),AbeNestImport.ProgramStem(file))).ToList();
                        if(matches.Count!=1){AddIssue(file,matches.Count==0?"Matching SY2 is missing.":"Multiple SY2 files match this program.");continue;}
                        nc=file;sy2=matches[0];
                    }
                    string key=nc+"\n"+sy2;
                    if(pairs.ContainsKey(key)){result.DuplicateSelections++;continue;}
                    pairs.Add(key,new AbeBatchItem{NcPath=nc,Sy2Path=sy2});
                }
                catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException){AddIssue(file,ex.Message);}
            }
            if(pairs.Count>200)throw new InvalidDataException("Choose up to 200 NC/SY2 sheet pairs per import.");
            var ambiguous=pairs.Values.GroupBy(p=>p.Sy2Path,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).SelectMany(g=>g).ToHashSet();
            long totalBytes=0;
            foreach(var item in pairs.Values.OrderBy(p=>p.NcPath,StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();result.Items.Add(item);
                if(ambiguous.Contains(item)){item.Status="Several selected NC programs use the same SY2. Select one NC variant.";continue;}
                try
                {
                    progress?.Invoke("Reading "+Path.GetFileName(item.NcPath));
                    long size=new FileInfo(item.NcPath).Length+new FileInfo(item.Sy2Path).Length;
                    if(size>32L*1024*1024||totalBytes+size>128L*1024*1024)throw new InvalidDataException("Import size limit exceeded. Select a smaller group of sheets (128 MiB per batch).");
                    totalBytes+=size;item.Document=AbeNestImport.Read(item.NcPath,item.Sy2Path);item.Status="Ready to match";
                }
                catch(Exception ex)when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is OverflowException)
                {item.Status=ex.Message;}
            }
            if(reportFiles==null)
                foreach(string directory in pairs.Values.Select(p=>Path.GetDirectoryName(p.NcPath)!).Distinct(StringComparer.OrdinalIgnoreCase))
                    pdfs.AddRange(Directory.EnumerateFiles(directory).Where(f=>Same(Path.GetExtension(f),".PDF")));
            AbeSheetReports.Attach(result,pdfs,token,reportReader);
            return result;
        }
    }

    // Pending sheets share a quantity budget; checking another input cannot
    // allocate copies already present on a reviewed sheet in this batch.
    internal sealed class AbeBatchBudget
    {
        private readonly Dictionary<string,int> used=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        public AbePartMatch Match(AbeNestDocument doc,NestingSnapshot snapshot,IReadOnlyList<SavedNestReceipt> receipts)
            =>AbeNestImport.MatchParts(doc,snapshot,receipts,used);
        public void Reserve(NestResultPackage package)
        {
            foreach(var part in package.Parts)
            {string key=LoadedNestResult.PartKey(part);used[key]=checked((used.TryGetValue(key,out int n)?n:0)+part.Quantity);}
        }
    }
}
