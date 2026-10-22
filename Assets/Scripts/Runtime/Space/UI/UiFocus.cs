// UiFocus.cs
// Rework (docs/UI-DESIGN.md "Focus", found by the controller walk): a ScrollView's scroll bars are focusable in UI Toolkit,
// so the D-pad could stop on a list's slider (the hangar window's) where A and the arrows did nothing. SkipScrollBars(root)
// makes every scroll bar under the root unfocusable the first time it would take the focus, and gives the focus back to
// what had it.

using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public static class UiFocus
    {
        public static void SkipScrollBars(VisualElement root)
        {
            if (root == null || root.ClassListContains("ui-skip-scrollbars")) return;
            root.AddToClassList("ui-skip-scrollbars");
            root.RegisterCallback<FocusInEvent>(e =>
            {
                if (!(e.target is VisualElement v)) return;
                Scroller scroller = null;
                for (var p = v; p != null && scroller == null; p = p.parent) scroller = p as Scroller;
                if (scroller == null) return;
                foreach (var f in scroller.Query<VisualElement>().ToList()) f.focusable = false;
                scroller.focusable = false;
                v.Blur();
                if (e.relatedTarget is Focusable back && back != v) back.Focus();
            }, TrickleDown.TrickleDown);
        }
    }
}
