// ProjectQuest.cs
// Remake-only: a quest or bar mission made in the Mod Builder: events/<name>.gof2quest, a JSON list of steps the game turns
// into an event graph's script when it loads it (Events.QuestFile; Modding/README.md "Quests made in the Mod Builder"):
//   { "format": "gof2quest", "title", "kind": "quest" | "mission", "startsWhen" (quests), "reward", "offer", "client",
//     "stations": [...], "players": [min, max] (bar missions), "steps": [ { "type": "dialog", ... }, ... ] }
// The step types and their fields are QuestFile's. Like the other entries a JObject with typed accessors; the steps stay
// JObjects (the builder edits their fields by name). Plain C#.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GoF2Remake.Modding
{
    public class ProjectQuest : ProjectEntry
    {
        public const string Extension = "gof2quest";
        public const string Folder = "events";

        /// <summary>Its file name (without .gof2quest): the name startquest, campaign.json's "quest" and "quest(name)" use.</summary>
        public string Name { get; set; }
        public string Ref => "quest:" + Name;

        public ProjectQuest(string name, JObject o) : base(o)
        {
            Name = name;
            json["format"] = "gof2quest";
            if (!(json["steps"] is JArray)) json["steps"] = new JArray();
        }

        public string Title { get => S("title"); set => SetS("title", value); }
        /// <summary>A bar mission (offered in the Space Lounges) instead of a quest (starts by itself).</summary>
        public bool IsMission { get => S("kind") == "mission"; set => json["kind"] = value ? "mission" : "quest"; }
        /// <summary>A quest's start condition: "" = at once, "never" = only when another starts it, else an expression.</summary>
        public string StartsWhen { get => S("startsWhen") ?? ""; set => SetS("startsWhen", value); }
        public int Reward { get => N("reward") ?? 0; set => SetN("reward", value <= 0 ? (int?)null : value); }
        public string Offer { get => Text("offer"); set => SetText("offer", value); }
        public string Client { get => S("client"); set => SetS("client", value); }
        public string Description { get => Text("description"); set => SetText("description", value); }

        /// <summary>The stations a bar mission is offered at (numbers or keys; empty = every station).</summary>
        public List<string> Stations
        {
            get => json["stations"] is JArray a ? a.Select(RefText).Where(x => x != null).ToList() : new List<string>();
            set { if (value == null || value.Count == 0) json.Remove("stations"); else json["stations"] = new JArray(value.Select(RefToken)); }
        }

        public JArray Steps => (JArray)json["steps"];
        public List<JObject> StepList => Steps.OfType<JObject>().ToList();

        public JObject AddStep(string type, int at = -1)
        {
            var o = new JObject { ["type"] = type };
            if (at < 0 || at >= Steps.Count) Steps.Add(o); else Steps.Insert(at, o);
            return o;
        }

        public void RemoveStep(JObject step) => step.Remove();

        public void MoveStep(JObject step, int delta)
        {
            int i = Steps.IndexOf(step), j = i + delta;
            if (i < 0 || j < 0 || j >= Steps.Count) return;
            step.Remove();
            Steps.Insert(j, step);
        }
    }
}
