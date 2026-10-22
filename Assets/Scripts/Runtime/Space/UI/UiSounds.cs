// UiSounds.cs
// Rework (docs/UI-DESIGN.md "Sound"): the menus' three feedback sounds for the screens without sounds of their own: a
// focus move by the keys or a controller (event 124 Button_Push at 0.45, as the main menu's), a button's or toggle's click
// and Back (both 123 Button_Release). Attach(root) hooks a panel's root once; Move / Confirm / Back play them from code.
// The main menu (MainMenu.HookFocusSound), the station (StationMenu's root hooks) and the flight HUD's buttons
// (FlightHud.ButtonSounds) play the same clips at the same volumes themselves.

using GoF2Remake.Data;
using GoF2Remake.Flight;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class UiSounds
    {
        static AudioSource source;
        static CombatAudio audio;
        static AudioClip lastClip;
        static int lastFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            source = null;
            audio = null;
            lastClip = null;
            lastFrame = -1;
        }

        static CombatAudio Audio => audio != null ? audio : audio = CombatAudio.Load();

        /// <summary>The focus moved to another control (the keys, a controller).</summary>
        public static void Move() => Play(Audio != null ? Audio.buttonPush : null, 0.45f);

        /// <summary>A button or toggle was pressed.</summary>
        public static void Confirm() => Play(Audio != null ? Audio.buttonRelease : null, 1f);

        /// <summary>Back / Esc / B closed something.</summary>
        public static void Back() => Play(Audio != null ? Audio.buttonRelease : null, 1f);

        public static void Play(AudioClip clip, float volume)
        {
            if (clip == null || Application.isBatchMode) return;
            if (clip == lastClip && lastFrame == Time.frameCount) return;   // once a frame: a click and its submit both report
            lastClip = clip;
            lastFrame = Time.frameCount;
            if (source == null)
            {
                var go = new GameObject("UiSounds") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(go);
                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.ignoreListenerPause = true;   // the pause menu pauses the listener
            }
            source.PlayOneShot(Modding.ModSounds.Get(clip), Settings.SfxVolume * volume);
        }

        /// <summary>Sounds for every control under 'root' (once per root): a focus move by the keys or a controller, and
        /// unless 'clicks' is false a click on a button or toggle and Back.</summary>
        public static void Attach(VisualElement root, bool clicks = true)
        {
            if (root == null || root.ClassListContains("ui-sounds")) return;
            root.AddToClassList("ui-sounds");
            root.RegisterCallback<FocusInEvent>(_ =>
            {
                // Not under the mouse or a finger: a pointer focuses what it presses, and that press clicks.
                if (InputMode.Current == InputKind.Gamepad || InputMode.Current == InputKind.KeyboardMouse && !InputMode.MousePointing) Move();
            }, TrickleDown.TrickleDown);
            if (!clicks) return;
            root.RegisterCallback<ClickEvent>(e =>
            {
                for (var v = e.target as VisualElement; v != null; v = v.parent)
                    if (v is Button || v is Toggle) { Confirm(); return; }
            });
            root.RegisterCallback<NavigationCancelEvent>(_ => Back());
        }
    }
}
