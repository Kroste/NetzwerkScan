using System;
using Avalonia.Controls;

namespace NetScanner.Views;

/// <summary>
/// Passt Fenstergroessen an den tatsaechlich verfuegbaren Bildschirmbereich an,
/// damit Dialoge nie groesser als der Schirm sind (wichtig auf kleinen Displays,
/// z. B. Mini-PCs oder Touchscreens). Zusammen mit einem ScrollViewer im Inhalt
/// kann dadurch nichts mehr abgeschnitten werden.
/// </summary>
public static class WindowSizing
{
    /// <summary>Begrenzt Breite/Hoehe auf einen Anteil des Arbeitsbereichs (ohne Taskleiste)
    /// und zentriert neu. Best-effort — schlaegt nie fehl.</summary>
    public static void FitToScreen(Window w, double fraction = 0.92)
    {
        try
        {
            var screen = w.Screens.ScreenFromVisual(w) ?? w.Screens.Primary;
            if (screen is null) return;

            double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            // WorkingArea ist in physischen Pixeln -> in DIPs umrechnen (Avalonia-Maßeinheit).
            double availW = screen.WorkingArea.Width / scale;
            double availH = screen.WorkingArea.Height / scale;

            double maxW = availW * fraction;
            double maxH = availH * fraction;

            bool changed = false;
            if (w.Width > maxW) { w.Width = Math.Max(w.MinWidth, maxW); changed = true; }
            if (w.Height > maxH) { w.Height = Math.Max(w.MinHeight, maxH); changed = true; }

            // Nach dem Verkleinern neu mittig setzen, damit nichts off-screen rutscht.
            if (changed)
            {
                double x = screen.WorkingArea.X / scale + (availW - w.Width) / 2;
                double y = screen.WorkingArea.Y / scale + (availH - w.Height) / 2;
                w.Position = new Avalonia.PixelPoint(
                    (int)(x * scale), (int)(y * scale));
            }
        }
        catch { /* Sizing ist best-effort, Layout darf nie crashen */ }
    }
}
