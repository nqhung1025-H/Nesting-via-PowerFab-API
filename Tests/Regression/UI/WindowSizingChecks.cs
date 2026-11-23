using System;
using System.Drawing;

namespace Hanlon.PowerFab.Nesting
{
    internal static class WindowSizingChecks
    {
        public static int Run()
        {
            int passed = 0;
            void Check(bool ok, string name) { if (!ok) throw new Exception("Window sizing: " + name); passed++; }
            foreach (int dpi in new[] { 96, 120, 144, 192 })
                foreach (var desktop in new[] { new Size(1366, 728), new Size(1920, 1040), new Size(3440, 1400), new Size(3840, 2120) })
                {
                    double scale = dpi / 96.0;
                    var frame = new Size((int)(16 * scale), (int)(39 * scale));
                    var controls = new Size((int)(112 * scale), (int)(420 * scale));
                    Size window = NestingWindowSizing.Fit(desktop, frame, controls, dpi);
                    Check(window.Width > 0 && window.Height > 0 && window.Width < desktop.Width && window.Height < desktop.Height,
                        "Complete window fits the working area, including frame, at " + dpi + " DPI on " + desktop);
                    Check(window.Width <= 1116 * scale, "Ultrawide desktops do not produce an oversized window");
                    int drawWidth = window.Width - frame.Width - controls.Width;
                    int drawHeight = window.Height - frame.Height - controls.Height;
                    if (drawWidth >= 720 * scale && drawHeight >= 200 * scale)
                        Check(Math.Abs(drawWidth - 2 * drawHeight) <= 1, "Landscape sheet fits without wasted side space");
                }
            Size normal = NestingWindowSizing.Fit(new Size(1920, 1040), new Size(16, 39), new Size(112, 420), 96);
            Size doubled = NestingWindowSizing.Fit(new Size(3840, 2080), new Size(32, 78), new Size(224, 840), 192);
            Check(Math.Abs(normal.Width * 2 - doubled.Width) <= 2 && Math.Abs(normal.Height * 2 - doubled.Height) <= 2,
                "Equivalent desktop and controls at 200 percent retain the logical layout");
            Size tiny = NestingWindowSizing.Fit(new Size(640, 480), new Size(16, 39), new Size(112, 420), 96);
            Check(tiny.Width <= 640 && tiny.Height <= 480 && tiny.Width > 0 && tiny.Height > 0,
                "A small remote desktop is not forced outside its working area");
            foreach(var available in new[]{new Size(760,230),new Size(1200,480),new Size(420,600),new Size(0,0),new Size(80,40)})
                foreach(double ratio in new[]{2.0,2400.0/1240.0,2557.0/1500.0,0.4})
                {
                    var chrome=new Size(104,28);
                    Rectangle bounds=PreviewFrameSizing.Fit(available,ratio,chrome,8);
                    Check(bounds.X>=0&&bounds.Y>=0&&bounds.Right<=available.Width&&bounds.Bottom<=available.Height,
                        "Framed sheet/remnant and navigation stay inside the preview area");
                    int cw=bounds.Width-chrome.Width,ch=bounds.Height-chrome.Height;
                    if(cw>0&&ch>0)
                        Check(Math.Abs(cw-ch*ratio)<=Math.Max(1,ratio),"The viewport follows the image ratio, excluding tabs and navigation");
                }
            Rectangle screenshot=PreviewFrameSizing.Fit(new Size(760,230),2.0,new Size(104,28),8);
            Check(screenshot.Width<600&&screenshot.Height>200,"The supplied wide white panel becomes a tight landscape preview frame");
            Rectangle large=PreviewFrameSizing.Fit(new Size(1520,460),2.0,new Size(208,56),16);
            Check(Math.Abs(large.Width-screenshot.Width*2)<=2&&Math.Abs(large.Height-screenshot.Height*2)<=2,
                "Preview frame scales consistently at 200 percent DPI");
            Console.WriteLine($"PASS: {passed} screen-fit, sheet-ratio and DPI window-sizing checks.");
            return passed;
        }
    }
}
