// PhotoGuides.cs
// Remake-only: the photo modes' composition guides (UI.PhotoTools draws them over the frame, Camera settings > Guides):
// thirds, quarters, the golden ratio grid, the golden spiral with its rectangles, the golden triangles, the diagonals
// (both corners' and 45 degrees from each corner) and the centre. Each guide is a set of polylines in the frame's own
// pixels (y down), so the triangles' right angles and the spiral's shape hold on any frame. Plain C#.

using System.Collections.Generic;
using UnityEngine;

namespace GoF2Remake.UI
{
    public static class PhotoGuides
    {
        public enum Mode { Off, Thirds, Quarters, GoldenRatio, GoldenSpiral, GoldenTriangles, Diagonals, Centre }

        public const float Phi = 1.6180339887f;
        public const int SpiralTurns = 10;     // quarter arcs: past that they are under a pixel on any screen
        const int ArcPoints = 24;

        /// <summary>Guides with a direction (Camera settings > Guide direction): the spiral's four corners, the triangles'
        /// two diagonals (corner 0 / 2 and 1 / 3 alike).</summary>
        public static bool Turns(Mode mode) => mode == Mode.GoldenSpiral || mode == Mode.GoldenTriangles;

        /// <summary>The lines of 'mode' over a w x h frame. 'corner' 0..3 mirrors the spiral and the triangles: bit 0
        /// left / right, bit 1 top / bottom, so the spiral winds toward any corner. 'faint' gets the lines drawn lighter
        /// (the spiral's rectangles).</summary>
        public static List<Vector2[]> Lines(Mode mode, float w, float h, int corner, List<Vector2[]> faint = null)
        {
            var lines = new List<Vector2[]>();
            if (w <= 0f || h <= 0f) return lines;
            switch (mode)
            {
                case Mode.Thirds: Grid(lines, w, h, 1f / 3f, 2f / 3f); break;
                case Mode.Quarters: Grid(lines, w, h, 0.25f, 0.5f, 0.75f); break;
                case Mode.GoldenRatio: Grid(lines, w, h, 1f - 1f / Phi, 1f / Phi); break;
                case Mode.GoldenSpiral: Spiral(lines, faint, w, h, corner); break;
                case Mode.GoldenTriangles: Triangles(lines, w, h, corner); break;
                case Mode.Diagonals: Diagonals(lines, w, h); break;
                case Mode.Centre:
                    lines.Add(new[] { new Vector2(w / 2f, 0f), new Vector2(w / 2f, h) });
                    lines.Add(new[] { new Vector2(0f, h / 2f), new Vector2(w, h / 2f) });
                    break;
            }
            return lines;
        }

        static void Grid(List<Vector2[]> lines, float w, float h, params float[] at)
        {
            foreach (float f in at)
            {
                lines.Add(new[] { new Vector2(w * f, 0f), new Vector2(w * f, h) });
                lines.Add(new[] { new Vector2(0f, h * f), new Vector2(w, h * f) });
            }
        }

        /// <summary>The golden spiral: the frame cut along its long side at 1 / phi again and again (left, top, right,
        /// bottom), a quarter ellipse in each cut-off piece from one corner to the next, so the arcs join into one curve that
        /// winds in toward the last piece. The pieces are only squares on a golden frame; on any other the spiral is
        /// stretched to fit, as photo editors draw it. A portrait frame is cut top first (x and y swapped).</summary>
        static void Spiral(List<Vector2[]> lines, List<Vector2[]> faint, float w, float h, int corner)
        {
            bool swap = h > w;
            float W = swap ? h : w, H = swap ? w : h;
            float x0 = 0f, y0 = 0f, x1 = W, y1 = H, a = 1f / Phi;
            var curve = new List<Vector2>();
            var rects = new List<Vector2[]>();
            for (int i = 0; i < SpiralTurns; i++)
            {
                Vector2 c, p0, p1, cutA, cutB;
                switch (i % 4)
                {
                    case 0:   // the piece on the left: bottom-left to top-right around its bottom-right corner
                    {
                        float cx = x0 + (x1 - x0) * a;
                        c = new Vector2(cx, y1); p0 = new Vector2(x0, y1); p1 = new Vector2(cx, y0);
                        cutA = new Vector2(cx, y0); cutB = new Vector2(cx, y1);
                        x0 = cx;
                        break;
                    }
                    case 1:   // on top: top-left to the right edge around its bottom-left corner
                    {
                        float cy = y0 + (y1 - y0) * a;
                        c = new Vector2(x0, cy); p0 = new Vector2(x0, y0); p1 = new Vector2(x1, cy);
                        cutA = new Vector2(x0, cy); cutB = new Vector2(x1, cy);
                        y0 = cy;
                        break;
                    }
                    case 2:   // on the right: top-right to the bottom edge around its top-left corner
                    {
                        float cx = x1 - (x1 - x0) * a;
                        c = new Vector2(cx, y0); p0 = new Vector2(x1, y0); p1 = new Vector2(cx, y1);
                        cutA = new Vector2(cx, y0); cutB = new Vector2(cx, y1);
                        x1 = cx;
                        break;
                    }
                    default:  // at the bottom: bottom-right to the left edge around its top-right corner
                    {
                        float cy = y1 - (y1 - y0) * a;
                        c = new Vector2(x1, cy); p0 = new Vector2(x1, y1); p1 = new Vector2(x0, cy);
                        cutA = new Vector2(x0, cy); cutB = new Vector2(x1, cy);
                        y1 = cy;
                        break;
                    }
                }
                rects.Add(new[] { cutA, cutB });
                for (int k = curve.Count == 0 ? 0 : 1; k <= ArcPoints; k++)
                {
                    float t = k * (Mathf.PI / 2f) / ArcPoints;
                    curve.Add(c + (p0 - c) * Mathf.Cos(t) + (p1 - c) * Mathf.Sin(t));
                }
            }
            lines.Add(Place(curve.ToArray(), swap, w, h, corner));
            if (faint != null) foreach (var r in rects) faint.Add(Place(r, swap, w, h, corner));
        }

        /// <summary>'pts' in the spiral's own frame (x along the long side) to the frame's, then mirrored by 'corner'.</summary>
        static Vector2[] Place(Vector2[] pts, bool swap, float w, float h, int corner)
        {
            var o = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var p = swap ? new Vector2(pts[i].y, pts[i].x) : pts[i];
                if ((corner & 1) != 0) p.x = w - p.x;
                if ((corner & 2) != 0) p.y = h - p.y;
                o[i] = p;
            }
            return o;
        }

        /// <summary>The golden triangles: a diagonal, and from the two other corners the lines meeting it at a right
        /// angle (in pixels, so on the screen). Odd corners take the other diagonal.</summary>
        static void Triangles(List<Vector2[]> lines, float w, float h, int corner)
        {
            bool other = (corner & 1) != 0;
            Vector2 a = other ? new Vector2(w, 0f) : Vector2.zero, b = other ? new Vector2(0f, h) : new Vector2(w, h);
            Vector2 c = other ? Vector2.zero : new Vector2(w, 0f), d = other ? new Vector2(w, h) : new Vector2(0f, h);
            lines.Add(new[] { a, b });
            lines.Add(new[] { c, Foot(c, a, b) });
            lines.Add(new[] { d, Foot(d, a, b) });
        }

        /// <summary>The point of the line a-b nearest p.</summary>
        public static Vector2 Foot(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            return a + ab * (Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        }

        /// <summary>Both diagonals, and from every corner a line at 45 degrees (on the screen) into the frame up to its edge.</summary>
        static void Diagonals(List<Vector2[]> lines, float w, float h)
        {
            lines.Add(new[] { Vector2.zero, new Vector2(w, h) });
            lines.Add(new[] { new Vector2(w, 0f), new Vector2(0f, h) });
            float s = Mathf.Min(w, h);
            lines.Add(new[] { Vector2.zero, new Vector2(s, s) });
            lines.Add(new[] { new Vector2(w, 0f), new Vector2(w - s, s) });
            lines.Add(new[] { new Vector2(0f, h), new Vector2(s, h - s) });
            lines.Add(new[] { new Vector2(w, h), new Vector2(w - s, h - s) });
        }
    }
}
