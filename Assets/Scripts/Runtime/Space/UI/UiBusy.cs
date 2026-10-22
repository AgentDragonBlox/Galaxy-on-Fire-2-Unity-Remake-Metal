// UiBusy.cs
// Rework (docs/UI-DESIGN.md "Waiting"): one look for "something is going on": a small turning amber arc before a waiting
// line (connecting, starting a session, asking the server, looking for games, loading parts, importing). Mark(label, on)
// puts it before a label's text and takes it away again; Spinner() is the element alone. A wait with a known end shows a
// bar instead (ModLoadingView). Styles: GoF2Common.uss .gof-busy, .gof-busy-label.

using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public static class UiBusy
    {
        const float TurnsPerSecond = 1.1f;
        static readonly Color Arc = new Color(1f, 0.651f, 0.188f, 1f);        // --gof-amber
        static readonly Color Track = new Color(0.243f, 0.784f, 1f, 0.25f);   // --gof-cyan at the line's alpha

        /// <summary>A turning arc, as big as the body text (it grows with Options > Accessibility "Text size").</summary>
        public static VisualElement Spinner()
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList("gof-busy");
            e.generateVisualContent += Draw;
            float started = Time.unscaledTime;
            e.schedule.Execute(() => e.style.rotate = new Rotate(((Time.unscaledTime - started) * TurnsPerSecond % 1f) * 360f)).Every(16);
            return e;
        }

        static void Draw(MeshGenerationContext ctx)
        {
            var r = ctx.visualElement.contentRect;
            float radius = Mathf.Min(r.width, r.height) * 0.5f;
            if (radius < 2f) return;
            float line = Mathf.Max(2f, radius * 0.24f);
            var p = ctx.painter2D;
            p.lineWidth = line;
            p.lineCap = LineCap.Round;
            var centre = r.center;
            radius -= line * 0.5f;
            p.strokeColor = Track;
            p.BeginPath();
            p.Arc(centre, radius, 0f, 360f);
            p.Stroke();
            p.strokeColor = Arc;
            p.BeginPath();
            p.Arc(centre, radius, -90f, 180f);
            p.Stroke();
        }

        /// <summary>The spinner before 'label''s text while 'on' (a Label or a Button; it keeps its own text and style).</summary>
        public static void Mark(TextElement label, bool on)
        {
            if (label == null) return;
            var spinner = label.Q(className: "gof-busy");
            label.EnableInClassList("gof-busy-label", on);
            if (on && spinner == null) label.Add(Spinner());
            else if (!on) spinner?.RemoveFromHierarchy();
        }
    }
}
