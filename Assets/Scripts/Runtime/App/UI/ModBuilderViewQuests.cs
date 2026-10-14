// ModBuilderViewQuests.cs
// The Mod Builder's Quests kind (part of ModBuilderView), edited in the Space Lounge where missions are offered: the
// project's quests and bar missions as step lists (ProjectQuest, events/<name>.gof2quest; the game turns the steps into an
// event graph's script, Events.QuestFile; Modding/README.md "Quests made in the Mod Builder"). Left: the quests and bar
// missions, a new one of each, Remove, and the event graphs made in the Unity Editor (listed, run like the rest). Right: the
// title, the kind, when a quest starts (at once, after another quest, after a visit, a story step, never, or a condition), a
// bar mission's offer, client and stations, the reward; then the steps in order (a conversation with its pages, a radio call,
// the objective, going somewhere, a fight, cargo, rewards, waits, checkpoints, music, titles, waypoints, the next quest, the
// end), the picked one opened for editing, moved up / down, removed; "+ Add a step" after it. Speakers, stations, ships and
// items come from ModRefs: the game's, the project's own (its characters!) and other mods' (which become dependencies).
// Play starts a play test with the quest under way (ModBuilderPlay.StartQuest); the script it makes can be looked at.

using System;
using System.Collections.Generic;
using System.Linq;
using GoF2Remake.Data;
using GoF2Remake.Events;
using GoF2Remake.Modding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public partial class ModBuilderView
    {
        VisualElement questList, questPage;
        ProjectQuest currentQuest;
        JObject currentStep;

        /// <summary>The steps offered (QuestFile's types), in the picker's order.</summary>
        static readonly (string type, string key, string english, string what)[] StepTypes =
        {
            ("dialog", "mbStepDialog", "Conversation", "Pages with portraits, like the story's"),
            ("radio", "mbStepRadio", "Radio call", "A call in the flight HUD"),
            ("objective", "mbStepObjective", "Objective", "The Missions window's line and the target on the map"),
            ("goto", "mbStepGoto", "Go to a station", "Waits until the player docks there (or reaches its orbit)"),
            ("fight", "mbStepFight", "Fight", "Ships in an orbit; waits until they are destroyed"),
            ("cargo", "mbStepCargo", "Bring cargo", "Waits until the hold has it"),
            ("reward", "mbStepReward", "Reward", "Credits and items, in the reward box"),
            ("give", "mbStepGive", "Give an item", "Into the hold, without a box"),
            ("wait", "mbStepWait", "Wait", "Seconds"),
            ("waituntil", "mbStepWaitUntil", "Wait until", "A condition"),
            ("checkpoint", "mbStepCheckpoint", "Checkpoint", "A save goes on from here"),
            ("music", "mbStepMusic", "Music", "A track, or stop"),
            ("title", "mbStepTitle", "Title", "Big text on the screen"),
            ("waypoint", "mbStepWaypoint", "Waypoint", "A point in an orbit the autopilot flies to"),
            ("startquest", "mbStepStartQuest", "Start a quest", "Another quest beside this one"),
            ("complete", "mbStepComplete", "Complete", "The quest is done, the reward paid"),
            ("fail", "mbStepFail", "Fail", "Mission failed"),
            ("command", "mbStepCommand", "Command", "Any event command (as typed in multiplayer)"),
        };

        static string StepName(string type) => StepTypes.FirstOrDefault(s => s.type == type) is var st && st.type != null ? T(st.key, st.english) : type;

        void BuildQuestsPanels(VisualElement left, VisualElement right)
        {
            questList = ListPanel(left, T("mbQuests", "Quests"));
            Act(left, T("mbNewQuest", "+ New quest"), () => NewQuest(false), "mbv-add");
            Act(left, T("mbNewMission", "+ New bar mission"), () => NewQuest(true), "mbv-add");
            Act(left, T("mbRemoveQuest", "Remove"), AskRemoveQuest, "mbv-danger", "mbv-remove");
            questPage = PagePanel(right);
        }

        void RebuildQuests()
        {
            questList.Clear();
            if (Project == null) return;
            if (currentQuest != null && !Project.Quests.Contains(currentQuest)) currentQuest = null;
            currentQuest ??= Project.Quests.FirstOrDefault();
            foreach (var quest in Project.Quests)
            {
                var q = quest;
                ListRow(questList, q.Title ?? q.Name, (q.IsMission ? T("mbBarMission", "Bar mission") : T("mbQuest", "Quest")) + "  ·  " + q.Name + "  ·  "
                        + string.Format(T("mbSteps", "{0} steps"), q.Steps.Count), q == currentQuest,
                        () => { currentQuest = q; currentStep = null; RebuildQuests(); BuildQuestPage(); }, null, null);
            }
            var graphs = Project.EventGraphs();
            if (graphs.Count > 0)
            {
                Heading(questList, T("mbEventGraphs", "Event graphs"));
                foreach (var g in graphs) ListRow(questList, g, T("mbMadeInUnity", "Made in the Unity Editor: edit it there"), false, () => { }, null, null);
            }
            if (Project.Quests.Count == 0 && graphs.Count == 0)
                Note(questList, T("mbNoQuests", "No quests yet. A quest starts by itself and runs like the game's story; a bar mission is offered by a visitor in the Space Lounges."));
        }

        void NewQuest(bool mission) => AskText(mission ? T("mbNewMission", "+ New bar mission") : T("mbNewQuest", "+ New quest"), T("mbTitleField", "Title"), "", title =>
        {
            var q = Project.AddQuest(title, mission);
            if (mission) { q.Offer = T("mbOfferDefault", "I have a job for you."); q.Reward = 10000; }
            // A first conversation and the end, to build on.
            var page = new JObject { ["speaker"] = "player", ["text"] = T("mbFirstLine", "Let's get to work.") };
            q.AddStep("dialog")["pages"] = new JArray(page);
            q.AddStep("complete");
            currentQuest = q;
            currentStep = null;
            MarkDirty();
            RebuildQuests();
            BuildQuestPage();
        });

        void AskRemoveQuest()
        {
            var q = currentQuest;
            if (q == null) return;
            Confirm(T("mbRemoveQuest", "Remove"), string.Format(T("mbRemoveText", "Remove \"{0}\" from the project?"), q.Title ?? q.Name), () =>
            {
                Project.Quests.Remove(q);
                currentQuest = null;
                currentStep = null;
                MarkDirty();
                RebuildQuests();
                BuildQuestPage();
            });
        }

        void QuestEdited(bool rebuildPage = false)
        {
            MarkDirty();
            if (rebuildPage) BuildQuestPage();
        }

        // ---- the page ---------------------------------------------------------------------------------------------

        void BuildQuestPage()
        {
            questPage.Clear();
            var q = currentQuest;
            if (q == null) { Note(questPage, T("mbPickQuest", "Add a quest or a bar mission on the left to start.")); return; }
            var top = Row(questPage);
            Act(top, T("mbPlayQuest", "Play it"), () => PlayQuest(q), "mbv-play");
            Act(top, T("mbShowScript", "Show the script"), () => ShowScript(q));
            Text(questPage, T("mbTitleField", "Title"), q.Title, 60, v => { q.Title = v.Trim(); QuestEdited(); RebuildQuests(); });
            Note(questPage, string.Format(T("mbQuestName", "Its name for other quests and the campaign: {0}"), q.Name));
            Segments(questPage, T("mbKind", "Kind"), new[] { T("mbQuest", "Quest"), T("mbBarMission", "Bar mission") }, q.IsMission ? 1 : 0, n => { q.IsMission = n == 1; QuestEdited(true); RebuildQuests(); });
            if (!q.IsMission) BuildStartsWhen(q);
            else BuildMissionOffer(q);
            Int(questPage, T("mbRewardCredits", "Reward (credits)"), q.Reward, 0, 999999999, v => { q.Reward = v; QuestEdited(); });

            Heading(questPage, T("mbStepsHeading", "Steps"));
            var steps = q.StepList;
            if (currentStep != null && !steps.Contains(currentStep)) currentStep = null;
            for (int i = 0; i < steps.Count; i++)
            {
                var st = steps[i];
                int n = i;
                bool open = st == currentStep;
                var card = Add(questPage, new VisualElement(), "mbv-step-card");
                card.EnableInClassList("mbv-step-card--on", open);
                var head = new Button(() => { sound(false); currentStep = open ? null : st; BuildQuestPage(); }) { focusable = false };
                head.AddToClassList("mbv-step-head");
                Add(head, new Label($"{n + 1}. {StepName((string)st["type"])}") { pickingMode = PickingMode.Ignore }, "mbv-step-name", "gof-semibold");
                Add(head, new Label(StepSummary(st)) { pickingMode = PickingMode.Ignore }, "mbv-step-summary");
                card.Add(head);
                if (!open) continue;
                var tools = Row(card);
                Act(tools, "▲", () => { q.MoveStep(st, -1); QuestEdited(true); }, "mbv-step");
                Act(tools, "▼", () => { q.MoveStep(st, 1); QuestEdited(true); }, "mbv-step");
                Act(tools, T("mbDelete", "Delete"), () => { q.RemoveStep(st); currentStep = null; QuestEdited(true); RebuildQuests(); }, "mbv-danger");
                BuildStep(card, q, st, n);
            }
            if (steps.Count == 0) Note(questPage, T("mbNoSteps", "No steps: add the first one."));
            Act(questPage, currentStep != null ? T("mbAddStepAfter", "+ Add a step after it") : T("mbAddStep", "+ Add a step"), () => PickStepType(q), "mbv-add");
            Note(questPage, T("mbStepsNote", "The steps run one after another. A quest ends at Complete or Fail (without one it completes after the last step). Put a Checkpoint after each step the player shouldn't repeat after loading a save."));
        }

        static readonly string[] StartModes = { "At once", "After a quest", "After a visit", "After a story step", "Never", "Condition" };

        void BuildStartsWhen(ProjectQuest q)
        {
            string w = q.StartsWhen.Trim();
            int mode = w.Length == 0 ? 0 : w == "never" ? 4 : Matches(w, @"^quest\((.+)\)\s*==\s*2$", out string qa) ? 1
                     : Matches(w, @"^visited\((.+)\)$", out string va) ? 2 : Matches(w, @"^campaign\s*>=\s*(\d+)$", out string ca) ? 3 : 5;
            Segments(questPage, T("mbStartsWhen", "Starts"), StartModes.Select(m => T("mbStart_" + m.Replace(" ", ""), m)).ToArray(), mode, n =>
            {
                q.StartsWhen = n switch { 0 => "", 4 => "never", 1 => $"quest({Project.Quests.FirstOrDefault(o => o != q)?.Name ?? "my_quest"}) == 2",
                                          2 => $"visited({Session.StationIndex})", 3 => "campaign >= 20", _ => w };
                QuestEdited(true);
            });
            switch (mode)
            {
                case 1:
                    Matches(w, @"^quest\((.+)\)\s*==\s*2$", out string after);
                    RefField(questPage, T("mbAfterQuest", "When this quest is done"), RefKind.Quest, after, v => { if (v != null) { q.StartsWhen = $"quest({v}) == 2"; QuestEdited(true); } }, clear: false,
                             filter: e => e.value != q.Name);
                    break;
                case 2:
                    Matches(w, @"^visited\((.+)\)$", out string st);
                    RefField(questPage, T("mbAfterVisit", "Once the player has docked at"), RefKind.Station, st, v => { if (v != null) { q.StartsWhen = $"visited({v})"; QuestEdited(true); } }, clear: false);
                    break;
                case 3:
                    Matches(w, @"^campaign\s*>=\s*(\d+)$", out string step);
                    Int(questPage, T("mbStoryStep", "From the story step"), int.TryParse(step, out int s) ? s : 20, 0, 162, v => { q.StartsWhen = $"campaign >= {v}"; QuestEdited(); });
                    Note(questPage, T("mbStoryStepNote", "The GoF2 story's step (Debug menu's list); in free play and mod campaigns it is -1, so such a quest waits for the story."));
                    break;
                case 5:
                    ConditionField(questPage, T("mbCondition", "Condition"), w, v => { q.StartsWhen = v ?? ""; QuestEdited(); });
                    break;
            }
        }

        static bool Matches(string s, string pattern, out string group)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", pattern);
            group = m.Success ? m.Groups[1].Value.Trim() : null;
            return m.Success;
        }

        void BuildMissionOffer(ProjectQuest q)
        {
            Text(questPage, T("mbOffer", "What the visitor says"), q.Offer, 600, v => { q.Offer = v; QuestEdited(); }, true);
            RefField(questPage, T("mbClient", "The client"), RefKind.Character, q.Client, v => { q.Client = v; QuestEdited(true); }, T("mbClientAny", "someone of the station's race"));
            Caption(questPage, T("mbOfferedAt", "Offered at"));
            var stations = q.Stations;
            var row = Row(questPage);
            Add(row, new Label(stations.Count == 0 ? T("mbEveryStation", "every station") : string.Join(", ", stations.Select(s => RefLabel(RefKind.Station, s, s)))), "mbv-inline-label");
            Act(row, "+", () => PickRef(RefKind.Station, T("mbOfferedAt", "Offered at"), e => { var l = q.Stations; if (!l.Contains(e.value)) l.Add(e.value); q.Stations = l; QuestEdited(true); }), "mbv-step");
            if (stations.Count > 0) Act(row, "✕", () => { q.Stations = null; QuestEdited(true); }, "mbv-step");
        }

        void PickStepType(ProjectQuest q)
        {
            var choices = new List<(string, Texture2D, Action)>();
            foreach (var (type, key, english, what) in StepTypes)
            {
                string t = type;
                choices.Add(($"{T(key, english)}\n{T(key + "What", what)}", null, () =>
                {
                    int at = currentStep != null ? q.Steps.IndexOf(currentStep) + 1 : -1;
                    var st = q.AddStep(t, at);
                    NewStepDefaults(st, t);
                    currentStep = st;
                    QuestEdited(true);
                    RebuildQuests();
                }));
            }
            ShowPicker(T("mbAddStep", "+ Add a step"), choices);
        }

        void NewStepDefaults(JObject st, string type)
        {
            switch (type)
            {
                case "dialog": st["pages"] = new JArray(new JObject { ["speaker"] = "player", ["text"] = "" }); break;
                case "radio": st["speaker"] = "player"; st["text"] = ""; break;
                case "goto": st["station"] = ProjectEntry.RefToken(Session.StationIndex.ToString()); st["dock"] = true; break;
                case "fight": st["station"] = ProjectEntry.RefToken(Session.StationIndex.ToString()); st["ships"] = new JArray(new JObject { ["ship"] = 2, ["count"] = 3, ["race"] = "pirate" }); st["quiet"] = true; break;
                case "cargo": st["item"] = 22; st["amount"] = 10; break;
                case "reward": st["credits"] = 5000; break;
                case "give": st["item"] = 22; st["amount"] = 1; break;
                case "wait": st["seconds"] = 3; break;
                case "music": st["track"] = "battle"; break;
            }
        }

        string StepSummary(JObject st)
        {
            string S(string k) => st[k] is JValue v && v.Type != JTokenType.Null ? v.ToString() : "";
            string Short(string s) => s.Length > 60 ? s.Substring(0, 57) + "…" : s;
            switch (S("type"))
            {
                case "dialog":
                    var pages = (st["pages"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                    return pages.Count == 0 ? "" : Short($"{WhoLabel(S2(pages[0], "speaker"))}: {S2(pages[0], "text")}") + (pages.Count > 1 ? $"  (+{pages.Count - 1})" : "");
                case "radio": return Short($"{WhoLabel(S("speaker"))}: {S("text")}");
                case "objective": return Short(S("text"));
                case "goto": return (st["dock"]?.Type == JTokenType.Boolean && !(bool)st["dock"] ? T("mbToOrbit", "the orbit of ") : T("mbDockAt", "dock at ")) + RefLabel(RefKind.Station, S("station"), "?");
                case "fight":
                    var ships = (st["ships"] as JArray)?.OfType<JObject>().Select(o => $"{(int?)o["count"] ?? 1} × {RefLabel(RefKind.Ship, S2(o, "ship"), "?")}") ?? Enumerable.Empty<string>();
                    return Short(string.Join(", ", ships) + "  ·  " + RefLabel(RefKind.Station, S("station"), T("mbWherever", "wherever the player is")));
                case "cargo": return $"{S("amount")} × {RefLabel(RefKind.Item, S("item"), "?")}";
                case "reward": return $"{S("credits")} $" + ((st["items"] as JArray)?.Count > 0 ? " + " + string.Format(T("mbItemsCount", "{0} items"), ((JArray)st["items"]).Count) : "");
                case "give": return $"{S("amount")} × {RefLabel(RefKind.Item, S("item"), "?")}";
                case "wait": return S("seconds") + " s";
                case "waituntil": return Short(S("condition"));
                case "music": return S("track");
                case "title": return Short(S("title"));
                case "waypoint": return S("at").Length > 0 ? RefLabel(RefKind.Station, S("station"), "?") + "  " + S("at") : T("mbCleared", "cleared");
                case "startquest": return S("quest");
                case "complete": return S("reward").Length > 0 ? S("reward") + " $" : "";
                case "fail": return Short(S("title"));
                case "command": return Short(S("line"));
            }
            return "";
        }

        static string S2(JObject o, string k) => o[k] is JValue v && v.Type != JTokenType.Null ? v.ToString() : "";

        /// <summary>A speaker for people: a character's name ("Captain Vega"), a story speaker's, "Keith (the player)".</summary>
        string WhoLabel(string speaker) => string.IsNullOrEmpty(speaker) ? "?" : RefLabel(RefKind.Character, speaker, speaker);

        // ---- one step's fields ----------------------------------------------------------------------------------

        void BuildStep(VisualElement card, ProjectQuest q, JObject st, int index)
        {
            string type = (string)st["type"];
            string S(string k) => st[k] is JValue v && v.Type != JTokenType.Null ? v.ToString() : null;
            void SetS(string k, string v) { if (string.IsNullOrEmpty(v)) st.Remove(k); else st[k] = v; QuestEdited(); }
            void SetRef(string k, string v) { if (string.IsNullOrEmpty(v)) st.Remove(k); else st[k] = ProjectEntry.RefToken(v); QuestEdited(true); }
            int I(string k, int d) => st[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (int)Math.Round((double)v) : d;
            switch (type)
            {
                case "dialog":
                {
                    var pages = st["pages"] as JArray ?? (JArray)(st["pages"] = new JArray());
                    int pn = 0;
                    foreach (var page in pages.OfType<JObject>().ToList())
                    {
                        var p = page;
                        int pi = pn++;
                        var box = Add(card, new VisualElement(), "mbv-page-box");
                        var prow = Row(box);
                        Add(prow, new Label(string.Format(T("mbPageN", "Page {0}"), pi + 1)), "mbv-inline-label");
                        if (pages.Count > 1) Act(prow, "✕", () => { p.Remove(); QuestEdited(true); }, "mbv-step");
                        SpeakerField(box, (string)p["speaker"], v => { p["speaker"] = v; QuestEdited(true); });
                        Text(box, T("mbLine", "Text"), (string)p["text"], 600, v => { p["text"] = v; QuestEdited(); }, true);
                        VoiceField(box, q, index, pi, (string)p["voice"], v => { if (v == null) p.Remove("voice"); else p["voice"] = v; QuestEdited(true); });
                    }
                    Act(card, T("mbAddPage", "+ Page"), () =>
                    {
                        string last = pages.OfType<JObject>().LastOrDefault()?["speaker"]?.ToString() ?? "player";
                        pages.Add(new JObject { ["speaker"] = last, ["text"] = "" });
                        QuestEdited(true);
                    }, "mbv-add");
                    Note(card, T("mbDialogNote", "%player% in a text is the player's name."));
                    break;
                }
                case "radio":
                    SpeakerField(card, S("speaker"), v => SetS("speaker", v));
                    Text(card, T("mbLine", "Text"), S("text"), 300, v => SetS("text", v), true);
                    VoiceField(card, q, index, 0, S("voice"), v => { SetS("voice", v); BuildQuestPage(); });
                    break;
                case "objective":
                    Text(card, T("mbObjectiveText", "Objective"), S("text"), 300, v => SetS("text", v), true);
                    RefField(card, T("mbTargetStation", "Target on the map"), RefKind.Station, S("station"), v => SetRef("station", v), T("mbNoTarget", "none"));
                    break;
                case "goto":
                    RefField(card, T("mbStation", "Station"), RefKind.Station, S("station"), v => SetRef("station", v), clear: false);
                    Segments(card, null, new[] { T("mbDocked", "Docked there"), T("mbInOrbit", "In its orbit") }, st["dock"]?.Type == JTokenType.Boolean && !(bool)st["dock"] ? 1 : 0,
                             n => { st["dock"] = n == 0; QuestEdited(true); });
                    Text(card, T("mbObjectiveOptional", "Objective meanwhile (optional)"), S("objective"), 300, v => SetS("objective", v));
                    break;
                case "fight":
                {
                    RefField(card, T("mbInOrbitOf", "In the orbit of"), RefKind.Station, S("station"), v => SetRef("station", v), T("mbWherever", "wherever the player is"));
                    var ships = st["ships"] as JArray ?? (JArray)(st["ships"] = new JArray());
                    foreach (var sh in ships.OfType<JObject>().ToList())
                    {
                        var o = sh;
                        var box = Add(card, new VisualElement(), "mbv-page-box");
                        var srow = Row(box);
                        Add(srow, new Label(RefLabel(RefKind.Ship, S2(o, "ship"), "?")), "mbv-inline-label");
                        Act(srow, T("mbChoose", "Choose…"), () => PickRef(RefKind.Ship, T("mbShip", "Ship"), e => { o["ship"] = ProjectEntry.RefToken(e.value); QuestEdited(true); }), "mbv-step");
                        Act(srow, "✕", () => { o.Remove(); QuestEdited(true); }, "mbv-step");
                        Int(box, T("mbCount", "How many"), o["count"] is JValue c ? (int)c : 1, 1, 10, v => { o["count"] = v; QuestEdited(); });
                        string race = (string)o["race"] ?? "";
                        string[] races = { "", "pirate", "terran", "vossk", "nivelian", "midorian", "void" };
                        Segments(box, T("mbFlownBy", "Flown by"), new[] { T("mbItsMaker", "its maker"), "Pirates", "Terran", "Vossk", "Nivelian", "Midorian", "Void" },
                                 Math.Max(0, Array.IndexOf(races, race)), n => { if (n == 0) o.Remove("race"); else o["race"] = races[n]; QuestEdited(true); });
                        string beh = (string)o["behaviour"] ?? "enemy";
                        string[] behs = { "enemy", "neutral", "friendly" };
                        Segments(box, null, new[] { T("mbEnemy", "Enemy"), T("mbNeutral", "Neutral"), T("mbFriendly", "Friendly") }, Math.Max(0, Array.IndexOf(behs, beh)),
                                 n => { if (n == 0) o.Remove("behaviour"); else o["behaviour"] = behs[n]; QuestEdited(true); });
                    }
                    Act(card, T("mbAddShips", "+ Ships"), () => PickRef(RefKind.Ship, T("mbShip", "Ship"), e => { ships.Add(new JObject { ["ship"] = ProjectEntry.RefToken(e.value), ["count"] = 1 }); QuestEdited(true); }), "mbv-add");
                    Text(card, T("mbShipsName", "Their name (on the lock plate)"), S("name"), 32, v => SetS("name", v));
                    var frow = Row(card);
                    FlagButton(frow, T("mbQuietOrbit", "No other traffic there"), st["quiet"]?.Type != JTokenType.Boolean || (bool)st["quiet"], v => st["quiet"] = v, () => BuildQuestPage());
                    Text(card, T("mbObjectiveOptional", "Objective meanwhile (optional)"), S("objective"), 300, v => SetS("objective", v));
                    Note(card, T("mbFightNote", "They come in front of the player a few seconds after the player reaches the orbit; the step ends when none of them is left."));
                    break;
                }
                case "cargo":
                    RefField(card, T("mbItem", "Item"), RefKind.Item, S("item"), v => SetRef("item", v), clear: false);
                    Int(card, T("mbAmount", "Amount"), I("amount", 1), 1, 100000, v => { st["amount"] = v; QuestEdited(); });
                    RefField(card, T("mbDockedAtOptional", "Docked at (optional)"), RefKind.Station, S("station"), v => SetRef("station", v), T("mbAnywhere", "anywhere"));
                    Text(card, T("mbObjectiveOptional", "Objective meanwhile (optional)"), S("objective"), 300, v => SetS("objective", v));
                    break;
                case "reward":
                {
                    Int(card, T("mbCredits", "Credits"), I("credits", 0), 0, 999999999, v => { st["credits"] = v; QuestEdited(); });
                    var items = (st["items"] as JArray)?.OfType<JObject>().Select(o => new ProjectStack(ProjectEntry.RefText(o["item"]), (int?)o["amount"] ?? 1)).ToList() ?? new List<ProjectStack>();
                    StackList(card, T("mbItems", "Items"), items, l => { st["items"] = ProjectStack.Write(l); if (l.Count == 0) st.Remove("items"); QuestEdited(true); }, false);
                    Text(card, T("mbBoxTitle", "Title of the box (optional)"), S("title"), 60, v => SetS("title", v));
                    break;
                }
                case "give":
                    RefField(card, T("mbItem", "Item"), RefKind.Item, S("item"), v => SetRef("item", v), clear: false);
                    Int(card, T("mbAmount", "Amount"), I("amount", 1), 1, 1000, v => { st["amount"] = v; QuestEdited(); });
                    break;
                case "wait":
                    Float(card, T("mbSeconds", "Seconds"), st["seconds"] is JValue sv ? (float)sv : 1f, 0f, 3600f, v => { st["seconds"] = v; QuestEdited(); });
                    break;
                case "waituntil":
                    ConditionField(card, T("mbCondition", "Condition"), S("condition"), v => SetS("condition", v));
                    break;
                case "checkpoint":
                    Note(card, T("mbCheckpointNote", "Loading a save goes on from here: what came before isn't played again. Keep checkpoints on the quest's own line of steps."));
                    break;
                case "music":
                {
                    var row = Row(card);
                    Add(row, new Label(S("track") ?? "stop"), "mbv-inline-label");
                    Act(row, T("mbChoose", "Choose…"), () => PickMusicWord(v => SetS("track", v)), "mbv-step");
                    break;
                }
                case "title":
                    Text(card, T("mbTitleField", "Title"), S("title"), 60, v => SetS("title", v));
                    Text(card, T("mbSubtitle", "Line under it"), S("subtitle"), 120, v => SetS("subtitle", v));
                    break;
                case "waypoint":
                    RefField(card, T("mbStation", "Station"), RefKind.Station, S("station"), v => SetRef("station", v), clear: false);
                    Text(card, T("mbAtXyz", "At (x y z, game units from the station)"), S("at"), 60, v => SetS("at", v));
                    Note(card, T("mbWaypointNote", "Empty: the waypoint is cleared. The station sits at 0 0 0; 20000 units is a kilometre."));
                    break;
                case "startquest":
                    RefField(card, T("mbQuestField", "Quest"), RefKind.Quest, S("quest"), v => SetS("quest", v), clear: false, filter: e => e.value != q.Name);
                    break;
                case "complete":
                    Int(card, T("mbRewardOwn", "Reward (empty = the quest's)"), I("reward", -1), -1, 999999999, v => { if (v < 0) st.Remove("reward"); else st["reward"] = v; QuestEdited(); });
                    Text(card, T("mbBoxTitle", "Title of the box (optional)"), S("title"), 60, v => SetS("title", v));
                    break;
                case "fail":
                    Text(card, T("mbFailText", "What the player is told"), S("title"), 120, v => SetS("title", v));
                    break;
                case "command":
                    Text(card, T("mbCommandLine", "Command"), S("line"), 400, v => SetS("line", v));
                    Note(card, T("mbCommandNote", "Any event command without the \"/\": spawn @a 2 pirate 3 enemy named Raider, npc Raider attack player, cutscene @a start, ..."));
                    break;
            }
        }

        /// <summary>A speaker: a character (the project's own, the game's story characters, other mods'), or "race name".</summary>
        void SpeakerField(VisualElement parent, string value, Action<string> set)
        {
            Caption(parent, T("mbSpeaker", "Speaker"));
            var row = Row(parent);
            Add(row, new Label(WhoLabel(value)), "mbv-inline-label");
            Act(row, T("mbChoose", "Choose…"), () => PickRef(RefKind.Character, T("mbSpeaker", "Speaker"), e => set(e.value)), "mbv-step");
            Act(row, T("mbRaceAndName", "A race and a name…"), () => AskText(T("mbRaceAndName", "A race and a name…"), T("mbRaceNameField", "e.g. vossk K'ekki, terran female Jane"), value, set, 60), "mbv-step");
        }

        /// <summary>A page's voice: a sound file of the project (voices/), or the game's voice line by its name.</summary>
        void VoiceField(VisualElement parent, ProjectQuest q, int step, int page, string value, Action<string> set)
        {
            var row = Row(parent);
            Add(row, new Label($"{T("mbVoice", "Voice")}: {value ?? T("mbNone", "none")}"), "mbv-inline-label");
            Act(row, T("mbVoiceFile", "Sound file…"), () => PickAudio(T("mbPickVoice", "Choose the voice line"), path =>
            {
                string clip = $"{q.Name}_{step + 1}_{page + 1}";
                Project.SetAudio(ModProject.VoicesFolder, clip, path);
                set(clip);
            }), "mbv-step");
            Act(row, T("mbGameVoice", "Game's line…"), () => AskText(T("mbGameVoice", "Game's line…"), T("mbVoiceName", "The voice line's name (voices.md)"), value, set, 80), "mbv-step");
            if (value != null) Act(row, "✕", () => set(null), "mbv-step");
        }

        /// <summary>A track for a Music step: the game's (the event words) or a mod's.</summary>
        void PickMusicWord(Action<string> pick)
        {
            var choices = new List<(string, Texture2D, Action)> { (T("mbStopMusic", "Stop the music"), null, () => pick("stop")), (T("mbFromGame", "The game"), null, null) };
            foreach (var m in Enum.GetNames(typeof(EventMusic))) { string w = m.ToLowerInvariant(); choices.Add((m, null, () => pick(w))); }
            string group = null;
            foreach (var e in ModRefs.List(RefKind.Music, Project).Where(e => e.group != ModRefs.GameGroup))
            {
                if (e.group != group) { group = e.group; choices.Add((e.own ? string.Format(T("mbFromThisMod", "This mod: {0}"), e.group) : string.Format(T("mbFromMod", "Mod: {0} (becomes a dependency)"), e.group), null, null)); }
                var en = e;
                choices.Add((e.name, null, () => { if (ModRefs.Use(Project, en)) RequestReload(false); pick(en.value); }));
            }
            ShowPicker(T("mbMusic", "Music"), choices);
        }

        void ShowScript(ProjectQuest q)
        {
            var problems = new List<string>();
            string script = QuestFile.Compile(q.ToJson(), problems);
            modal.Clear();
            var box = Add(modal, new VisualElement(), "mbv-modal-box", "mbv-modal-box--list", "mbv-panel");
            Heading(box, T("mbScript", "The script the game runs"));
            foreach (var p in problems) Add(box, new Label("✖ " + p), "mbv-note", "mbv-warn");
            var scroll = Add(box, new ScrollView(ScrollViewMode.Vertical), "mbv-picker-scroll");
            var field = Add(scroll, new TextField { value = script, multiline = true, isReadOnly = true }, "mbv-field", "mbv-script");
            Act(box, T("mbClose", "Close"), CloseModal);
            modal.AddToClassList("mbv-modal--shown");
        }

        void PlayQuest(ProjectQuest q)
        {
            var problems = new List<string>();
            QuestFile.Compile(q.ToJson(), problems);
            if (problems.Count > 0) { Status("✖ " + problems[0], true); return; }
            SaveNow();
            ModBuilder.Reload();
            afterReload.Clear();
            string why = ModBuilderPlay.StartQuest(q.Name);
            if (why != null) Status(why, true);
        }
    }
}
