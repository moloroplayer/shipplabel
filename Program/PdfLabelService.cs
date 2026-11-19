using PDFiumSharp;
using PDFiumSharp.Enums;
using PDFiumSharp.Types;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DHLabel
{

/// <summary>
/// Etikettenformat des PDFs. Der Wert wird als Einstellung "labelType" gespeichert.
/// </summary>
public enum LabelFormat
{
    /// <summary>Format anhand des PDF-Inhalts automatisch erkennen.</summary>
    Auto   = 0,
    /// <summary>DHL (Privat-, Retoure- oder Business-Label).</summary>
    Dhl    = 1,
    /// <summary>Hermes-Paketschein.</summary>
    Hermes = 2
}

/// <summary>
/// Rendert, beschneidet und skaliert ein PDF-Versandetikett
/// für die Druckausgabe oder Vorschau.
/// </summary>
public class PdfLabelService
{
    // ------------------------------------------------------------------
    // Konfiguration: alle Maßangaben in Millimetern
    // ------------------------------------------------------------------
    private static class CropSettings
    {
        // Ausschnitt aus der Original-PDF-Seite (Privat-/Retourenlabel, ~A4,
        // Inhalt liegt quer auf der Seite)
        public const double CropXStartMm     =   4.0;
        public const double CropYStartMm     =  23.0;
        public const double CropXEndMm       = 208.0;
        public const double CropYEndMm       = 125.0;

        // Vertikale Bereiche, die nach der 90°-Drehung entfernt werden
        public const double CutAStartMm      =  55.0;
        public const double CutAEndMm        =  60.0;
        public const double CutBStartMm      =  90.0;
        public const double CutBEndMm        = 120.0;

        // Zielhöhen je nach Papierformat
        public const double TargetHeightMm   = 148.0;   // A6 / Einzelblatt
        public const double EndlessHeightMm  = 158.0;   // Endlosetiketten
    }

    /// <summary>
    /// Ausschnitt für DHL-Business-Labels (Geschäftskundenportal). Diese PDFs
    /// sind ca. 105x203mm groß, der Inhalt steht bereits aufrecht und füllt
    /// die Seite fast vollständig – anders als bei Privat-/Retourenlabels
    /// ist daher weder eine Drehung noch das Entfernen von Streifen nötig,
    /// nur ein Wegschneiden des schmalen weißen Rands.
    /// </summary>
    private static class BusinessCropSettings
    {
        public const double CropXStartMm =   2.0;
        public const double CropYStartMm =   3.0;
        public const double CropXEndMm   = 103.0;
        public const double CropYEndMm   = 197.0;

        // Seiten, die schmaler als dieser Wert sind, gelten als Business-Format
        // (zur Unterscheidung von den ~210mm breiten Privat-/Retourenlabels).
        public const double MaxPageWidthMm = 150.0;
    }

    /// <summary>
    /// Ausschnitt für Hermes-Paketscheine (A4-PDF). Der eigentliche Paketschein
    /// liegt quer in der oberen Seitenhälfte (Barcode links, Adresse rechts);
    /// darunter folgen Abtrennlinie und Auftragsbestätigung, die nicht gedruckt
    /// werden sollen. Der Ausschnitt wird wie bei Privatlabels gedreht, es
    /// müssen aber keine Streifen entfernt werden.
    /// </summary>
    private static class HermesCropSettings
    {
        public const double CropXStartMm =  18.0;
        public const double CropYStartMm =  10.0;
        public const double CropXEndMm   = 190.0;
        public const double CropYEndMm   = 134.0;

        // Zielbreite nach der Drehung (100x150-mm-Etikett). Der gedrehte
        // Ausschnitt wird auf diese Breite eingepasst (statt wie bei DHL auf
        // die Höhe gestreckt), damit er nicht breiter als das Etikett wird.
        public const double TargetWidthMm = 100.0;

        // Innenrand rundum (mm). Die Kopfzeile liegt nach der Drehung direkt am
        // Bildrand; im Endlos-Modus wird das Bild zusätzlich um 2,5 mm nach rechts
        // und 2 mm nach oben geschoben (Form1) und der Drucker hat einen nicht
        // bedruckbaren Rand. Mit 5,5 mm bleiben rechts mindestens 3 mm Abstand
        // zur Etikettenkante (5,5 - 2,5), oben 3,5 mm (5,5 - 2,0).
        public const double InnerMarginMm = 5.5;

        // Text, an dem ein Hermes-PDF automatisch erkannt wird.
        public const string DetectionText = "Hermes";
    }

    // ------------------------------------------------------------------
    // Öffentliche API
    // ------------------------------------------------------------------

    /// <summary>
    /// Rendert Seite 0 des angegebenen PDFs und liefert eine zugeschnittene,
    /// gedrehte und skalierte Bitmap zurück.
    /// Der Aufrufer ist für das Dispose der zurückgegebenen Bitmap verantwortlich.
    /// </summary>
    /// <param name="pdfPath">Pfad zur PDF-Datei.</param>
    /// <param name="dpi">Ziel-Auflösung in DPI (z. B. 203 oder 300).</param>
    /// <param name="scale">Optionaler Skalierungsfaktor (Standard 1,0).</param>
    public Bitmap RenderPdf(string pdfPath, int dpi, float scale = 1.0f)
    {
        bool endless = DHLabel.Properties.Settings.Default.endless;

        using (var document = new PdfDocument(pdfPath))
        {
            var page = document.Pages[0];

            double pageWidthMm  = page.Size.Width  / 72.0 * 25.4;
            double pageHeightMm = page.Size.Height / 72.0 * 25.4;

            int fullWidthPx  = MmToPx(pageWidthMm,  dpi);
            int fullHeightPx = MmToPx(pageHeightMm, dpi);

            LabelFormat format = (LabelFormat)DHLabel.Properties.Settings.Default.labelType;
            bool isHermes = format == LabelFormat.Hermes
                || (format == LabelFormat.Auto && IsHermesPdf(page));

            // Bei Hermes ist das Seitenformat A4 - die Business-Erkennung darf nie greifen.
            bool isBusinessFormat = !isHermes
                && pageWidthMm < BusinessCropSettings.MaxPageWidthMm;

            // hasAlpha: false, da sonst AsBmpStream() einen BITMAPV4HEADER mit
            // Alpha-Maske schreibt, den System.Drawing.Bitmap(Stream)/GDI+ nicht
            // zuverlässig decodieren kann (führt zu "Nicht genügend Arbeitsspeicher").
            // Transparenz wird hier ohnehin nicht benötigt – der Hintergrund wird
            // direkt im Anschluss deckend weiß gefüllt.
            using (var pdfBitmap = new PDFiumBitmap(fullWidthPx, fullHeightPx, hasAlpha: false))
            {
                pdfBitmap.FillRectangle(0, 0, fullWidthPx, fullHeightPx, 0xFFFFFFFF);
                page.Render(pdfBitmap, PageOrientations.Normal, RenderingFlags.Annotations);

                using (var stream    = pdfBitmap.AsBmpStream())
                using (var fullImage = new Bitmap(stream))
                {
                    fullImage.SetResolution(dpi, dpi);

                    if (isHermes)
                    {
                        // Hermes-Paketschein: Ausschnitt → drehen → auf 100 mm Breite einpassen.
                        using (var cropped = CropImage(fullImage, dpi,
                            HermesCropSettings.CropXStartMm, HermesCropSettings.CropYStartMm,
                            HermesCropSettings.CropXEndMm,   HermesCropSettings.CropYEndMm))
                        {
                            cropped.RotateFlip(RotateFlipType.Rotate90FlipNone);
                            return ScaleToFit(cropped, dpi, scale, endless,
                                HermesCropSettings.TargetWidthMm,
                                HermesCropSettings.InnerMarginMm);
                        }
                    }

                    if (isBusinessFormat)
                    {
                        // Business-Label: Seite steht bereits aufrecht, nur Rand wegschneiden.
                        using (var cropped = CropImage(fullImage, dpi,
                            BusinessCropSettings.CropXStartMm, BusinessCropSettings.CropYStartMm,
                            BusinessCropSettings.CropXEndMm,   BusinessCropSettings.CropYEndMm))
                        {
                            return ScaleToTargetHeight(cropped, dpi, scale, endless);
                        }
                    }

                    // Privat-/Retourenlabel: Ausschnitt → drehen → Streifen entfernen → skalieren.
                    using (var cropped = CropImage(fullImage, dpi,
                        CropSettings.CropXStartMm, CropSettings.CropYStartMm,
                        CropSettings.CropXEndMm,   CropSettings.CropYEndMm))
                    {
                        cropped.RotateFlip(RotateFlipType.Rotate90FlipNone);

                        using (var assembled = RemoveStrips(cropped, dpi))
                        {
                            return ScaleToTargetHeight(assembled, dpi, scale, endless);
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Private Hilfsmethoden
    // ------------------------------------------------------------------

    /// <summary>
    /// Prüft per PDFium-Textextraktion, ob die Seite ein Hermes-Paketschein ist
    /// (Text "Hermes" kommt auf der Seite vor). Bei jedem Fehler gilt: kein Hermes,
    /// damit das DHL-Verhalten unverändert bleibt.
    /// </summary>
    private static bool IsHermesPdf(PdfPage page)
    {
        try
        {
            FPDF_TEXTPAGE textPage = PDFium.FPDFText_LoadPage(page.Handle);
            if (textPage.IsNull)
                return false;

            try
            {
                int count = PDFium.FPDFText_CountChars(textPage);
                if (count <= 0)
                    return false;

                string text = PDFium.FPDFText_GetText(textPage, 0, count);
                return text != null && text.IndexOf(
                    HermesCropSettings.DetectionText,
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            finally
            {
                PDFium.FPDFText_ClosePage(textPage);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Schneidet den angegebenen Bereich (in mm) aus dem gerenderten Vollbild aus.</summary>
    private static Bitmap CropImage(Bitmap source, int dpi,
        double xStartMm, double yStartMm, double xEndMm, double yEndMm)
    {
        int xStart     = MmToPx(xStartMm, dpi);
        int yStart     = MmToPx(yStartMm, dpi);
        int cropWidth  = MmToPx(xEndMm - xStartMm, dpi);
        int cropHeight = MmToPx(yEndMm - yStartMm, dpi);

        // Auf die tatsächliche Bildgröße begrenzen: Bitmap.Clone() wirft bei
        // einem Rechteck außerhalb der Quellbild-Grenzen GDI+'s generisches
        // "Nicht genügend Arbeitsspeicher", z.B. wenn eine PDF-Seite ein
        // unerwartetes Format hat. Lieber ein unvollständiger Ausschnitt als
        // ein Absturz.
        xStart     = Math.Min(xStart, Math.Max(0, source.Width - 1));
        yStart     = Math.Min(yStart, Math.Max(0, source.Height - 1));
        cropWidth  = Math.Max(1, Math.Min(cropWidth,  source.Width  - xStart));
        cropHeight = Math.Max(1, Math.Min(cropHeight, source.Height - yStart));

        var rect = new Rectangle(xStart, yStart, cropWidth, cropHeight);
        return source.Clone(rect, source.PixelFormat);
    }

    /// <summary>
    /// Entfernt zwei horizontale Streifen aus dem (bereits gedrehten) Bild
    /// und setzt die verbleibenden Blöcke nahtlos zusammen.
    /// </summary>
    private static Bitmap RemoveStrips(Bitmap source, int dpi)
    {
        int cutA_start = MmToPx(CropSettings.CutAStartMm, dpi);
        int cutA_end   = MmToPx(CropSettings.CutAEndMm,   dpi);
        int cutB_start = MmToPx(CropSettings.CutBStartMm, dpi);
        int cutB_end   = MmToPx(CropSettings.CutBEndMm,   dpi);

        int h1 = cutA_start;                    // Block 1: 0 → CutA-Start
        int h2 = cutB_start - cutA_end;         // Block 2: CutA-Ende → CutB-Start
        int h3 = source.Height - cutB_end;      // Block 3: CutB-Ende → Bildende

        int newHeight = h1 + h2 + h3;

        Bitmap result = new Bitmap(source.Width, newHeight);
        result.SetResolution(dpi, dpi);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.Clear(Color.White);
            int destY = 0;

            DrawStrip(g, source, destY, 0,        h1, source.Width); destY += h1;
            DrawStrip(g, source, destY, cutA_end, h2, source.Width); destY += h2;
            DrawStrip(g, source, destY, cutB_end, h3, source.Width);
        }

        return result;
    }

    /// <summary>Kopiert einen horizontalen Streifen von <paramref name="src"/> nach <paramref name="g"/>.</summary>
    private static void DrawStrip(Graphics g, Bitmap src, int destY, int srcY, int height, int width)
    {
        if (height <= 0) return;

        g.DrawImage(
            src,
            new Rectangle(0, destY, width, height),
            new Rectangle(0, srcY,  width, height),
            GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Skaliert die Bitmap proportional auf die konfigurierte Zielhöhe.
    /// Der Aufrufer erhält eine neue Bitmap und ist für deren Dispose verantwortlich.
    /// </summary>
    private static Bitmap ScaleToTargetHeight(Bitmap source, int dpi, float scale, bool endless)
    {
        double targetHeightMm = endless
            ? CropSettings.EndlessHeightMm
            : CropSettings.TargetHeightMm;

        int targetHeightPx  = (int)Math.Round(targetHeightMm / 25.4 * dpi * scale);
        double aspectRatio  = (double)source.Width / source.Height;
        int targetWidthPx   = (int)Math.Round(targetHeightPx * aspectRatio);

        Bitmap final = new Bitmap(targetWidthPx, targetHeightPx);
        try
        {
            final.SetResolution(dpi, dpi);

            using (Graphics g = Graphics.FromImage(final))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, new Rectangle(0, 0, targetWidthPx, targetHeightPx));
            }

            return final;
        }
        catch
        {
            final.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Passt die Bitmap proportional in ein Feld aus Zielbreite und konfigurierter
    /// Zielhöhe ein (oben ausgerichtet, horizontal zentriert, Rest weiß).
    /// Rundum bleibt der Innenrand <paramref name="marginMm"/> frei, damit nichts
    /// am Etikettenrand abgeschnitten wird.
    /// Der Aufrufer erhält eine neue Bitmap und ist für deren Dispose verantwortlich.
    /// </summary>
    private static Bitmap ScaleToFit(Bitmap source, int dpi, float scale, bool endless, double targetWidthMm, double marginMm)
    {
        double targetHeightMm = endless
            ? CropSettings.EndlessHeightMm
            : CropSettings.TargetHeightMm;

        int canvasWidthPx  = (int)Math.Round(targetWidthMm  / 25.4 * dpi * scale);
        int canvasHeightPx = (int)Math.Round(targetHeightMm / 25.4 * dpi * scale);

        int marginPx = (int)Math.Round(marginMm / 25.4 * dpi * scale);

        double factor = Math.Min(
            (double)(canvasWidthPx  - 2 * marginPx) / source.Width,
            (double)(canvasHeightPx - 2 * marginPx) / source.Height);
        int drawWidthPx  = (int)Math.Round(source.Width  * factor);
        int drawHeightPx = (int)Math.Round(source.Height * factor);

        Bitmap final = new Bitmap(canvasWidthPx, canvasHeightPx);
        try
        {
            final.SetResolution(dpi, dpi);

            using (Graphics g = Graphics.FromImage(final))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, new Rectangle(
                    (canvasWidthPx - drawWidthPx) / 2, marginPx, drawWidthPx, drawHeightPx));
            }

            return final;
        }
        catch
        {
            final.Dispose();
            throw;
        }
    }

    /// <summary>Rechnet Millimeter in Pixel um (gerundet).</summary>
    private static int MmToPx(double mm, int dpi)
        => (int)Math.Round(mm / 25.4 * dpi);
}

} // namespace DHLabel
