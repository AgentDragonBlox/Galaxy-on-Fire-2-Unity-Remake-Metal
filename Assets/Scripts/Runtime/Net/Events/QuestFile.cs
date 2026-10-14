// QuestFile.cs
// Remake-only: a quest or bar mission made in the Mod Builder (events/<name>.gof2quest, Modding.ProjectQuest) turned into an
// event graph's script (EventRunner's line form, the same EventGraphScript writes from a node graph), so it runs like any
// other quest: a JSON list of steps instead of nodes, for modders without the Unity Editor.
//   header   title, kind (quest | mission), startsWhen (quests), reward, offer / client / stations / players (missions)
//   steps    dialog       pages [{ speaker, text, voice }]               the conversation window
//            radio        speaker, text, voice                          a call in the flight HUD's radio box
//            objective    text, station                                 the Missions window's line, the gold target icon
//            goto         station, dock (true: docked, else in orbit), objective
//            fight        station, ships [{ ship, count, race, behaviour }], name, quiet, objective
//                         waits in that orbit, spawns them ahead of the player, waits until every one of them is gone
//            cargo        item, amount, station                         waits until the hold has them (docked there)
//            reward       credits, items [{ item, amount }], title
//            give         item, amount
//            wait         seconds
//            waituntil    condition (an expression)
//            checkpoint   (a save goes on from here)
//            music        track (a game track word, a mod's track, or "stop")
//            title        title, subtitle
//            waypoint     station, at "x y z" (empty: cleared)
//            startquest   quest
//            complete     reward, title
//            fail         title
//            command      line (any event command)
// Text never carries the script's own marks: '#' starts a comment there, '|' separates pages / answers, '{ }' are
// expressions; they are replaced. Plain C#.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GoF2Remake.Events
{
    public static class QuestFile
    {
        public const string Extension = "gof2quest";

        /// <summary>A .gof2quest file's text?</summary>
        public static bool Is(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.TrimStart();
            return t.StartsWith("{") && t.Contains("\"gof2quest\"");
        }

        /// <summary>The players every step speaks to: everyone the run covers (single player: the player; a bar mission: its
        /// team, NetCommands.Scope).</summary>
        const string Players = "@a";

        /// <summary>The script, or "" with the problems (step numbers from 1).</summary>
        public static string Compile(string json, List<string> problems)
        {
            JObject o;
            try { o = JObject.Parse(json); }
            catch (Exception e) { problems.Add("can't read the quest file: " + e.Message); return ""; }
            return Compile(o, problems);
        }

        public static string Compile(JObject o, List<string> problems)
        {
            var sb = new StringBuilder();
            void Line(string l) => sb.Append(l.TrimEnd()).Append('\n');
            string title = Clean(Str(o, "title"));
            bool mission = Str(o, "kind") == "mission";
            int reward = Int(o, "reward", 0);
            if (title.Length == 0) problems.Add("The quest needs a title.");
            if (!mission)
            {
                if (title.Length > 0) Line("mission title " + title);
                Line("mission kind quest");
                string starts = Str(o, "startsWhen").Trim();
                if (starts.Length > 0) Line("mission starts " + Expr(starts));
                if (reward > 0) Line("mission reward " + reward);
            }
            else
            {
                Line("mission kind mission");
                Line("mission title " + title);
                string offer = Clean(Text(o, "offer"));
                if (offer.Length > 0) Line("mission offer " + offer);
                string client = Clean(Str(o, "client"));
                if (client.Length > 0) Line("mission client " + client);
                if (reward > 0) Line("mission reward " + reward);
                int min = 1, max = 4;
                if (o["players"] is JArray pl && pl.Count >= 2) { min = Math.Max(1, (int)pl[0]); max = Math.Max(min, (int)pl[1]); }
                Line($"mission players {min} {max}");
                var stations = Refs(o["stations"]);
                if (stations.Count > 0) Line("mission stations " + string.Join(", ", stations));
            }
            Line("");
            var steps = o["steps"] as JArray ?? new JArray();
            int n = 0, enemies = 0;
            bool ended = false;
            foreach (var t in steps)
            {
                n++;
                if (!(t is JObject st)) { problems.Add($"Step {n}: not a step."); continue; }
                string type = Str(st, "type").ToLowerInvariant();
                void Problem(string m) => problems.Add($"Step {n} ({type}): {m}");
                switch (type)
                {
                    case "dialog":
                    {
                        var pages = new List<string>();
                        foreach (var p in (st["pages"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            string speaker = Clean(Str(p, "speaker")), text = Clean(Text(p, "text"));
                            if (text.Length == 0) continue;
                            if (speaker.Length == 0) speaker = "player";
                            pages.Add(speaker + " : " + text + Voice(p));
                        }
                        if (pages.Count == 0) { Problem("a conversation needs a page with text."); break; }
                        Line($"dialog {Players} " + string.Join(" | ", pages));
                        break;
                    }
                    case "radio":
                    {
                        string speaker = Clean(Str(st, "speaker")), text = Clean(Text(st, "text"));
                        if (text.Length == 0) { Problem("a radio call needs a text."); break; }
                        Line($"radio {Players} {(speaker.Length > 0 ? speaker : "player")} : {text}{Voice(st)}");
                        break;
                    }
                    case "objective":
                        Objective(Line, Clean(Text(st, "text")), Ref(st["station"]));
                        break;
                    case "goto":
                    {
                        string station = Ref(st["station"]);
                        if (station.Length == 0) { Problem("pick the station."); break; }
                        string obj = Clean(Text(st, "objective"));
                        if (obj.Length > 0) Objective(Line, obj, station);
                        Line($"wait until {(Bool(st, "dock", true) ? "docked" : "inorbit")}({station}) > 0");
                        break;
                    }
                    case "fight":
                    {
                        string station = Ref(st["station"]);
                        var ships = (st["ships"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                        if (ships.Count == 0) { Problem("add the ships to fight."); break; }
                        string name = Clean(Str(st, "name"));
                        if (name.Length == 0) name = "Quest Enemy " + (++enemies);
                        string obj = Clean(Text(st, "objective"));
                        if (obj.Length > 0) Objective(Line, obj, station);
                        bool quiet = station.Length > 0 && Bool(st, "quiet", true);
                        if (quiet) Line("questorbit " + station);
                        Line(station.Length > 0 ? $"wait until inorbit({station}) > 0" : "wait until inspace > 0");
                        Line("wait 3");
                        string who = station.Length > 0 ? $"@alive[orbit={station}]" : "@alive";
                        foreach (var sh in ships)
                        {
                            string ship = Ref(sh["ship"]);
                            if (ship.Length == 0) { Problem("a ship isn't picked."); continue; }
                            int count = Math.Max(1, Math.Min(10, Int(sh, "count", 1)));
                            string race = Clean(Str(sh, "race")).ToLowerInvariant();
                            string behaviour = Clean(Str(sh, "behaviour")).ToLowerInvariant();
                            if (behaviour.Length == 0) behaviour = "enemy";
                            Line(string.Join(" ", new[] { "spawn", who, ship, race, count.ToString(CultureInfo.InvariantCulture), behaviour, "named", name }.Where(x => x.Length > 0)));
                        }
                        Line("wait 2");
                        Line($"wait until alive({name}) == 0");
                        if (quiet) Line($"questorbit {station} off");
                        break;
                    }
                    case "cargo":
                    {
                        string item = Ref(st["item"]), station = Ref(st["station"]);
                        if (item.Length == 0) { Problem("pick the item."); break; }
                        int amount = Math.Max(1, Int(st, "amount", 1));
                        string obj = Clean(Text(st, "objective"));
                        if (obj.Length > 0) Objective(Line, obj, station);
                        Line($"wait until cargo({item}) >= {amount}" + (station.Length > 0 ? $" and docked({station}) > 0" : ""));
                        break;
                    }
                    case "reward":
                    {
                        var parts = new List<string>();
                        int credits = Int(st, "credits", 0);
                        if (credits != 0) parts.Add(credits.ToString(CultureInfo.InvariantCulture));
                        foreach (var it in (st["items"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            string item = Ref(it["item"]);
                            if (item.Length > 0) parts.Add(item + " " + Math.Max(1, Int(it, "amount", 1)));
                        }
                        if (parts.Count == 0) { Problem("a reward needs credits or items."); break; }
                        string rt = Clean(Text(st, "title"));
                        Line($"reward {Players} " + string.Join(" + ", parts) + (rt.Length > 0 ? " | " + rt : ""));
                        break;
                    }
                    case "give":
                    {
                        string item = Ref(st["item"]);
                        if (item.Length == 0) { Problem("pick the item."); break; }
                        Line($"give {Players} {item} {Math.Max(1, Math.Min(1000, Int(st, "amount", 1)))}");
                        break;
                    }
                    case "wait":
                        Line("wait " + Num(st, "seconds", 1));
                        break;
                    case "waituntil":
                    {
                        string c = Str(st, "condition").Trim();
                        if (c.Length == 0) { Problem("write the condition."); break; }
                        Line("wait until " + Expr(c));
                        break;
                    }
                    case "checkpoint":
                        Line("checkpoint step" + n);
                        break;
                    case "music":
                    {
                        string track = Clean(Str(st, "track"));
                        if (track.Length == 0) track = "stop";
                        Line($"music {Players} {track}");
                        break;
                    }
                    case "title":
                    {
                        string tt = Clean(Text(st, "title")), sub = Clean(Text(st, "subtitle"));
                        if (tt.Length == 0 && sub.Length == 0) { Problem("write a title."); break; }
                        Line($"title {Players} {tt}" + (sub.Length > 0 ? " | " + sub : ""));
                        break;
                    }
                    case "waypoint":
                    {
                        string station = Ref(st["station"]), at = Clean(Str(st, "at"));
                        if (station.Length == 0 || at.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length != 3) { Line($"waypoint {Players} off"); break; }
                        Line($"waypoint {Players} {station} {at}");
                        break;
                    }
                    case "startquest":
                    {
                        string q = Clean(Str(st, "quest"));
                        if (q.Length == 0 || q.Contains(" ")) { Problem("pick the quest."); break; }
                        Line("startquest " + q);
                        break;
                    }
                    case "complete":
                    {
                        int r = Int(st, "reward", -1);
                        string ct = Clean(Text(st, "title"));
                        Line(("complete " + (r >= 0 ? r.ToString(CultureInfo.InvariantCulture) : "")).TrimEnd() + (ct.Length > 0 ? " | " + ct : ""));
                        ended = true;
                        break;
                    }
                    case "fail":
                        Line(("fail " + Clean(Text(st, "title"))).TrimEnd());
                        ended = true;
                        break;
                    case "command":
                    {
                        string l = (Str(st, "line") ?? "").Replace("\r", " ").Replace("\n", " ").Trim().TrimStart('/');
                        if (l.Length > 0) Line(l);
                        break;
                    }
                    default:
                        Problem($"unknown step \"{type}\".");
                        break;
                }
            }
            if (!ended) Line(mission ? "complete" : "complete");
            return sb.ToString();
        }

        static void Objective(Action<string> line, string text, string station)
        {
            if (text.Length > 0) line("objective " + text);
            line("target " + (station.Length > 0 ? station : "off"));
        }

        static string Voice(JObject o)
        {
            string v = Clean(Str(o, "voice"));
            return v.Length > 0 && !v.Contains(" ") ? " [voice " + v + "]" : "";
        }

        static string Str(JObject o, string k) => o[k] is JValue v && v.Type != JTokenType.Null ? v.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>A text field: a string, or one per language ({"en": ...}): the player's language, else English.</summary>
        static string Text(JObject o, string k)
        {
            if (o[k] is JObject t)
            {
                string lang = Data.Localization.Language;
                return (string)(t[lang] ?? t["en"] ?? t.Properties().FirstOrDefault()?.Value) ?? "";
            }
            return Str(o, k);
        }

        static int Int(JObject o, string k, int d) => o[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (int)Math.Round((double)v) : d;
        static string Num(JObject o, string k, double d) =>
            (o[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (double)v : d).ToString("0.###", CultureInfo.InvariantCulture);
        static bool Bool(JObject o, string k, bool d) => o[k] is JValue v && v.Type == JTokenType.Boolean ? (bool)v : d;

        /// <summary>A reference (a number or a key) as the script names it.</summary>
        static string Ref(JToken t) => t is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.String) ? Clean(v.ToString(CultureInfo.InvariantCulture)) : "";

        static List<string> Refs(JToken t) => t is JArray a ? a.Select(Ref).Where(x => x.Length > 0).ToList() : new List<string>();

        /// <summary>Text without the script's own marks (one line).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '#': b.Append("No."); break;
                    case '|': b.Append('/'); break;
                    case '{': b.Append('('); break;
                    case '}': b.Append(')'); break;
                    case '\r': break;
                    case '\n': b.Append(' '); break;
                    default: b.Append(c); break;
                }
            }
            return b.ToString().Trim();
        }

        /// <summary>An expression as written (one line; '#' would cut it).</summary>
        static string Expr(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("#", "").Trim();
    }
}
