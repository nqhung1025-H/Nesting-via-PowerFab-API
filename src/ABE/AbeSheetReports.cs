using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hanlon.PowerFab.Nesting
{
    public sealed class AbeProcessTime
    {
        public long Seconds { get; set; }
        public string DataName { get; set; } = "";
        public string ReportName { get; set; } = "";
        public string ReportFile { get; set; } = "ABE-sheet-report.pdf";
        public string ReportSha256 { get; set; } = "";
        public int Page { get; set; }
        [JsonIgnore] public string SourcePath { get; set; } = "";
        [JsonIgnore] public string Display => Format(Seconds);
        public static string Format(long seconds) => (seconds / 3600).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (seconds / 60 % 60).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (seconds % 60).ToString("D2", CultureInfo.InvariantCulture);
        public static string DisplayOf(AbeImportReference? reference) => reference?.ProcessTime?.Display
            ?? (string.IsNullOrWhiteSpace(reference?.ProcessTimeIssue) ? "Not available" : reference!.ProcessTimeIssue);
        public static void Validate(AbeImportReference? reference)
        {
            if(reference?.ProcessTime is not AbeProcessTime time)return;
            if(time.Seconds<=0||time.Seconds>3599999||time.Page<1||time.Page>500
                ||!string.Equals(time.DataName,reference.ProgramName,StringComparison.Ordinal)
                ||!Regex.IsMatch(time.ReportSha256,@"\A[0-9A-Fa-f]{64}\z")
                ||Path.GetFileName(time.ReportFile)!=time.ReportFile||string.IsNullOrWhiteSpace(time.ReportFile))
                throw new InvalidDataException("The ABE sheet-report runtime does not match this sheet. Import the folder again.");
        }
        public static string? PowerFabValue(AbeImportReference? reference)
        {
            Validate(reference);
            return reference?.ProcessTime==null?null:"Process time: "+reference.ProcessTime.Display;
        }
    }

    internal sealed class AbeReportWord
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Right => X + Width;
        public double CenterY => Y + Height / 2;
    }
    internal sealed class AbeReportPage
    {
        public string File { get; set; } = "";
        public int Page { get; set; }
        public string Error { get; set; } = "";
        public List<AbeReportWord> Words { get; set; } = new List<AbeReportWord>();
    }

    internal static class AbeSheetReports
    {
        // Only OCR whitespace and equivalent dash glyphs are normalized. The
        // source program name remains unchanged, including its -01 suffix.
        internal static string Key(string value) => Regex.Replace(Regex.Replace(value.Replace('\u2010','-').Replace('\u2011','-')
            .Replace('\u2012','-').Replace('\u2013','-').Replace('\u2014','-').Replace('\u2212','-'), @"\s+", ""),@"-+","-").ToUpperInvariant();

        private static IEnumerable<AbeReportWord> Labels(List<AbeReportWord> words, string first, string second)
        {
            foreach (var word in words)
            {
                string key = Key(word.Text);
                if (key == first + second) { yield return word; continue; }
                if (key != first) continue;
                foreach (var end in words.Where(w => Key(w.Text) == second && w.Y >= word.Y - word.Height
                    && w.Y - word.Y <= Math.Max(word.Height, w.Height) * 4
                    && w.X >= word.X - word.Height && w.X <= word.Right + word.Height * 5))
                {
                    // For stacked Process / Time use the Time row to locate
                    // the value, never a date or the Process Quantity above it.
                    if (first == "PROCESS") yield return end;
                    else yield return new AbeReportWord { X = word.X, Y = Math.Min(word.Y,end.Y),
                        Width = Math.Max(word.Right,end.Right)-word.X, Height = Math.Max(word.Y+word.Height,end.Y+end.Height)-Math.Min(word.Y,end.Y) };
                }
            }
        }

        internal static IEnumerable<(string Name, long Seconds)> Parse(AbeReportPage page, IEnumerable<string> names)
        {
            var expected = names.GroupBy(Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key,g => g.Single());
            var anchors = Labels(page.Words,"DATA","NAME").OrderBy(w => w.Y).ToList();
            for (int i = 0; i < anchors.Count; i++)
            {
                var anchor=anchors[i];
                string body=string.Concat(page.Words.Where(w => w.X >= anchor.Right-1
                    && Math.Abs(w.CenterY-anchor.CenterY) <= Math.Max(w.Height,anchor.Height)*0.85).OrderBy(w=>w.X).Select(w=>w.Text));
                if (!expected.TryGetValue(Key(body),out string? name)) continue;
                double next=i+1<anchors.Count?anchors[i+1].Y:double.MaxValue;
                var seconds=new HashSet<long>();
                foreach(var time in Labels(page.Words,"PROCESS","TIME").Where(w=>w.Y>=anchor.Y&&w.Y<next))
                {
                    string row=string.Concat(page.Words.Where(w=>w.X>=time.Right+1
                        && Math.Abs(w.CenterY-time.CenterY)<=Math.Max(w.Height,time.Height)*0.9).OrderBy(w=>w.X).Select(w=>w.Text));
                    foreach(Match match in Regex.Matches(row,@"(?<!\d)(\d{1,3}):([0-5]\d):([0-5]\d)(?!\d)"))
                    {
                        long value=long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture)*3600
                            +long.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)*60+long.Parse(match.Groups[3].Value,CultureInfo.InvariantCulture);
                        if(value>0)seconds.Add(value);
                    }
                }
                foreach(long value in seconds)yield return (name,value);
            }
        }

        public static void Attach(AbeBatchRead batch,IEnumerable<string> files,CancellationToken token,
            Func<IReadOnlyList<string>,CancellationToken,IReadOnlyList<AbeReportPage>>? reader=null)
        {
            var items=batch.Items.Where(i=>i.Document!=null).ToList();
            if(items.Count==0)return;
            var pdfs=files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var item in items){item.Document!.Reference.ProcessTime=null;item.Document.Reference.ProcessTimeIssue="No matching report";}
            if(pdfs.Count==0)return;
            try
            {
                if(pdfs.Count>200||pdfs.Any(p=>new FileInfo(p).Length>64L*1024*1024)
                    ||pdfs.Sum(p=>new FileInfo(p).Length)>256L*1024*1024)throw new InvalidDataException("Sheet-report PDF size limit exceeded.");
                var names=items.Select(i=>i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var matches=new Dictionary<string,List<AbeProcessTime>>(StringComparer.OrdinalIgnoreCase);
                var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var page in (reader??AbePdfReportReader.Read)(pdfs,token))
                {
                    token.ThrowIfCancellationRequested();
                    if(!pdfs.Contains(page.File,StringComparer.OrdinalIgnoreCase))continue;
                    if(page.Error.Length>0)
                    {
                        batch.ReportWarnings.Add(Path.GetFileName(page.File)+(page.Page>0?" (page "+page.Page+")":"")+": "+page.Error);
                        continue;
                    }
                    foreach(var pair in Parse(page,names))
                    {
                        if(!hashes.TryGetValue(page.File,out string? hash))hashes[page.File]=hash=Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(page.File)));
                        if(!matches.TryGetValue(pair.Name,out var list))matches[pair.Name]=list=new List<AbeProcessTime>();
                        list.Add(new AbeProcessTime{DataName=pair.Name,Seconds=pair.Seconds,ReportName=Path.GetFileName(page.File),
                            ReportSha256=hash,Page=page.Page,SourcePath=page.File});
                    }
                }
                foreach(var item in items)
                    if(matches.TryGetValue(item.Name,out var matchesForSheet))
                    {
                        if(matchesForSheet.Select(m=>m.Seconds).Distinct().Count()!=1)
                        {item.Document!.Reference.ProcessTimeIssue="Conflicting report times";continue;}
                        item.Document!.Reference.ProcessTime=matchesForSheet.First();item.Document.Reference.ProcessTimeIssue="";
                    }
                if(batch.ReportWarnings.Count>0)
                    foreach(var item in items.Where(i=>i.Document!.Reference.ProcessTime==null))item.Document!.Reference.ProcessTimeIssue="Report could not be read";
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex)when(ex is IOException||ex is InvalidDataException||ex is InvalidOperationException||ex is UnauthorizedAccessException
                ||ex is System.Text.Json.JsonException||ex is System.ComponentModel.Win32Exception||ex is NotSupportedException)
            {
                batch.ReportWarnings.Add(ex.Message);
                foreach(var item in items)item.Document!.Reference.ProcessTimeIssue="Report could not be read";
            }
        }
    }
}
