// TwoFingerTouch.cs
// Remake: the two-finger touch gesture of the Mod Builder's camera views (the hangar's ship orbit, the space view, the
// room view): a pinch zooms and both fingers dragged move the camera, as in the photo tools' free camera (PhotoTools).
// Before, a phone could only turn the views (and pinch in the hangar): moving them took the middle mouse button.

using UnityEngine;
using UnityEngine.InputSystem;

namespace GoF2Remake.UI
{
    public sealed class TwoFingerTouch
    {
        float span = -1f;
        Vector2 mid;

        /// <summary>Once a frame: true while two or more fingers are down. 'zoom' is the distance factor since the last frame
        /// (above 1 = pinched together: further out), 'pan' the fingers' midpoint movement in 1080p pixels, y up (both
        /// neutral on the gesture's first frame).</summary>
        public bool Read(out float zoom, out Vector2 pan)
        {
            zoom = 1f;
            pan = Vector2.zero;
            var ts = Touchscreen.current;
            Vector2 a = Vector2.zero, b = Vector2.zero;
            int n = 0;
            if (ts != null)
                foreach (var t in ts.touches)
                {
                    if (!t.press.isPressed) continue;
                    if (n == 0) a = t.position.ReadValue(); else if (n == 1) b = t.position.ReadValue();
                    n++;
                }
            if (n < 2) { span = -1f; return false; }
            float s = (a - b).magnitude;
            var m = (a + b) * 0.5f;
            if (span > 1f && s > 1f)
            {
                zoom = span / s;
                pan = (m - mid) * (1080f / Mathf.Max(1, Screen.height));
            }
            span = s;
            mid = m;
            return true;
        }
    }
}
