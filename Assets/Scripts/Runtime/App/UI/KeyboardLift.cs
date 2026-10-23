// KeyboardLift.cs
// Remake: on a phone the on-screen keyboard covers the lower half of the screen in landscape, and a text field there (the
// main menu's pilot name prompt, the Host card's fields, the Mod Builder's) was typed into blind: GameActivity's keyboard
// shows no input box of its own. While the keyboard is up and a text field of the screen has the focus, the screen's
// root slides up (style.translate) until the field sits a little above the keyboard, never so far that the field's top
// leaves the screen; it slides back when the keyboard goes or the focus leaves the field. The keyboard's height is
// TouchScreenKeyboard.area, else (a device that reports none) 55 % of the screen.
//   Watch(root): once per screen root (MainMenu, StationMenu, FlightHud, next to PadKeyboard.Attach).

using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public static class KeyboardLift
    {
        const float Margin = 16f;          // panel units between the field and the keyboard
        const float FallbackShare = 0.55f; // of the screen, when the keyboard reports no area
        const float Ease = 14f;            // per second

        public static void Watch(VisualElement root)
        {
            if (root == null || !(Application.isMobilePlatform && TouchScreenKeyboard.isSupported)) return;
            if (root.ClassListContains("keyboard-lift-host")) return;
            root.AddToClassList("keyboard-lift-host");
            float lift = 0f;
            root.schedule.Execute(() =>
            {
                float target = Target(root, lift);
                if (target == lift) return;   // nothing to move (most frames): the style isn't touched
                lift = Mathf.Abs(target - lift) < 0.5f ? target : Mathf.Lerp(lift, target, 1f - Mathf.Exp(-Ease * Time.unscaledDeltaTime));
                root.style.translate = lift > 0f ? new Translate(0f, -lift) : new StyleTranslate(StyleKeyword.Null);
            }).Every(0);
        }

        /// <summary>How far 'root' should be lifted now (panel units); 'lift' is what is applied (in worldBound already).</summary>
        static float Target(VisualElement root, float lift)
        {
            if (root.panel == null || !TouchScreenKeyboard.visible) return 0f;
            var field = FieldOf(root.focusController?.focusedElement as VisualElement);
            if (field == null || !root.Contains(field)) return 0f;
            float panelH = root.panel.visualTree.layout.height;
            if (float.IsNaN(panelH) || panelH <= 0f || Screen.height <= 0) return lift;
            float keyboardPx = TouchScreenKeyboard.area.height;
            if (keyboardPx <= 1f) keyboardPx = Screen.height * FallbackShare;
            float keyboardTop = panelH - keyboardPx * panelH / Screen.height;
            var b = field.worldBound;
            float top = b.yMin + lift, bottom = b.yMax + lift;   // where it sits unlifted
            float need = bottom + Margin - keyboardTop;
            return Mathf.Clamp(need, 0f, Mathf.Max(0f, top - Margin));
        }

        static TextField FieldOf(VisualElement e)
        {
            for (; e != null; e = e.parent) if (e is TextField f) return f;
            return null;
        }
    }
}
