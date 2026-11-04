// Renders every key layout to PNG files so they can be reviewed without a device.
//   mcs -langversion:5 -main:AiUsageDeck.RenderGallery ... src/*.cs test/RenderGallery.cs
//   mono gallery.exe <output folder>     (also writes gallery.png and gallery-actual-size.png)

using System;
using System.IO;

namespace AiUsageDeck
{
    internal static class RenderGallery
    {
        private static void Save(string folder, string name, string dataUrl)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".png"),
                               Convert.FromBase64String(dataUrl.Substring(dataUrl.IndexOf(',') + 1)));
        }

        private static Limit L(string name, double pct, TimeSpan reset)
        {
            var l = new Limit();
            l.Name = name; l.Percent = pct; l.ResetsAt = DateTime.UtcNow + reset;
            return l;
        }

        private static ExtraUsage X(string value, string sub, double? pct)
        {
            var x = new ExtraUsage();
            x.Enabled = true; x.Value = value; x.Sub = sub; x.Percent = pct;
            return x;
        }

        /// <summary>All tiles side by side, scaled to the given key size.</summary>
        private static void Sheet(string dir, int key, string name)
        {
            string[] files = Directory.GetFiles(dir, "??-*.png");
            Array.Sort(files, StringComparer.Ordinal);
            const int cols = 8, pad = 10;
            int rows = (files.Length + cols - 1) / cols;
            using (var sheet = new System.Drawing.Bitmap(cols * (key + pad) + pad, rows * (key + pad) + pad))
            using (var g = System.Drawing.Graphics.FromImage(sheet))
            {
                g.Clear(System.Drawing.Color.FromArgb(40, 40, 40));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                for (int i = 0; i < files.Length; i++)
                    using (var tile = System.Drawing.Image.FromFile(files[i]))
                        g.DrawImage(tile, pad + (i % cols) * (key + pad), pad + (i / cols) * (key + pad), key, key);
                sheet.Save(Path.Combine(dir, name), System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : "gallery";
            Directory.CreateDirectory(dir);
            DateTime? r5 = DateTime.UtcNow + new TimeSpan(2, 13, 30);
            DateTime? r7 = DateTime.UtcNow + new TimeSpan(3, 4, 30, 0);

            Save(dir, "01-claude-5h", KeyRenderer.Meter("CLAUDE 5H", 37, r5, "fill", true, false, 60, 85));
            Save(dir, "02-claude-wk", KeyRenderer.Meter("CLAUDE WK", 71, r7, "fill", true, false, 60, 85));
            Save(dir, "03-gpt-5h", KeyRenderer.Meter("GPT 5H", 22, r5, "fill", true, false, 60, 85));
            Save(dir, "04-agy-gemini", KeyRenderer.Meter("GEMINI 5H", 92, r5, "fill", true, false, 60, 85));
            Save(dir, "05-number", KeyRenderer.Meter("GPT WK", 64, r7, "number", true, false, 60, 85));
            Save(dir, "06-bar", KeyRenderer.Meter("CLAUDE WK", 71, r7, "bar", true, false, 60, 85));
            Save(dir, "07-ring", KeyRenderer.Meter("OPUS", 92, r7, "ring", true, false, 60, 85));
            Save(dir, "08-overview", KeyRenderer.Overview("GPT", L("Session", 22, new TimeSpan(2, 0, 0)),
                                                          L("Weekly", 64, new TimeSpan(3, 0, 0, 0)), false, 60, 85));
            Save(dir, "09-extra", KeyRenderer.Extra("EXTRA", X("$12.34", "of $50", 24.7), false, 60, 85));
            Save(dir, "10-credits", KeyRenderer.Extra("CREDITS", X("480", "of 500", 4), false, 60, 85));
            Save(dir, "11-stale", KeyRenderer.Meter("CLAUDE 5H", 12, DateTime.UtcNow.AddMinutes(37), "fill", true, true, 60, 85));
            Save(dir, "12-noreset", KeyRenderer.Meter("SONNET", 4, null, "fill", false, false, 60, 85));
            Save(dir, "13-signin", KeyRenderer.Message("GPT 5H", "SIGN\nIN", KeyRenderer.Accent));
            Save(dir, "14-notrunning", KeyRenderer.Message("AGY 5H", "NOT\nRUNNING", KeyRenderer.Accent));
            Save(dir, "15-expired", KeyRenderer.Message("CLAUDE 5H", "LOGIN\nEXPIRED", KeyRenderer.Accent));
            Save(dir, "16-na", KeyRenderer.Message("HAIKU", "N/A", System.Drawing.Color.Gray));
            Sheet(dir, 144, "gallery.png");
            Sheet(dir, 100, "gallery-actual-size.png"); // about what the SS-550 shows
            return 0;
        }
    }
}
