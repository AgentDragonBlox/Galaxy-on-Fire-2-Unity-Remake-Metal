// MacGameControllerRunner.cs
// Remake, macOS: MacGameControllers' clock, on a hidden DontDestroyOnLoad object: every half second the controllers
// GameController lists are matched to virtual pads (devices are added outside the input update), and the pads go with it.

#if UNITY_STANDALONE_OSX || UNITY_EDITOR
using UnityEngine;

namespace GoF2Remake.UI
{
    public class MacGameControllerRunner : MonoBehaviour
    {
        const float SyncSeconds = 0.5f;
        float next;

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + SyncSeconds;
            MacGameControllers.Sync();
        }

        void OnDestroy() => MacGameControllers.Uninstall();
    }
}
#endif
