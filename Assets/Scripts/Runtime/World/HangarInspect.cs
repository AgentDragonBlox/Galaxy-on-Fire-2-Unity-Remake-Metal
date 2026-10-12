// HangarInspect.cs
// Remake-only: the hangar's "Inspect ship". The camera leaves its drifting hangar spot (StationLevel.UpdateCamera), zooms in
// on the player's ship and orbits it like the flight's Action Freeze (UI.PhotoMode): yaw / pitch around the ship's centre
// (the bounds of its meshes), a distance between just outside the hull and a few times its size. The orbit starts where the
// hangar camera is, so the way in is a zoom and a turn toward the ship (BlendMs, the distance easing to FocusRadii x the
// radius); leaving blends back to the hangar camera the same way. The pitch keeps the camera above the hangar floor (the
// hull's lowest point, the pad). Input comes from UI.StationMenu (drag, wheel, pinch, keys, sticks, triggers). Plain C#.

using UnityEngine;

namespace GoF2Remake.World
{
    public sealed class HangarInspect
    {
        const float BlendMs = 900f;             // hangar camera <-> orbit
        const float FocusRadii = 2.1f;          // the distance it zooms to on entering, in ship radii
        const float MinRadii = 1.15f, MaxRadii = 4f;
        const float MaxPitch = 1.45f;           // rad (~83 deg): never straight over the top
        const float FloorClearance = 1.5f;      // m above the hull's lowest point
        const float ZoomEase = 0.008f;          // per ms

        Vector3 centre;
        float radius, floorY, yaw, pitch, distance, targetDistance, minDistance, maxDistance;

        /// <summary>Inspecting (entered and not left; the blend may still be under way).</summary>
        public bool Active { get; private set; }
        /// <summary>0 = the hangar camera .. 1 = the orbit camera (eased; it runs back to 0 after End).</summary>
        public float Blend { get; private set; }
        /// <summary>The camera is the orbit's, wholly or partly.</summary>
        public bool Shown => Active || Blend > 0f;
        public float Distance => distance;
        public float Radius => radius;
        public Vector3 Centre => centre;

        /// <summary>From the hangar camera at 'cameraPosition' around the ship of 'shipBounds' (world); 'minRadii': how close it
        /// may zoom, in ship radii (the Mod Builder goes closer than Inspect ship).</summary>
        public void Begin(Vector3 cameraPosition, Bounds shipBounds, float minRadii = MinRadii)
        {
            centre = shipBounds.center;
            radius = Mathf.Max(1f, shipBounds.extents.magnitude);
            floorY = shipBounds.min.y;
            var d = cameraPosition - centre;
            distance = Mathf.Max(d.magnitude, radius * minRadii);
            minDistance = radius * minRadii;
            maxDistance = Mathf.Max(radius * MaxRadii, minDistance + 1f);
            yaw = Mathf.Atan2(d.x, d.z);
            pitch = Mathf.Asin(Mathf.Clamp(d.y / Mathf.Max(d.magnitude, 1e-3f), -1f, 1f));
            targetDistance = Mathf.Clamp(radius * FocusRadii, minDistance, maxDistance);
            Active = true;   // re-entered mid-blend, the blend goes on from where it is
        }

        public void End() => Active = false;

        /// <summary>Gone at once (the view left the hangar): no blend back.</summary>
        public void Reset() { Active = false; Blend = 0f; }

        /// <summary>Turns the orbit: + yaw takes the camera to its own left (the ship turns with a drag to the right), + pitch
        /// takes it up.</summary>
        public void Turn(float yawRad, float pitchRad)
        {
            yaw += yawRad;
            pitch += pitchRad;
        }

        /// <summary>Zooms: the distance it eases to x 'factor' (&lt; 1 = closer).</summary>
        public void Zoom(float factor) => targetDistance = Mathf.Clamp(targetDistance * factor, minDistance, maxDistance);

        /// <summary>Real time (the camera keeps moving when the game is slowed).</summary>
        public void Tick(float dtMs)
        {
            Blend = Mathf.MoveTowards(Blend, Active ? 1f : 0f, dtMs / BlendMs);
            distance = Mathf.Lerp(distance, targetDistance, 1f - Mathf.Exp(-ZoomEase * dtMs));
            // Above the floor: sin(pitch) * distance >= floor + clearance - centre.
            float minPitch = Mathf.Asin(Mathf.Clamp((floorY + FloorClearance - centre.y) / Mathf.Max(distance, 1e-3f), -1f, 1f));
            pitch = Mathf.Clamp(pitch, Mathf.Max(minPitch, -MaxPitch), MaxPitch);
        }

        /// <summary>The camera between the hangar pose and the orbit's, by the eased blend. Returns the near plane (m) for the
        /// orbit (in front of the hull at the closest zoom), or 'hangarNear' when it isn't shown.</summary>
        public float Apply(Transform camera, Vector3 hangarPos, Quaternion hangarRot, float hangarNear)
        {
            if (!Shown) return hangarNear;
            var dir = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            var pos = centre + dir * distance;
            var rot = Quaternion.LookRotation(-dir, Vector3.up);
            float b = Mathf.SmoothStep(0f, 1f, Blend);
            camera.SetPositionAndRotation(Vector3.Lerp(hangarPos, pos, b), Quaternion.Slerp(hangarRot, rot, b));
            float orbitNear = Mathf.Clamp((distance - radius) * 0.5f, 0.1f, hangarNear);
            return Mathf.Lerp(hangarNear, orbitNear, b);
        }
    }
}
