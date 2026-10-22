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

        /// <summary>The spinner before 'label''s text while 'on' (a Label or a Button; it keeps its own text and style).
        /// A Button's text moves into a child Label meanwhile and back after: a Button measures its own text only while it
        /// has no children, so with the spinner in it it shrank to a letter's width and its text stood on end (the Mod
        /// Builder's and the Mod Browser's Import while the file picker was open).</summary>
        public static void Mark(TextElement label, bool on)
        {
            if (label == null) return;
            var spinner = label.Q(className: "gof-busy");
            // A Button lays the spinner out beside its text (.gof-busy-button): the label's room is a left padding, which
            // a button's own padding overrides, so the spinner lay over the text.
            bool isButton = label is Button;
            label.EnableInClassList(isButton ? "gof-busy-button" : "gof-busy-label", on);
            if (on && spinner == null)
            {
                if (label is Button button && button.childCount == 0 && !string.IsNullOrEmpty(button.text))
                {
                    var text = new Label(button.text) { pickingMode = PickingMode.Ignore };
                    text.AddToClassList("gof-busy-text");
                    var look = button.resolvedStyle;
                    text.style.fontSize = look.fontSize;
                    text.style.color = look.color;
                    text.style.letterSpacing = look.letterSpacing;
                    text.style.unityFontStyleAndWeight = look.unityFontStyleAndWeight;
                    text.style.unityFontDefinition = look.unityFontDefinition;
                    button.text = "";
                    button.Add(text);
                }
                label.Insert(0, Spinner());
            }
            else if (!on)
            {
                spinner?.RemoveFromHierarchy();
                if (label is Button button && label.Q<Label>(className: "gof-busy-text") is Label text)
                {
                    text.RemoveFromHierarchy();
                    button.text = text.text;
                }
            }
        }
    }
}
