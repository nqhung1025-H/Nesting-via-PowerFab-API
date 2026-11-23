using System;
using System.IO;
using System.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal static class PreviewViewportChecks
    {
        public static int Run()
        {
            int passed = 0;
            void Check(bool ok,string name) { if(!ok)throw new Exception("Preview: "+name); passed++; }
            bool Near(double a,double b) => Math.Abs(a-b)<1e-8;
            var view=new PreviewViewport(2000,1000);view.Resize(1000,500);
            Check(Near(view.Scale,0.5)&&Near(view.OffsetX,0)&&Near(view.OffsetY,0),"Initial full-layout fit");
            double x=(400-view.OffsetX)/view.Scale,y=(200-view.OffsetY)/view.Scale;
            view.ZoomAt(400,200,1);
            Check(view.Zoom>1,"Wheel up zooms in");
            Check(Near((400-view.OffsetX)/view.Scale,x)&&Near((200-view.OffsetY)/view.Scale,y),"Wheel preserves the image point under the cursor");
            view.Pan(1e6,1e6);Check(view.OffsetX<view.Width&&view.OffsetY<view.Height&&view.OffsetX+view.ImageWidth*view.Scale>0&&view.OffsetY+view.ImageHeight*view.Scale>0,"Long positive pan retains a visible drawing strip");
            view.Pan(-1e6,-1e6);Check(view.OffsetX<view.Width&&view.OffsetY<view.Height&&view.OffsetX+view.ImageWidth*view.Scale>0&&view.OffsetY+view.ImageHeight*view.Scale>0,"Long negative pan retains a visible drawing strip");
            view.ZoomAt(500,250,100);Check(Near(view.Zoom,PreviewViewport.MaximumZoom),"Maximum zoom bounded");
            view.ZoomAt(500,250,-100);Check(Near(view.Zoom,1)&&Near(view.Scale,0.5),"Wheel down returns to full-layout fit");
            view.ZoomAt(500,250,0.5);Check(view.Zoom>1&&view.Zoom<1.2,"High-resolution wheels accept fractional steps");
            view.Reset();Check(Near(view.Zoom,1)&&Near(view.OffsetX,0)&&Near(view.OffsetY,0),"Reset restores full-layout fit");
            view.ZoomAt(500,250,2);x=(500-view.OffsetX)/view.Scale;y=(250-view.OffsetY)/view.Scale;
            view.Resize(600,300);Check(Near((300-view.OffsetX)/view.Scale,x)&&Near((150-view.OffsetY)/view.Scale,y),"Resize preserves the viewed image centre");
            var portrait=new PreviewViewport(2400,6000);portrait.Resize(1000,600);portrait.ZoomAt(500,300,2);
            double offset=portrait.OffsetX;portrait.Pan(100,0);Check(Near(portrait.OffsetX,offset+100),"Narrow image pans without an automatic centre snap");
            portrait.Resize(0,0);portrait.ZoomAt(0,0,1);Check(Near(portrait.Scale,0),"Zero-sized hidden preview does not divide by zero");
            foreach(var size in new[]{(W:2400,H:6000),(W:2400,H:1200),(W:2000,H:1000)})
                foreach(var target in new[]{(X:0.05,Y:0.95),(X:0.95,Y:0.05),(X:0.3,Y:0.7)})
                {
                    var anchored=new PreviewViewport(size.W,size.H);anchored.Resize(1000,600);
                    double sx=anchored.OffsetX+size.W*anchored.Scale*target.X,sy=anchored.OffsetY+size.H*anchored.Scale*target.Y;
                    double px=(sx-anchored.OffsetX)/anchored.Scale,py=(sy-anchored.OffsetY)/anchored.Scale;
                    anchored.ZoomAt(sx,sy,2);
                    Check(Near((sx-anchored.OffsetX)/anchored.Scale,px)&&Near((sy-anchored.OffsetY)/anchored.Scale,py),"Fixed pointer during zoom-in, including margins and edge targets");
                    anchored.ZoomAt(sx,sy,-1);
                    Check(Near((sx-anchored.OffsetX)/anchored.Scale,px)&&Near((sy-anchored.OffsetY)/anchored.Scale,py),"Fixed pointer during zoom-out");
                    anchored.ZoomAt(sx,sy,-1);
                    Check(Near((sx-anchored.OffsetX)/anchored.Scale,px)&&Near((sy-anchored.OffsetY)/anchored.Scale,py),"Fixed pointer when returning to minimum zoom");
                    double ox=anchored.OffsetX,oy=anchored.OffsetY;anchored.Pan(1,1);
                    Check(Near(anchored.OffsetX,ox+1)&&Near(anchored.OffsetY,oy+1),"First drag after zoom does not jump");
                }
            string fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","Phase10and11");
            var doc=AbeNestImport.Read(Path.Combine(fixture,"H0306-PHASE10+11-10MM.nc"),Path.Combine(fixture,"H0306-PHASE10+11-10MM.sy2"));
            string svg=AbeNestImport.PreviewSvg(doc);
            var frame=new AbePreviewFrame(doc,3000,1500);
            var origin=frame.Project(0,0);var top=frame.Project(0,1500);var right=frame.Project(3000,0);
            Check(Math.Abs((right.X-origin.X)/(origin.Y-top.Y)-2)<0.00001,"Vector and exported previews preserve the physical sheet aspect and upward Y axis");
            Check(doc.Cuts.Concat(doc.Marks).SelectMany(p=>p).Select(p=>frame.Project(p.x,p.y))
                .All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&p.X>=0&&p.X<=AbePreviewFrame.PixelWidth&&p.Y>=0&&p.Y<=frame.PixelHeight),
                "All actual NC cutting and plate-name strokes fit the shared vector/export frame");
            Check(!svg.Contains("stroke-dasharray")&&!svg.Contains("SY2 bounds"),"Actual NC/SY2 preview has no dashed placement boxes or legend");
            Check(!svg.Contains("<text")&&!svg.Contains("Hanlon — imported ABE layout"),"Metadata is outside the zoomable sheet drawing");
            Check(System.Xml.Linq.XDocument.Parse(svg).Descendants().Count(e=>e.Name.LocalName=="polyline")==doc.Cuts.Count+doc.Marks.Count,"Removing the header retains every NC cutting and marking path");
            Check(svg.Contains("<polyline")&&doc.ProductionCount==16&&doc.PackerCount==12,"Actual cutting paths, marks and placement identities remain available");
            Console.WriteLine($"PASS: {passed} pointer zoom, panning, fit and clean-preview checks.");return passed;
        }
    }
}
