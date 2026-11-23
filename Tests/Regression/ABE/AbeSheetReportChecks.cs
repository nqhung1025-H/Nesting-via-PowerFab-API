using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeSheetReportChecks
    {
        private static int passed;
        private static readonly string Fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","Reports");
        private static void Check(bool ok,string name){if(!ok)throw new Exception("ABE reports: "+name);passed++;}
        private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){passed++;return;}throw new Exception("ABE reports accepted: "+name);}
        internal static string Name(int mm)=>"240175-02-PHASE90C-"+mm+"MM";
        internal static AbeReportPage ReadPage(int mm,string? file=null)
        {
            var page=JsonSerializer.Deserialize<AbeReportPage>(File.ReadAllText(Path.Combine(Fixture,Name(mm)+"-ocr.json")),LoadedNestResult.JsonOptions)!;
            page.File=file??Path.Combine(Fixture,Name(mm)+".pdf");return page;
        }
        internal static void AttachFixtureRuntime(AbeNestDocument document,int mm)
        {
            var batch=new AbeBatchRead();batch.Items.Add(new AbeBatchItem{Document=document});
            var page=ReadPage(mm);
            AbeSheetReports.Attach(batch,new[]{page.File},CancellationToken.None,(_,_)=>new[]{page});
            if(document.Reference.ProcessTime==null)throw new Exception("Actual PDF OCR fixture failed to match "+Name(mm));
        }
        private static AbeBatchRead Batch(params string[] names)
        {
            var batch=new AbeBatchRead();foreach(string name in names)batch.Items.Add(new AbeBatchItem{Document=new AbeNestDocument{Reference=new AbeImportReference{ProgramName=name}}});return batch;
        }
        public static int Run()
        {
            var expected=new Dictionary<int,long>{{5,9482},{6,1335},{12,4046}};
            foreach(var pair in expected)
            {
                var page=ReadPage(pair.Key);var parsed=AbeSheetReports.Parse(page,new[]{Name(pair.Key)}).ToList();
                Check(parsed.Count==1&&parsed[0].Seconds==pair.Value&&parsed[0].Name==Name(pair.Key),"Actual raster-valued PDF matches Data Name and Process Time for "+pair.Key+"mm");
                Check(!AbeSheetReports.Parse(page,new[]{Name(pair.Key)+"-01"}).Any(),"A missing source suffix never matches a numbered sheet");
                var batch=Batch(Name(pair.Key));AbeSheetReports.Attach(batch,new[]{page.File},CancellationToken.None,(_,_)=>new[]{page});
                var time=batch.Items[0].Document!.Reference.ProcessTime!;
                Check(time.Seconds==pair.Value&&time.Page==1&&time.ReportSha256.Length==64,"PDF provenance, duration and page retained");
                Check(time.ReportName==Path.GetFileName(page.File)&&time.DataName==Name(pair.Key),"Source sheet identity and report name retained");
                Check(AbeProcessTime.PowerFabValue(batch.Items[0].Document!.Reference)=="Process time: "+AbeProcessTime.Format(pair.Value),"PowerFab extra field carries the exact report time");
            }
            var names=expected.Keys.Select(Name).ToArray();var pages=expected.Keys.Select(mm=>ReadPage(mm)).ToArray();
            var all=Batch(names);AbeSheetReports.Attach(all,pages.Select(p=>p.File),CancellationToken.None,(_,_)=>pages.Reverse().ToArray());
            Check(all.Items.Select(i=>i.Document!.Reference.ProcessTime!.Seconds).SequenceEqual(expected.Values),"Split PDF ordering does not affect matching");
            string mergedFile=Path.Combine(Fixture,"Merged-sheet-reports.pdf");
            var merged=Batch(names);var mergedPages=new[]{12,5,6}.Select(mm=>ReadPage(mm,mergedFile)).ToArray();
            for(int i=0;i<mergedPages.Length;i++)mergedPages[i].Page=i+1;
            AbeSheetReports.Attach(merged,new[]{mergedFile},CancellationToken.None,(_,_)=>mergedPages);
            Check(merged.Items.Select(i=>i.Document!.Reference.ProcessTime!.Seconds).SequenceEqual(expected.Values),"One merged PDF supplies all sheet runtimes");
            Check(merged.Items.Select(i=>i.Document!.Reference.ProcessTime!.Page).SequenceEqual(new[]{2,3,1}),"Merged report page numbers retained independently of sheet order");
            Check(merged.Items.All(i=>i.Document!.Reference.ProcessTime!.ReportName=="Merged-sheet-reports.pdf"),"Merged report uses its actual filename as provenance, not a sheet name");
            var mixed=Batch(names);
            AbeSheetReports.Attach(mixed,pages.Select(p=>p.File).Append(mergedFile),CancellationToken.None,(_,_)=>pages.Concat(mergedPages).ToArray());
            Check(mixed.Items.Select(i=>i.Document!.Reference.ProcessTime!.Seconds).SequenceEqual(expected.Values),"Merged and separate reports together never double-count runtimes");
            var partial=Batch(names);var unreadable=new AbeReportPage{File=mergedFile,Page=4,Error="Render PDF page: damaged page"};
            AbeSheetReports.Attach(partial,new[]{mergedFile},CancellationToken.None,(_,_)=>mergedPages.Append(unreadable).ToArray());
            Check(partial.Items.All(i=>i.Document!.Reference.ProcessTime!=null)&&partial.ReportWarnings.Count==1
                &&partial.ReportWarnings[0].Contains("(page 4): Render PDF page: damaged page"),"An unreadable merged page identifies its page/stage and retains other successfully matched sheet times");
            var duplicate=Batch(names);AbeSheetReports.Attach(duplicate,pages.Select(p=>p.File),CancellationToken.None,(_,_)=>pages.Concat(pages).ToArray());
            Check(duplicate.Items.All(i=>i.Document!.Reference.ProcessTime!=null),"Repeated identical report pages do not double the time");
            var changed=ReadPage(5);changed.Words.Single(w=>w.Text=="02:38:02").Text="02:38:03";
            var conflict=Batch(Name(5));AbeSheetReports.Attach(conflict,new[]{pages[0].File},CancellationToken.None,(_,_)=>new[]{pages[0],changed});
            Check(conflict.Items[0].Document!.Reference.ProcessTime==null&&conflict.Items[0].Document!.Reference.ProcessTimeIssue=="Conflicting report times","Different times for the same sheet are never silently selected");
            var missing=Batch(Name(5));AbeSheetReports.Attach(missing,Array.Empty<string>(),CancellationToken.None);
            Check(missing.Items[0].Document!.Reference.ProcessTime==null&&AbeProcessTime.PowerFabValue(missing.Items[0].Document!.Reference)==null,"Missing report never creates zero runtime or a PowerFab time field");
            var failed=Batch(Name(5));AbeSheetReports.Attach(failed,new[]{pages[0].File},CancellationToken.None,(_,_)=>throw new InvalidDataException("unreadable PDF"));
            Check(failed.Items[0].Document!=null&&failed.ReportWarnings.Count==1&&failed.Items[0].Document!.Reference.ProcessTime==null,"PDF failure keeps the NC layout and reports the unavailable time");
            var bad=ReadPage(5);bad.Words.Single(w=>w.Text=="02:38:02").Text="02:78:02";
            Check(!AbeSheetReports.Parse(bad,names).Any(),"Invalid clock fields are rejected");
            var noLabel=ReadPage(5);noLabel.Words.RemoveAll(w=>AbeSheetReports.Key(w.Text)=="TIME");
            Check(!AbeSheetReports.Parse(noLabel,names).Any(),"A date or unlabeled duration cannot substitute for Process Time");
            Check(AbeProcessTime.Format(90061)=="25:01:01","Durations longer than one day never wrap their hours");
            Check(AbeProcessTime.DisplayOf(null)=="Not available","Unknown runtime is explicit");
            var runtime=all.Items[0].Document!.Reference.ProcessTime!;runtime.DataName="other sheet";
            Reject(()=>AbeProcessTime.Validate(all.Items[0].Document!.Reference),"Runtime from another sheet");
            runtime.DataName=Name(5);runtime.Seconds=-1;Reject(()=>AbeProcessTime.Validate(all.Items[0].Document!.Reference),"Negative duration");
            string temporary=Path.Combine(Path.GetTempPath(),"HanlonReportChecks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
            try
            {
                string source=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","240175");
                foreach(int mm in expected.Keys)
                {
                    File.Copy(Path.Combine(source,Name(mm)+".nc"),Path.Combine(temporary,Name(mm)));
                    File.Copy(Path.Combine(source,Name(mm)+".sy2"),Path.Combine(temporary,Name(mm)+".SY2"));
                    File.Copy(Path.Combine(Fixture,Name(mm)+".pdf"),Path.Combine(temporary,Name(mm)+".pdf"));
                }
                IReadOnlyList<AbeReportPage> Read(IReadOnlyList<string> paths,CancellationToken _) => expected.Keys
                    .Select(mm=>ReadPage(mm,paths.Single(p=>Path.GetFileName(p)==Name(mm)+".pdf"))).ToArray();
                var folder=AbeImportBatch.ReadFolder(temporary,CancellationToken.None,null,Read);
                Check(folder.Items.Count==3&&folder.Items.All(i=>i.Document!.Reference.ProcessTime!=null),"Select folder automatically reads all NC/SY2 pairs and PDF runtimes in one operation");
                var file=AbeImportBatch.ReadFiles(new[]{Path.Combine(temporary,Name(5))},CancellationToken.None,null,Read);
                Check(file.Items.Single().Document!.Reference.ProcessTime!.Seconds==9482,"Select files discovers sibling reports without a second selection");
                var selection=AbeImportBatch.ReadFiles(Directory.GetFiles(temporary),CancellationToken.None,null,Read);
                Check(selection.Items.Count==3&&selection.Items.All(i=>i.Document!.Reference.ProcessTime!=null),"Selecting NC, SY2 and PDFs together produces no unsupported-report input issues");
                foreach(string report in Directory.GetFiles(temporary,"*.pdf"))File.Delete(report);
                string reportDirectory=Path.Combine(temporary,"Reports");Directory.CreateDirectory(reportDirectory);
                string combinedPath=Path.Combine(reportDirectory,"All thicknesses.pdf");File.Copy(mergedFile,combinedPath);
                IReadOnlyList<AbeReportPage> ReadCombined(IReadOnlyList<string> paths,CancellationToken _)
                {
                    if(paths.Count!=1||paths[0]!=combinedPath)throw new Exception("Merged report discovery did not supply the single PDF.");
                    return new[]{12,5,6}.Select((mm,index)=>{var page=ReadPage(mm,combinedPath);page.Page=index+1;return page;}).ToArray();
                }
                var combined=AbeImportBatch.ReadFolder(temporary,CancellationToken.None,null,ReadCombined);
                Check(combined.Items.Count==3&&combined.Items.All(i=>i.Document!=null),"Select folder with one merged PDF imports all NC/SY2 sheets");
                Check(combined.Items.All(i=>i.Document!.Reference.ProcessTime!.Seconds==expected.Single(p=>Name(p.Key)==i.Name).Value),"Single merged PDF supplies the correct time to each imported thickness");
                Check(combined.Items.All(i=>i.Document!.Reference.ProcessTime!.ReportName=="All thicknesses.pdf"),"Nested merged PDF is discovered without a sheet-specific filename");
                Check(combined.Items.Single(i=>i.Name==Name(12)).Document!.Reference.ProcessTime!.Page==1,"Merged report page order is independent of NC file order");
                var combinedSelection=AbeImportBatch.ReadFiles(Directory.GetFiles(temporary).Append(combinedPath),CancellationToken.None,null,ReadCombined);
                Check(combinedSelection.Items.Count==3&&combinedSelection.Items.All(i=>i.Document!.Reference.ProcessTime!=null),"Select files accepts all NC/SY2 pairs plus one merged PDF in one operation");
                var cancelled=new CancellationTokenSource();cancelled.Cancel();bool wasCancelled=false;
                try{AbeSheetReports.Attach(Batch(Name(5)),new[]{pages[0].File},cancelled.Token,(_,token)=>{token.ThrowIfCancellationRequested();return pages;});}
                catch(OperationCanceledException){wasCancelled=true;}
                Check(wasCancelled,"Report reading propagates cancellation");
            }
            finally{Directory.Delete(temporary,true);}
            Console.WriteLine($"PASS: {passed} ABE sheet-report runtime matching, merged/split pages, failure and one-import checks.");
            return passed;
        }
    }
}
