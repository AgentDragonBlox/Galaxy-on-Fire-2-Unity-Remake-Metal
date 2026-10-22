// NetChat.cs
// Multiplayer chat: global (every player anywhere) and local (the players in the same orbit, or docked at the same
// station). A message goes to the host (NetState.SendChatRpc), which stamps it with the sender's name and where they are
// and sends it to everyone; each player keeps the global ones and the local ones from where they are themselves. Plus
// the session's notices (a player joined / left). A line starting with "/" is a command for this game (NetCommands). ChatView shows them in the flight HUD and the station menu.
// While a chat line is being typed (Typing) the game's key input is off: the code-made InputActions are disabled and the
// direct keyboard reads go through Keys (null meanwhile); outside a session Keys is just Keyboard.current.

using System;
using System.Collections.Generic;
using GoF2Remake.Data;
using UnityEngine;
using UnityEngine.InputSystem;
using GoF2Remake.Events;

namespace GoF2Remake.Multiplayer
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class NetChat
    {
        public const int MaxLength = 160, Keep = 60;
        /// <summary>A typed command may be longer (a /dialog conversation; the server's cap NetGuard.MaxCommandArgs).</summary>
        public const int MaxCommandLength = 500;

        public enum Channel { Local, Global, Notice, Whisper, Faction }

        public sealed class Message
        {
            public Channel channel;
            public string from, text;
            public float time;
            public bool own;
        }

        static readonly List<Message> messages = new List<Message>();
        static readonly List<InputAction> paused = new List<InputAction>();

        // Play mode without a domain reload keeps statics.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            paused.Clear();
            Typing = false;
            typingEndFrame = -10;
        }

        public static IReadOnlyList<Message> Messages => messages;
        public static event Action<Message> Added;

        /// <summary>The channel new lines go to (ChatView's switch). Faction only while this player is in one (InFaction):
        /// SendingNow falls back to Local when they leave it.</summary>
        public static Channel Sending = Channel.Local;

        /// <summary>This player is in a faction (a dedicated server with profiles): the Faction channel is offered.</summary>
        public static bool InFaction => NetPlayer.Local != null && NetPlayer.Local.FactionTag.Length > 0;

        /// <summary>The channel lines go to now: Sending, but Local once the player has left their faction.</summary>
        public static Channel SendingNow => Sending == Channel.Faction && !InFaction ? Channel.Local : Sending;

        /// <summary>The channel key / button: Local, Global, then Faction when in one, round again.</summary>
        public static void NextChannel()
        {
            var now = SendingNow;
            Sending = now == Channel.Local ? Channel.Global : now == Channel.Global && InFaction ? Channel.Faction : Channel.Local;
        }

        /// <summary>A channel's name on its button and line tag.</summary>
        public static string ChannelName(Channel c) =>
            c == Channel.Global ? Localization.Extra("mpChatGlobal", "Global")
            : c == Channel.Faction ? Localization.Extra("mpChatFaction", "Faction")
            : Localization.Extra("mpChatLocal", "Local");

        /// <summary>A channel's colour (#rrggbb): Local cyan, Global amber, Faction green.</summary>
        public static string ChannelColor(Channel c) =>
            c == Channel.Global ? "#f0b35a" : c == Channel.Faction ? "#8fe28f" : "#8fd8ff";

        /// <summary>A chat line is being typed: the game's keys are off.</summary>
        public static bool Typing { get; private set; }

        /// <summary>The keyboard for the game's own key reads: null while a chat line is typed.</summary>
        /// <summary>Also null on the frame the typing ended and the next: Esc closing the chat would otherwise open the
        /// pause / system menu too (the chat's key event and the menus' key reads run in either order).</summary>
        public static Keyboard Keys => Typing || JustStopped || EventScreen.QuestionOpen ? null : Keyboard.current;

        /// <summary>The typing ended this frame or the last (Keys).</summary>
        public static bool JustStopped => Time.frameCount <= typingEndFrame + 1;
        static int typingEndFrame = -10;

        public static void SetTyping(bool on)
        {
            if (Typing == on) return;
            Typing = on;
            if (!on) typingEndFrame = Time.frameCount;
            // The game's controls (GameControls' map) go off; the UI's (text input, clicks) keep running.
            GoF2Remake.Flight.GameControls.Suspend(on);
            if (on)
            {
                // Any other action made in code (no map) goes off too.
                paused.Clear();
                foreach (var a in InputSystem.ListEnabledActions())
                    if (a.actionMap == null) paused.Add(a);
                foreach (var a in paused) a.Disable();
            }
            else
            {
                foreach (var a in paused) if (a != null) a.Enable();
                paused.Clear();
            }
        }

        /// <summary>While typing (ChatView, every frame): an action a component enabled meanwhile goes off too.</summary>
        public static void KeepGameKeysOff()
        {
            if (!Typing) return;
            foreach (var a in InputSystem.ListEnabledActions())
                if (a.actionMap == null && !paused.Contains(a)) { paused.Add(a); a.Disable(); }
        }

        /// <summary>The scene the typing started in is going (ChatView.OnDestroy): typing ends, and its actions stay off
        /// (every code-made game action belongs to a flight-scene component, gone with it).</summary>
        public static void DropTyping()
        {
            paused.Clear();
            if (Typing) GoF2Remake.Flight.GameControls.Suspend(false);
            Typing = false;
        }

        /// <summary>Trimmed, at most MaxLength characters, no rich-text tags (the log is rich text).</summary>
        public static string Clean(string text)
        {
            text = (text ?? "").Replace("<", "").Replace(">", "").Replace('\n', ' ').Trim();
            return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
        }

        static string CleanCommand(string text)
        {
            text = (text ?? "").Replace("<", "").Replace(">", "").Replace('\n', ' ').Trim();
            return text.Length > MaxCommandLength ? text.Substring(0, MaxCommandLength) : text;
        }

        public static void Send(string text)
        {
            text = (text ?? "").TrimStart().StartsWith("/") ? CleanCommand(text) : Clean(text);
            if (NetCommands.TryRun(text)) return;   // "/help", "/sos": this game's own; the rest to the server, never as chat
            if (text.Length == 0 || NetState.Instance == null || !NetState.Instance.IsSpawned) return;
            // The Faction channel: the line goes as the server's /f (NetFactions.Talk, to the members online).
            if (text[0] != '/' && SendingNow == Channel.Faction) text = "/f " + text;
            NetState.Instance.SendChatRpc(text, SendingNow == Channel.Global);
        }

        /// <summary>NetState: a message from the host; kept when global or from where this player is.</summary>
        public static void Receive(ulong sender, string from, string text, bool global, int station, bool inSpace, bool inHangar)
        {
            var me = NetPlayer.Local;
            if (!global && (me == null || me.Station != station || !((inSpace && me.InSpace) || (inHangar && me.InHangar)))) return;
            Add(new Message { channel = global ? Channel.Global : Channel.Local, from = from, text = text, own = me != null && me.OwnerClientId == sender });
        }

        /// <summary>NetState: a private message to this player ('own' = the copy of one this player sent; 'other' = the other
        /// player's name).</summary>
        public static void ReceiveWhisper(string other, string text, bool own) =>
            Add(new Message { channel = Channel.Whisper, from = other, text = text, own = own });

        /// <summary>NetState: a faction member's line (NetFactions.Talk); 'tag' the faction's.</summary>
        public static void ReceiveFaction(string from, string text, bool own) =>
            Add(new Message { channel = Channel.Faction, from = from, text = text, own = own });

        public static void Notice(string text) => Add(new Message { channel = Channel.Notice, text = text });

        static void Add(Message m)
        {
            m.time = Time.unscaledTime;
            // Private messages stay out of the log (it is shared in bug reports).
            if (m.channel == Channel.Whisper) { }
            else Debug.Log(m.channel == Channel.Notice ? $"[Chat] {m.text}" : $"[Chat {m.channel}] {m.from}: {m.text}");
            messages.Add(m);
            if (messages.Count > Keep) messages.RemoveAt(0);
            Added?.Invoke(m);
        }

        /// <summary>A session ended: nothing carries over.</summary>
        public static void Clear()
        {
            SetTyping(false);
            messages.Clear();
            Sending = Channel.Local;
        }

        public static string JoinedText(string name) => string.Format(Localization.Extra("mpJoined", "{0} joined the game."), name);
        public static string LeftText(string name) => string.Format(Localization.Extra("mpLeft", "{0} left the game."), name);
    }
}
