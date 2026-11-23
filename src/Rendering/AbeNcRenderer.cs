using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeNcRenderer
    {
        public static void Draw(AbeNestDocument document,string path,double? sheetLength=null,double? sheetWidth=null,bool includeProcessTime=false)
        {
            using var drawing=new AbeVectorPreview(document,sheetLength,sheetWidth);
            using var bitmap=new Bitmap(drawing.Width,drawing.Height,PixelFormat.Format24bppRgb);
            using Graphics g=Graphics.FromImage(bitmap);g.Clear(Color.White);g.SmoothingMode=SmoothingMode.AntiAlias;
            // Business information belongs to the fixed result summary, so it
            // does not move or enlarge when the user zooms the sheet drawing.
            drawing.Draw(g,new RectangleF(0,0,drawing.Width,drawing.Height));
            if(includeProcessTime&&document.Reference.ProcessTime is AbeProcessTime runtime)
            {
                using var font=new Font("Segoe UI",20F,FontStyle.Bold,GraphicsUnit.Pixel);
                using var brush=new SolidBrush(Color.FromArgb(27,16,39));
                g.DrawString("Process time: "+runtime.Display,font,brush,40,6);
            }
            bitmap.Save(path,ImageFormat.Png);
        }
    }
}
