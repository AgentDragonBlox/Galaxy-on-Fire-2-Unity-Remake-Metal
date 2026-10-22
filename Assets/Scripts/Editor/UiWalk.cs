// UiWalk.cs
// Remake (Editor): walks a screen with a controller (docs/UI-DESIGN.md "Checking a screen"): presses buttons on a virtual
// gamepad through the Input System, one every few frames, and after each press records which element of the UI Toolkit
// panels has the focus (its name, classes and text), so a script can check that every control is reached, the order
// follows the layout and Back leaves. Play mode only. From a script / the unity CLI: UiWalk.Run("down down a b"), then
// poll UiWalk.Status until "done" and read UiWalk.Result. Buttons: up down left right a b x y lb rb lt rt menu view, and wait (nothing).
// The presses go through McpInput's virtual gamepad, which ignores the window's focus while it holds something (an
// unfocused Game view takes no device input otherwise, and the UI's navigation saw none of the walk's presses).

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace GoF2Remake.EditorTools
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class UiWalk
    {
        public static string Status { get; private set; } = "idle";
        public static string Result { get; private set; } = "";

        static readonly Queue<string> steps = new Queue<string>();
        static readonly StringBuilder log = new StringBuilder();
        static int wait;
        static bool down;
        static string current;
        const float HoldSeconds = 0.1f;

        public static void Run(string presses)
        {
            if (!EditorApplication.isPlaying) { Status = "error: Play mode only"; return; }
            steps.Clear();
            foreach (var s in presses.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries)) steps.Enqueue(s.ToLowerInvariant());
            log.Clear();
            log.Append("start: ").AppendLine(Focused());
            Status = "running";
            wait = 3;
            down = false;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish("error: Play mode ended"); return; }
            GoF2Remake.UI.McpInput.Tick();   // the focus settings back once nothing is held
            if (--wait > 0) return;
            if (down)
            {
                if (GoF2Remake.UI.McpInput.Busy) return;   // still held
                down = false;
                wait = 6;   // the press's effects, a laid-out frame
                return;
            }
            if (current != null) log.Append(current).Append(" -> ").AppendLine(Focused());
            if (steps.Count == 0) { Finish("done"); return; }
            current = steps.Dequeue();
            Press(current);
            down = true;
            wait = 2;
        }

        static void Press(string b)
        {
            GamepadButton? button = b switch
            {
                "up" => GamepadButton.DpadUp,
                "down" => GamepadButton.DpadDown,
                "left" => GamepadButton.DpadLeft,
                "right" => GamepadButton.DpadRight,
                "a" => GamepadButton.South,
                "b" => GamepadButton.East,
                "x" => GamepadButton.West,
                "y" => GamepadButton.North,
                "lb" => GamepadButton.LeftShoulder,
                "rb" => GamepadButton.RightShoulder,
                "menu" => GamepadButton.Start,
                "view" => GamepadButton.Select,
                _ => null,
            };
            if (button != null) GoF2Remake.UI.McpInput.HoldButton(button.Value, HoldSeconds);
            else if (b == "lt") GoF2Remake.UI.McpInput.LeftTrigger(HoldSeconds);
            else if (b == "rt") GoF2Remake.UI.McpInput.RightTrigger(HoldSeconds);
            // "wait": nothing pressed, a few frames pass
        }

        /// <summary>The focused element of every live panel (the menus', the HUD's...), described.</summary>
        static string Focused()
        {
            var sb = new StringBuilder();
            foreach (var root in GoF2Remake.UI.McpInput.Roots())   // every shown panel's root, the topmost first
            {
                var f = root?.panel?.focusController?.focusedElement as VisualElement;
                if (f == null) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(Describe(f));
            }
            return sb.Length > 0 ? sb.ToString() : "(nothing focused)";
        }

        static string Describe(VisualElement e)
        {
            string text = e is TextElement t ? t.text : e.Q<Label>()?.text;
            if (text != null && text.Length > 28) text = text.Substring(0, 28);
            string cls = string.Join(".", e.GetClasses());
            return $"{e.GetType().Name}#{e.name}.{cls} '{text}'";
        }

        static void Finish(string status)
        {
            EditorApplication.update -= Tick;
            steps.Clear();
            current = null;
            Result = log.ToString();
            Status = status;
        }
    }
}
