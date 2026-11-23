using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbePartNames
    {
        private static readonly Regex Lot=new Regex(@"^\s*(?:LOT[\s#:_-]*)?(?<n>\d+)(?=\s|[-._:/]|$)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        private static readonly Regex PrefixLot=new Regex(@"^[A-Z]+(?<n>\d+)$",RegexOptions.CultureInvariant);
        private static readonly Regex Packer=new Regex(@"^(?:(?<x>\d+(?:\.\d+)?)\s*[X×]\s*(?<y>\d+(?:\.\d+)?)\s*[-_ ]*)?PACKER$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        private static string Digits(string text){string n=text.TrimStart('0');return n.Length==0?"0":n;}
        public static bool Direct(string name,string job,string piece)
        {string n=AbeNestImport.Canonical(name);return n==AbeNestImport.Canonical(piece)||n==AbeNestImport.Canonical(job+piece);}
        public static bool Matches(string name,string job,string piece,string lot)
        {
            if(Direct(name,job,piece))return true;
            string n=AbeNestImport.Canonical(name),mark=AbeNestImport.Canonical(piece);
            if(mark.Length==0||n.Length<=mark.Length||!n.EndsWith(mark,StringComparison.Ordinal))return false;
            Match left=PrefixLot.Match(n.Substring(0,n.Length-mark.Length)),right=Lot.Match(lot??"");
            return left.Success&&right.Success&&Digits(left.Groups["n"].Value)==Digits(right.Groups["n"].Value);
        }
        public static bool IsPacker(string name)=>Packer.IsMatch(name.Trim());
        public static bool PackerSize(string name,out double x,out double y)
        {
            var match=Packer.Match(name.Trim());x=y=0;
            return match.Success&&double.TryParse(match.Groups["x"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out x)
                &&double.TryParse(match.Groups["y"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out y)&&x>0&&y>0;
        }
        // These are AMADA profile settings, not the physical PowerFab grade.
        // 4 and 5 mm share 4.5; resolve that ambiguity from the filename/parts.
        private static readonly (decimal Nominal,decimal Code)[] Thicknesses =
            { (1m,1.2m),(2m,2m),(3m,3.2m),(4m,4.5m),(5m,4.5m),(6m,6m),
              (8m,8m),(10m,9m),(12m,12m),(16m,16m),(20m,19m),(25m,25m) };
        private static decimal? FilenameThickness(string programName)
        {
            var names=Regex.Matches(programName,@"(?<![0-9.])(?<n>\d+(?:\.\d+)?)\s*MM(?![A-Z0-9])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            var values=new System.Collections.Generic.HashSet<decimal>();
            foreach(Match name in names)
            {
                if(!decimal.TryParse(name.Groups["n"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out decimal n)||n<=0||n>100000)
                    throw new System.IO.InvalidDataException("ABE import: invalid PowerFab thickness in the program filename.");
                values.Add(n);
            }
            if(values.Count>1)throw new System.IO.InvalidDataException("ABE import: several thicknesses occur in one program filename. Use one thickness per NC/SY2 pair.");
            foreach(decimal n in values)return n;
            return null;
        }
        public static decimal PowerFabThickness(string material,decimal thickness,string programName="")
        {
            decimal? declared=FilenameThickness(programName);
            if(declared.HasValue)return declared.Value;
            var targets=Thicknesses.Where(t=>Math.Abs(t.Code-thickness)<=0.01m).Select(t=>t.Nominal).Distinct().ToArray();
            return targets.Length==1?targets[0]:thickness;
        }
        public static bool AcceptsPowerFabThickness(decimal nominal,decimal code,string programName="")
        {
            decimal? declared=FilenameThickness(programName);
            if(declared.HasValue)return Math.Abs(nominal-declared.Value)<=0.01m;
            return Math.Abs(nominal-code)<=0.01m || Thicknesses.Any(t=>Math.Abs(t.Nominal-nominal)<=0.01m&&Math.Abs(t.Code-code)<=0.01m);
        }
        public static decimal MachineThickness(string profile)
        {
            string compact=Regex.Replace(profile,@"\s+","");
            var code=Regex.Match(compact,@"^(?:[A-Z][A-Z0-9]*[-_]|[A-Z]*)(?<n>\d+(?:\.\d+)?)(?:[A-Z][A-Z0-9_-]*)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(!code.Success||!decimal.TryParse(code.Groups["n"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out decimal n)||n<=0)
                throw new System.IO.InvalidDataException("ABE import: cannot read the numeric thickness from NC profile "+profile+".");
            return n;
        }
    }
}
