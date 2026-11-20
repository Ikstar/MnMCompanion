namespace MnMCompanion.Gear;

/// <summary>Raw 24-bit RGB pixel buffer (3 bytes per pixel, R-G-B order, top-down).</summary>
public sealed class RgbImage(int width, int height, byte[] rgb)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgb { get; } = rgb;
}

/// <summary>Preprocessed 8-bit grayscale image ready for OCR (0 = black ink, 255 = white paper).</summary>
public sealed class GrayImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>Location and uniform scale of the 21-slot equipped paperdoll grid inside a screenshot.</summary>
public sealed record DetectedPaperdoll(double XLeft, double YTop, double Pitch, double Score, bool HasPersonaColumn);

/// <summary>Extracted grayscale OCR patches for the Persona column on the left side of the Inventory window.</summary>
public sealed record PersonaPatches(
    GrayImage? Identity,
    GrayImage? Vitals,
    GrayImage? StatsLeft,
    GrayImage? StatsRight,
    GrayImage? ResistsLeft,
    GrayImage? ResistsRight,
    GrayImage? Devotion);

/// <summary>Result of locating the paperdoll and extracting resolution-normalized OCR patches from a screenshot.</summary>
public sealed record DetectedSheet(
    bool Found,
    DetectedPaperdoll? Paperdoll,
    IReadOnlyDictionary<GearSlot, GrayImage> SlotPatches,
    PersonaPatches? Persona,
    int NonEmptyVisualSlots);

/// <summary>
/// Resolution- and position-invariant detector for the Monsters &amp; Memories Inventory/Persona paperdoll.
/// Locates the 21-slot equipment grid across arbitrary screen resolutions or window crops using a coarse-to-fine
/// integral-image search parameterized solely by anchor <c>(XLeft, YTop)</c> and slot pitch <c>P</c>, then isolates
/// each slot's bottom text plate and Persona regions and upscales them with Catmull-Rom bicubic interpolation.
/// </summary>
public static class CharacterSheetDetector
{
    private const double MinPitch = 18.0;
    private const double MinScoreThreshold = 22.0;
    private const int MinMatchedSlots = 15;
    private const double TargetSlotWidthForOcr = 260.0;
    private const int QuietPad = 24;

    private readonly record struct Candidate(double Score, double X, double Y, double P, int Matched);

    private sealed class IntegralMaps
    {
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public long[] Lum { get; }
        public long[] HorzEdge { get; }
        public long[] VertEdge { get; }

        public IntegralMaps(int width, int height, byte[] luma)
        {
            Width = width;
            Height = height;
            Stride = width + 1;
            int len = (width + 1) * (height + 1);
            Lum = new long[len];
            HorzEdge = new long[len];
            VertEdge = new long[len];

            for (int y = 0; y < height; y++)
            {
                long rowL = 0, rowH = 0, rowV = 0;
                int rowBase = y * width;
                int prevY = Math.Max(0, y - 1) * width;
                int nextY = Math.Min(height - 1, y + 1) * width;
                int pCur = (y + 1) * Stride;
                int pPrev = y * Stride;

                for (int x = 0; x < width; x++)
                {
                    int v = luma[rowBase + x];
                    int dy = Math.Abs(luma[nextY + x] - luma[prevY + x]);
                    int dx = Math.Abs(luma[rowBase + Math.Min(width - 1, x + 1)] - luma[rowBase + Math.Max(0, x - 1)]);
                    rowL += v;
                    rowH += dy;
                    rowV += dx;
                    Lum[pCur + x + 1] = Lum[pPrev + x + 1] + rowL;
                    HorzEdge[pCur + x + 1] = HorzEdge[pPrev + x + 1] + rowH;
                    VertEdge[pCur + x + 1] = VertEdge[pPrev + x + 1] + rowV;
                }
            }
        }

        public double RectMean(long[] map, int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, Width);
            x1 = Math.Clamp(x1, 0, Width);
            y0 = Math.Clamp(y0, 0, Height);
            y1 = Math.Clamp(y1, 0, Height);
            if (x1 <= x0 || y1 <= y0) return 0.0;
            long sum = map[y1 * Stride + x1] - map[y0 * Stride + x1] - map[y1 * Stride + x0] + map[y0 * Stride + x0];
            return (double)sum / ((x1 - x0) * (y1 - y0));
        }

        public double EdgePerLength(long[] map, int x0, int y0, int x1, int y1, bool verticalEdge)
        {
            x0 = Math.Clamp(x0, 0, Width);
            x1 = Math.Clamp(x1, 0, Width);
            y0 = Math.Clamp(y0, 0, Height);
            y1 = Math.Clamp(y1, 0, Height);
            if (x1 <= x0 || y1 <= y0) return 0.0;
            long sum = map[y1 * Stride + x1] - map[y0 * Stride + x1] - map[y1 * Stride + x0] + map[y0 * Stride + x0];
            int length = verticalEdge ? (y1 - y0) : (x1 - x0);
            return (double)sum / Math.Max(1, length);
        }
    }

    public static DetectedSheet Detect(RgbImage image)
    {
        if (image.Width < 120 || image.Height < 120 || image.Rgb.Length < image.Width * image.Height * 3)
            return new DetectedSheet(false, null, new Dictionary<GearSlot, GrayImage>(), null, 0);

        var paperdoll = FindPaperdoll(image);
        if (paperdoll is null)
            return new DetectedSheet(false, null, new Dictionary<GearSlot, GrayImage>(), null, 0);

        var slotPatches = new Dictionary<GearSlot, GrayImage>();
        int visualNonEmpty = 0;

        foreach (var spec in GearSlots.All)
        {
            var (patch, visualActive) = ExtractSlotPatch(image, paperdoll, spec);
            if (visualActive) visualNonEmpty++;
            if (patch is not null) slotPatches[spec.Slot] = patch;
        }

        PersonaPatches? persona = paperdoll.HasPersonaColumn
            ? ExtractPersonaPatches(image, paperdoll)
            : null;

        return new DetectedSheet(true, paperdoll, slotPatches, persona, visualNonEmpty);
    }

    private static DetectedPaperdoll? FindPaperdoll(RgbImage image)
    {
        int w = image.Width, h = image.Height;
        var fullLuma = ComputeLuma(image);

        int ds = Math.Max(1, Math.Min(4, (int)Math.Round(Math.Max(w, h) / 720.0)));
        int cw = w / ds, ch = h / ds;
        if (cw < 80 || ch < 80) return null;

        var coarseLuma = ds == 1 ? fullLuma : DownsampleLuma(fullLuma, w, h, ds, cw, ch);
        var coarseMaps = new IntegralMaps(cw, ch, coarseLuma);

        double pMaxCoarse = Math.Min((cw - 2) / 6.96, (ch - 2) / 6.99);
        double pMinCoarse = Math.Max(8.0, MinPitch / ds);
        if (pMaxCoarse < pMinCoarse) return null;

        var coarseCandidates = new List<Candidate>();
        for (double p = pMinCoarse; p <= pMaxCoarse + 0.01; p = Math.Min(pMaxCoarse + 0.02, p * 1.06 + 0.25))
        {
            double pUse = Math.Min(p, pMaxCoarse);
            int gridW = (int)Math.Floor(6.96 * pUse);
            int gridH = (int)Math.Floor(6.99 * pUse);
            int step = Math.Max(1, (int)Math.Round(pUse * 0.12));
            int maxY = Math.Max(0, ch - gridH);
            int maxX = Math.Max(0, cw - gridW);

            var scaleBest = new List<Candidate>();
            for (int y0 = 0; y0 <= maxY; y0 += step)
            {
                for (int xl = 0; xl <= maxX; xl += step)
                {
                    var cand = ScoreGrid(coarseMaps, xl, y0, pUse, isCoarse: true);
                    if (cand.Score > 12.0) InsertTopCandidate(scaleBest, cand, pUse, 3);
                }
            }
            coarseCandidates.AddRange(scaleBest);
            if (pUse >= pMaxCoarse) break;
        }

        if (coarseCandidates.Count == 0) return null;

        var fullMaps = ds == 1 ? coarseMaps : new IntegralMaps(w, h, fullLuma);
        double pMaxFull = Math.Min((w - 2) / 6.96, (h - 2) / 6.99);
        var level1Top = new List<Candidate>();

        foreach (var c in coarseCandidates)
        {
            double pCenter = Math.Clamp(c.P * ds, MinPitch, pMaxFull);
            double xCenter = c.X * ds;
            double yCenter = c.Y * ds;

            // Level 1: medium search around mapped coarse candidate
            double dpSpan = Math.Max(2.5, pCenter * 0.10);
            double dpStep = Math.Max(0.6, dpSpan / 4.0);
            int dxySpan = Math.Max(3, (int)Math.Ceiling(ds * 2.2));
            int dxyStep = Math.Max(1, ds / 2);

            Candidate localBest = new(double.NegativeInfinity, xCenter, yCenter, pCenter, 0);
            for (double p = Math.Max(MinPitch, pCenter - dpSpan); p <= Math.Min(pMaxFull, pCenter + dpSpan); p += dpStep)
            {
                for (int dy = -dxySpan; dy <= dxySpan; dy += dxyStep)
                {
                    for (int dx = -dxySpan; dx <= dxySpan; dx += dxyStep)
                    {
                        var sc = ScoreGrid(fullMaps, xCenter + dx, yCenter + dy, p, isCoarse: false);
                        if (sc.Score > localBest.Score) localBest = sc;
                    }
                }
            }

            if (localBest.Score > 10.0)
                InsertTopCandidate(level1Top, localBest, localBest.P, 6);
        }

        // Level 2: tight fine sub-pixel pitch + 1px anchor refinement on top full-res candidates
        Candidate best = new(double.NegativeInfinity, 0, 0, 0, 0);
        foreach (var c in level1Top)
        {
            for (double p = Math.Max(MinPitch, c.P - 1.5); p <= Math.Min(pMaxFull, c.P + 1.5); p += 0.2)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        var sc = ScoreGrid(fullMaps, c.X + dx, c.Y + dy, p, isCoarse: false, tight: true);
                        if (sc.Score > best.Score) best = sc;
                    }
                }
            }
        }

        if (best.Score < MinScoreThreshold || best.Matched < MinMatchedSlots) return null;

        bool hasPersona = best.X - 3.18 * best.P >= 0 && best.Y + 6.9 * best.P <= h;
        return new DetectedPaperdoll(best.X, best.Y, best.P, best.Score, hasPersona);
    }

    private static void InsertTopCandidate(List<Candidate> list, Candidate cand, double p, int maxCount)
    {
        double distTol = Math.Max(3.0, p * 0.7);
        for (int i = 0; i < list.Count; i++)
        {
            if (Math.Abs(list[i].X - cand.X) <= distTol &&
                Math.Abs(list[i].Y - cand.Y) <= distTol &&
                Math.Abs(list[i].P - cand.P) <= p * 0.2)
            {
                if (cand.Score > list[i].Score) list[i] = cand;
                return;
            }
        }
        list.Add(cand);
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (list.Count > maxCount) list.RemoveRange(maxCount, list.Count - maxCount);
    }

    private static Candidate ScoreGrid(IntegralMaps maps, double xl, double y0, double p, bool isCoarse, bool tight = false)
    {
        int sw = (int)Math.Round(0.94 * p);
        int sh = (int)Math.Round(0.94 * p);
        if (sw < 8 || sh < 8) return new Candidate(double.NegativeInfinity, xl, y0, p, 0);
        if (xl < -2 || y0 < -2 || xl + 6.96 * p > maps.Width + 2 || y0 + 6.99 * p > maps.Height + 2)
            return new Candidate(double.NegativeInfinity, xl, y0, p, 0);

        int b = tight
            ? Math.Max(1, (int)Math.Round(0.03 * p))
            : Math.Max(1, (int)Math.Round(0.055 * p));
        int inset = Math.Max(1, (int)Math.Round(0.06 * p));
        int cornerInset = Math.Max(1, (int)Math.Round(0.045 * p));
        int cw = Math.Max(2, (int)Math.Round(0.22 * sw));
        int ch = Math.Max(2, (int)Math.Round(0.24 * sh));
        int half = Math.Max(4, sh / 2);
        int borderBand = tight ? Math.Max(0, (int)Math.Round(0.015 * p)) : Math.Max(1, (int)Math.Round(0.025 * p));
        double total = 0.0;
        int matched = 0;

        foreach (var spec in GearSlots.All)
        {
            int x0 = (int)Math.Round(xl + spec.RelX * p);
            int yt = (int)Math.Round(y0 + spec.RelY * p);
            int x1 = x0 + sw;
            int ym = yt + half;
            int yb = yt + sh;

            // Sample all four slot borders (upper & lower vertical halves + top & bottom horizontal edges), capped at 55.
            double vLeftTop = Math.Min(55.0, maps.EdgePerLength(maps.VertEdge, x0 - b, yt + inset, x0 + b + 1, ym, verticalEdge: true));
            double vRightTop = Math.Min(55.0, maps.EdgePerLength(maps.VertEdge, x1 - 1 - b, yt + inset, x1 + b, ym, verticalEdge: true));
            double vLeftBot = Math.Min(55.0, maps.EdgePerLength(maps.VertEdge, x0 - b, ym, x0 + b + 1, yb - inset, verticalEdge: true));
            double vRightBot = Math.Min(55.0, maps.EdgePerLength(maps.VertEdge, x1 - 1 - b, ym, x1 + b, yb - inset, verticalEdge: true));
            double hTop = Math.Min(55.0, maps.EdgePerLength(maps.HorzEdge, x0 + inset, yt - b, x1 - inset, yt + b + 1, verticalEdge: false));
            double hBot = Math.Min(55.0, maps.EdgePerLength(maps.HorzEdge, x0 + inset, yb - 1 - b, x1 - inset, yb + b, verticalEdge: false));

            // Sample bevels and recessed stone corners in the upper half (never covered by bottom item name banners).
            double interior = maps.RectMean(maps.Lum, x0 + cornerInset, yt + cornerInset, x1 - cornerInset, ym);
            double topBorderLum = maps.RectMean(maps.Lum, x0 + inset, yt - borderBand, x1 - inset, yt + borderBand + 1);
            double leftBorderLum = maps.RectMean(maps.Lum, x0 - borderBand, yt + inset, x0 + borderBand + 1, ym);
            double rightBorderLum = maps.RectMean(maps.Lum, x1 - 1 - borderBand, yt + inset, x1 + borderBand, ym);

            double avgBevel = (topBorderLum + leftBorderLum + rightBorderLum) / 3.0;
            double ulCorner = maps.RectMean(maps.Lum, x0 + cornerInset, yt + cornerInset, x0 + cornerInset + cw, yt + cornerInset + ch);
            double urCorner = maps.RectMean(maps.Lum, x1 - cornerInset - cw, yt + cornerInset, x1 - cornerInset, yt + cornerInset + ch);
            double cornerLum = 0.5 * (ulCorner + urCorner);
            double recess = avgBevel - cornerLum;

            bool validBevels = isCoarse
                ? avgBevel is >= 45.0 and <= 155.0
                : avgBevel is >= 56.0 and <= 155.0 &&
                  topBorderLum >= 36.0 &&
                  leftBorderLum >= 48.0 &&
                  rightBorderLum >= 48.0;

            double minCorner = isCoarse ? 22.0 : 28.0;
            double maxCorner = isCoarse ? 80.0 : 72.0;
            double minRecess = isCoarse ? 4.0 : 10.0;
            bool validSlot = interior is >= 20.0 and <= 155.0 &&
                             cornerLum >= minCorner && cornerLum <= maxCorner &&
                             validBevels &&
                             recess >= minRecess;

            double minTop = Math.Min(Math.Min(vLeftTop, vRightTop), hTop);
            double minBot = Math.Min(Math.Min(vLeftBot, vRightBot), hBot);
            double meanEdges = (vLeftTop + vRightTop + vLeftBot + vRightBot + 1.5 * hTop + 1.5 * hBot) / 7.0;
            double edgeScore = 0.45 * meanEdges + 0.30 * minTop + 0.25 * minBot;

            if (!validSlot)
            {
                total -= 40.0;
            }
            else
            {
                if (minTop >= 12.0 && minBot >= 10.0)
                    matched++;
                total += 0.20 * Math.Clamp(recess, 0.0, 32.0);
                if (tight)
                    total += 0.20 * Math.Clamp(recess, 0.0, 32.0);
            }

            total += edgeScore;
        }

        // Penalize uniform bright backgrounds inside the central portrait area.
        double centerMean = maps.RectMean(maps.Lum,
            (int)Math.Round(xl + 1.5 * p),
            (int)Math.Round(y0 + 1.4 * p),
            (int)Math.Round(xl + 5.4 * p),
            (int)Math.Round(y0 + 5.2 * p));
        if (centerMean > 155.0) total -= 250.0;

        return new Candidate(total / GearSlots.All.Count, xl, y0, p, matched);
    }

    private static (GrayImage? Patch, bool VisualActive) ExtractSlotPatch(RgbImage image, DetectedPaperdoll pd, GearSlotSpec spec)
    {
        double p = pd.Pitch;
        int insetX = Math.Max(1, (int)Math.Round(0.035 * p));
        int insetTop = Math.Max(1, (int)Math.Round(0.045 * p));
        int insetBottom = Math.Max(1, (int)Math.Round(0.025 * p));

        int rawX0 = (int)Math.Round(pd.XLeft + spec.RelX * p);
        int rawYt = (int)Math.Round(pd.YTop + spec.RelY * p);
        int rawX1 = (int)Math.Round(pd.XLeft + (spec.RelX + 0.94) * p);
        int rawYb = (int)Math.Round(pd.YTop + (spec.RelY + 0.95) * p);

        int x0 = Math.Clamp(rawX0 + insetX, 0, image.Width - 1);
        int x1 = Math.Clamp(rawX1 - insetX, x0 + 4, image.Width);
        int yt = Math.Clamp(rawYt + insetTop, 0, image.Height - 1);
        int yb = Math.Clamp(rawYb - insetBottom, yt + 4, image.Height);

        int w = x1 - x0;
        int h = yb - yt;
        if (w < 8 || h < 8) return (null, false);

        // Measure visual variance in the upper portion of the slot to distinguish empty stone slots from icon-only items.
        bool visualActive = HasIconVariance(image, x0, yt, w, Math.Max(4, (int)Math.Round(h * 0.55)));

        int edgeW = Math.Max(1, (int)Math.Round(0.045 * p));
        var inBanner = new bool[h];
        var textRows = new List<int>(h);

        for (int r = 0; r < h; r++)
        {
            int y = yt + r;
            int leftMin = 255, rightMin = 255;
            for (int dx = 0; dx < edgeW && x0 + dx < x1; dx++)
            {
                int lv = MaxRgb(image, x0 + dx, y);
                if (lv < leftMin) leftMin = lv;
                int rv = MaxRgb(image, x1 - 1 - dx, y);
                if (rv < rightMin) rightMin = rv;
            }
            int edge = Math.Min(leftMin, rightMin);

            int rmin = 255, rmax = 0;
            for (int x = x0; x < x1; x++)
            {
                int v = MaxRgb(image, x, y);
                if (v < rmin) rmin = v;
                if (v > rmax) rmax = v;
            }

            bool isText = rmin <= 38 && rmax >= 128;
            if (isText && edge <= 50) textRows.Add(r);
            inBanner[r] = edge <= 40 || isText;
        }

        int minTextRows = Math.Max(2, (int)Math.Round(0.06 * p));
        if (textRows.Count < minTextRows || textRows[^1] < h / 2)
            return (null, visualActive);

        int botRow = textRows[^1];
        int topRow = botRow;
        int maxAllowedGap = Math.Max(1, (int)Math.Round(0.03 * p));
        int gap = 0;
        for (int r = botRow; r >= 0; r--)
        {
            if (inBanner[r])
            {
                topRow = r;
                gap = 0;
            }
            else
            {
                gap++;
                if (gap > maxAllowedGap) break;
            }
        }

        int padY = Math.Max(1, (int)Math.Round(0.03 * p));
        int cropTop = Math.Max(0, topRow - padY);
        int cropBot = Math.Min(h, botRow + padY + 1);
        if (cropBot - cropTop < 3) return (null, visualActive);

        double scale = Math.Clamp(TargetSlotWidthForOcr / w, 2.0, 10.0);
        var patch = UpscaleBanner(image, x0, yt + cropTop, w, cropBot - cropTop, scale);
        return (patch, true);
    }

    private static bool HasIconVariance(RgbImage image, int x0, int y0, int w, int h)
    {
        double sum = 0, sumSq = 0;
        int minV = 255, maxV = 0;
        int n = w * h;
        for (int y = y0; y < y0 + h; y++)
        {
            int idx = (y * image.Width + x0) * 3;
            for (int x = 0; x < w; x++, idx += 3)
            {
                int v = Math.Max(image.Rgb[idx], Math.Max(image.Rgb[idx + 1], image.Rgb[idx + 2]));
                sum += v;
                sumSq += v * v;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
        }
        double mean = sum / n;
        double variance = sumSq / n - mean * mean;
        return variance >= 324.0 || maxV - minV >= 78;
    }

    private static PersonaPatches ExtractPersonaPatches(RgbImage image, DetectedPaperdoll pd)
    {
        double xl = pd.XLeft, y0 = pd.YTop, p = pd.Pitch;
        double scale = Math.Clamp(TargetSlotWidthForOcr / (0.96 * p), 2.0, 10.0);

        GrayImage? CropPersona(double relX0, double relX1, double relY0, double relY1)
        {
            int x0 = Math.Clamp((int)Math.Round(xl + relX0 * p), 0, image.Width - 2);
            int x1 = Math.Clamp((int)Math.Round(xl + relX1 * p), x0 + 2, image.Width);
            int yt = Math.Clamp((int)Math.Round(y0 + relY0 * p), 0, image.Height - 2);
            int yb = Math.Clamp((int)Math.Round(y0 + relY1 * p), yt + 2, image.Height);
            if (yb - yt < Math.Max(4, (int)Math.Round(0.15 * p))) return null;
            return UpscalePersonaRegion(image, x0, yt, x1 - x0, yb - yt, scale);
        }

        var identity = CropPersona(-3.18, -0.92, 0.20, 1.66);
        var vitals = CropPersona(-3.18, -0.90, 5.65, 6.88);
        var statsLeft = y0 + 8.35 * p <= image.Height ? CropPersona(-3.16, -2.08, 7.38, 8.50) : null;
        var statsRight = y0 + 8.35 * p <= image.Height ? CropPersona(-2.08, -0.90, 7.38, 8.50) : null;
        var resistsLeft = y0 + 9.95 * p <= image.Height ? CropPersona(-3.16, -2.08, 8.95, 10.08) : null;
        var resistsRight = y0 + 9.95 * p <= image.Height ? CropPersona(-2.08, -0.90, 8.95, 10.08) : null;
        var devotion = y0 + 10.65 * p <= image.Height ? CropPersona(-3.16, -0.90, 10.38, 10.78) : null;

        return new PersonaPatches(identity, vitals, statsLeft, statsRight, resistsLeft, resistsRight, devotion);
    }

    private static GrayImage UpscaleBanner(RgbImage image, int x0, int y0, int w, int h, double scale)
    {
        var raw = new float[w * h];
        for (int r = 0; r < h; r++)
        {
            int idx = ((y0 + r) * image.Width + x0) * 3;
            int outRow = r * w;
            for (int c = 0; c < w; c++, idx += 3)
                raw[outRow + c] = Math.Max(image.Rgb[idx], Math.Max(image.Rgb[idx + 1], image.Rgb[idx + 2]));
        }

        var sorted = (float[])raw.Clone();
        Array.Sort(sorted);
        double bg = Percentile(sorted, 0.15);
        double fg = Math.Max(bg + 80.0, Math.Max(155.0, Percentile(sorted, 0.995)));
        double low = bg + 16.0;
        double span = Math.Max(40.0, fg - low);

        return BicubicToGray(raw, w, h, scale, low, span);
    }

    private static GrayImage UpscalePersonaRegion(RgbImage image, int x0, int y0, int w, int h, double scale)
    {
        var raw = new float[w * h];
        for (int r = 0; r < h; r++)
        {
            int idx = ((y0 + r) * image.Width + x0) * 3;
            int outRow = r * w;
            for (int c = 0; c < w; c++, idx += 3)
            {
                int rv = image.Rgb[idx], gv = image.Rgb[idx + 1], bv = image.Rgb[idx + 2];
                int mx = Math.Max(rv, Math.Max(gv, bv));
                int mn = Math.Min(rv, Math.Min(gv, bv));
                int chromaBoost = Math.Max(gv - Math.Min(rv, bv), rv - Math.Min(gv, bv));
                // Boost green/red buffed or debuffed numbers above neutral gray stone background (~85..100).
                float sig = Math.Min(255f, mx + 0.55f * Math.Max(0, chromaBoost - (mn > 95 ? 10 : 0)));
                raw[outRow + c] = sig;
            }
        }

        var sorted = (float[])raw.Clone();
        Array.Sort(sorted);
        double bg = Percentile(sorted, 0.45);
        double fg = Math.Max(bg + 65.0, Math.Max(150.0, Percentile(sorted, 0.992)));
        double low = bg + 16.0;
        double span = Math.Max(36.0, fg - low);

        return BicubicToGray(raw, w, h, scale, low, span);
    }

    private static GrayImage BicubicToGray(float[] src, int srcW, int srcH, double scale, double low, double span)
    {
        int innerW = Math.Max(2, (int)Math.Round(srcW * scale));
        int innerH = Math.Max(2, (int)Math.Round(srcH * scale));
        int outW = innerW + QuietPad * 2;
        int outH = innerH + QuietPad * 2;
        var pixels = new byte[outW * outH];
        Array.Fill(pixels, (byte)255);

        for (int oy = 0; oy < innerH; oy++)
        {
            double sy = (oy + 0.5) / scale - 0.5;
            int iy = (int)Math.Floor(sy);
            double fy = sy - iy;
            double wy0 = CubicWeight(1 + fy);
            double wy1 = CubicWeight(fy);
            double wy2 = CubicWeight(1 - fy);
            double wy3 = CubicWeight(2 - fy);

            int py0 = Math.Clamp(iy - 1, 0, srcH - 1) * srcW;
            int py1 = Math.Clamp(iy, 0, srcH - 1) * srcW;
            int py2 = Math.Clamp(iy + 1, 0, srcH - 1) * srcW;
            int py3 = Math.Clamp(iy + 2, 0, srcH - 1) * srcW;

            int dstRow = (oy + QuietPad) * outW + QuietPad;
            for (int ox = 0; ox < innerW; ox++)
            {
                double sx = (ox + 0.5) / scale - 0.5;
                int ix = (int)Math.Floor(sx);
                double fx = sx - ix;
                double wx0 = CubicWeight(1 + fx);
                double wx1 = CubicWeight(fx);
                double wx2 = CubicWeight(1 - fx);
                double wx3 = CubicWeight(2 - fx);

                int x0 = Math.Clamp(ix - 1, 0, srcW - 1);
                int x1 = Math.Clamp(ix, 0, srcW - 1);
                int x2 = Math.Clamp(ix + 1, 0, srcW - 1);
                int x3 = Math.Clamp(ix + 2, 0, srcW - 1);

                double r0 = src[py0 + x0] * wx0 + src[py0 + x1] * wx1 + src[py0 + x2] * wx2 + src[py0 + x3] * wx3;
                double r1 = src[py1 + x0] * wx0 + src[py1 + x1] * wx1 + src[py1 + x2] * wx2 + src[py1 + x3] * wx3;
                double r2 = src[py2 + x0] * wx0 + src[py2 + x1] * wx1 + src[py2 + x2] * wx2 + src[py2 + x3] * wx3;
                double r3 = src[py3 + x0] * wx0 + src[py3 + x1] * wx1 + src[py3 + x2] * wx2 + src[py3 + x3] * wx3;

                double val = Math.Clamp(r0 * wy0 + r1 * wy1 + r2 * wy2 + r3 * wy3, 0.0, 255.0);
                double norm = Math.Clamp((val - low) / span, 0.0, 1.0);
                double ink = Math.Pow(norm, 0.9);
                pixels[dstRow + ox] = (byte)Math.Round(255.0 * (1.0 - ink));
            }
        }

        return new GrayImage(outW, outH, pixels);
    }

    private static double CubicWeight(double dist)
    {
        double d = Math.Abs(dist);
        if (d < 1.0) return 1.5 * d * d * d - 2.5 * d * d + 1.0;
        if (d < 2.0) return -0.5 * d * d * d + 2.5 * d * d - 4.0 * d + 2.0;
        return 0.0;
    }

    private static double Percentile(float[] sorted, double p)
    {
        if (sorted.Length == 0) return 0.0;
        int idx = Math.Clamp((int)Math.Floor(p * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static byte[] ComputeLuma(RgbImage image)
    {
        int n = image.Width * image.Height;
        var luma = new byte[n];
        var rgb = image.Rgb;
        for (int i = 0, j = 0; i < n; i++, j += 3)
            luma[i] = (byte)((rgb[j] * 299 + rgb[j + 1] * 587 + rgb[j + 2] * 114) / 1000);
        return luma;
    }

    private static byte[] DownsampleLuma(byte[] src, int srcW, int srcH, int ds, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH];
        int area = ds * ds;
        for (int y = 0; y < dstH; y++)
        {
            int sy0 = y * ds;
            int dstRow = y * dstW;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = x * ds;
                int sum = 0;
                for (int dy = 0; dy < ds; dy++)
                {
                    int sRow = (sy0 + dy) * srcW + sx0;
                    for (int dx = 0; dx < ds; dx++)
                        sum += src[sRow + dx];
                }
                dst[dstRow + x] = (byte)(sum / area);
            }
        }
        return dst;
    }

    private static int MaxRgb(RgbImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 3;
        return Math.Max(image.Rgb[i], Math.Max(image.Rgb[i + 1], image.Rgb[i + 2]));
    }
}
