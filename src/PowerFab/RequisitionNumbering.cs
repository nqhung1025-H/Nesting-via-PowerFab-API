using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Hanlon.PowerFab.Nesting
{
    internal static class RequisitionNumbering
    {
        public static string Next(IEnumerable<string> numbers)
        {
            BigInteger highest=0;int padding=0;
            foreach(string raw in numbers)
            {
                var match=Regex.Match(raw.Trim(),"^REQ([0-9]+)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                if(!match.Success)continue;
                string digits=match.Groups[1].Value;var number=BigInteger.Parse(digits,CultureInfo.InvariantCulture);
                if(number>highest){highest=number;padding=digits.Length>1&&digits[0]=='0'?digits.Length:0;}
            }
            return "REQ"+(highest+1).ToString(padding>0?"D"+padding:"D",CultureInfo.InvariantCulture);
        }
    }

    internal sealed partial class PowerFabNestApi
    {
        private readonly Dictionary<string,string> preparedRequisitions = new Dictionary<string,string>(StringComparer.Ordinal);
        private bool requisitionSaveInProgress;

        public IDisposable? BeginRequisitionSave(bool needed)
        {
            if(!needed||requisitionSaveInProgress)return null;
            if(readModel==null)throw new InvalidOperationException("PowerFab's requisition list is unavailable. No nest was sent.");
            var lease=readModel.RequisitionSequenceLock();requisitionSaveInProgress=true;
            return new RequisitionSaveScope(this,lease);
        }
        private sealed class RequisitionSaveScope:IDisposable
        {
            private PowerFabNestApi? api;private readonly IDisposable lease;
            public RequisitionSaveScope(PowerFabNestApi api,IDisposable lease){this.api=api;this.lease=lease;}
            public void Dispose(){if(api==null)return;try{lease.Dispose();}finally{api.requisitionSaveInProgress=false;api=null;}}
        }

        public string NextRequisitionNumber()
        {
            if(readModel==null)throw new InvalidOperationException("Open Hanlon through PowerFab to read the next REQ number. No nest was sent.");
            try {return RequisitionNumbering.Next(readModel.RequisitionNumbers());}
            catch(Exception e) {throw new InvalidOperationException("PowerFab's requisition numbering could not be read. No nest was sent. "+e.Message,e);}
        }

        // Once per Save-all attempt: re-read PowerFab so an intervening requisition
        // advances the preview number. Confirmed sheets in a partial batch retain
        // their real requisition instead of creating another purchasing request.
        public void PrepareRequisitionBatch(IEnumerable<LoadedNestResult> layouts,string folder)
        {
            var pending=layouts.ToList();
            var groups=pending.Where(l=>l.Result.AutoRequisitionNumber).GroupBy(l=>l.Result.RequisitionBatchId).ToList();
            if(groups.Count==0)return;
            if(readModel==null)throw new InvalidOperationException("PowerFab's requisition list is unavailable. No nest was sent.");
            var numbers=readModel.RequisitionNumbers().ToList();var receipts=ReadReceipts(folder);
            // Respect manually entered references, including those not yet in
            // PowerFab, rather than allocating the same number to an auto batch.
            numbers.AddRange(pending.Where(l=>!l.Result.AutoRequisitionNumber).Select(l=>l.Result.RequisitionNumber));
            foreach(var group in groups)
            {
                if(string.IsNullOrWhiteSpace(group.Key))throw new InvalidDataException("This layout has no requisition batch identity. Generate nesting again.");
                var previous=receipts.Where(r=>r.RequisitionBatchId==group.Key&&!r.PartsReleased&&r.RequisitionNumber.Length>0)
                    .Select(r=>r.RequisitionNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(previous.Count>1)throw new InvalidDataException("This batch has more than one confirmed requisition. Review its saved history before continuing.");
                string number=previous.Count==1?previous[0]:RequisitionNumbering.Next(numbers);
                if(previous.Count==1&&!numbers.Contains(number,StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This batch's confirmed requisition was removed from PowerFab. Start a fresh combining run before creating another requirement.");
                preparedRequisitions[group.Key]=number;
                foreach(var loaded in group)
                {
                    loaded.Result.RequisitionNumber=number;
                    string temp=loaded.SourceFile+".number.tmp";
                    File.WriteAllText(temp,JsonSerializer.Serialize(loaded.Result,LoadedNestResult.JsonOptions));File.Move(temp,loaded.SourceFile,true);
                }
                numbers.Add(number);
            }
        }

        private void ResolveRequisition(LoadedNestResult loaded,string folder)
        {
            if(!loaded.Result.AutoRequisitionNumber)return;
            if(!preparedRequisitions.TryGetValue(loaded.Result.RequisitionBatchId,out string? number))
            {
                PrepareRequisitionBatch(new[]{loaded},folder);
                number=preparedRequisitions[loaded.Result.RequisitionBatchId];
            }
            loaded.Result.RequisitionNumber=number;
        }
    }
}
