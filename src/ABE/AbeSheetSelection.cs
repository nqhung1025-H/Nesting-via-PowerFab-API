using System;
using System.Globalization;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeSheetSelection
    {
        private static decimal Mm(decimal value,NestMaterial material)=>
            value*(material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4m:1m);

        internal static bool FitsEnvelope(AbeNestDocument layout,NestMaterial material)
        {
            var bounds=layout.Bounds;
            // Test the unchanged import axes. A smaller sheet is usable when
            // its actual occupied strip fits; never rotate or scale the import.
            return bounds.Right<=(double)Mm(material.Length,material)+0.02
                && bounds.Top<=(double)Mm(material.Width,material)+0.02;
        }

        internal static NestMaterial? SuggestNewRequirement(AbeNestDocument layout,NestMaterial selected,PlateNestGroup group)
        {
            if(FitsEnvelope(layout,selected))return null;
            var proposed=new NestMaterial{Source="Purchase",Shape=group.Shape,Grade=group.Grade,Thickness=group.ThicknessMm,
                Length=3000,Width=1500,Unit="MM",Metric=true,
                Dimensions=group.ThicknessMm.ToString("0.########",CultureInfo.InvariantCulture)+"X1500"};
            // Offer the standard sheet only if it can contain this layout.
            // Full contour/NC verification still runs after user acceptance.
            return FitsEnvelope(layout,proposed)?proposed:null;
        }

        internal static string Prompt(NestMaterial selected,NestMaterial proposed)
        {
            string N(decimal n)=>n.ToString("0.##",CultureInfo.InvariantCulture);
            string kind=selected.RequiresOutline||selected.ParentRemnantPowerFabId.Length>0?"remnant":"stock sheet";
            return "The ABE layout does not fit the selected "+kind+" ("+N(Mm(selected.Length,selected))+" x "+N(Mm(selected.Width,selected))+" mm)."
                +"\n\nUse a new requisition sheet of "+N(proposed.Length)+" x "+N(proposed.Width)+" mm and continue?";
        }
    }
}
