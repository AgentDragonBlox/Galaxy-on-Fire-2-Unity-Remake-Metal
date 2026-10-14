// ProjectContent.cs
// Remake-only: the Mod Builder's other kinds of content beside its ships (ModProject.Ships / ProjectShip), each one entry of
// the mod's own file kept as a JObject (fields the builder doesn't edit, from a hand edit, stay) with typed accessors:
//   ProjectItem       an items.json entry: a new item ("id" + "base") or a change to a game item ("override");
//   ProjectCharacter  a characters.json entry.
// The formats are Modding/README.md's (ModContent, ModCharacters read them). Plain C#.

using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoF2Remake.Modding
{
    /// <summary>A JObject entry with the accessors both kinds share.</summary>
    public abstract class ProjectEntry
    {
        protected readonly JObject json;
        protected ProjectEntry(JObject o) { json = o; }

        public JObject ToJson() => (JObject)json.DeepClone();

        protected string S(string k) => json[k] is JValue v && v.Type == JTokenType.String ? (string)v : null;
        protected int? N(string k) => json[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? Mathf.RoundToInt((float)v) : (int?)null;
        protected bool B(string k, bool d) => json[k] is JValue v && v.Type == JTokenType.Boolean ? (bool)v : d;
        protected void SetS(string k, string v) { if (string.IsNullOrEmpty(v)) json.Remove(k); else json[k] = v; }
        protected void SetN(string k, int? v) { if (v == null) json.Remove(k); else json[k] = v.Value; }
        /// <summary>A flag written only when it isn't the game's default.</summary>
        protected void SetB(string k, bool v, bool d) { if (v == d) json.Remove(k); else json[k] = v; }

        /// <summary>A text field: a string, or one per language ({"en": ..., "de": ...}). The builder edits the English one
        /// (else the only one); a per-language field keeps its other languages.</summary>
        protected string Text(string k)
        {
            if (json[k] is JValue v && v.Type == JTokenType.String) return (string)v;
            if (json[k] is JObject o) return (string)(o["en"] ?? o.Properties().FirstOrDefault()?.Value);
            return null;
        }

        protected void SetText(string k, string value)
        {
            if (json[k] is JObject o) { if (string.IsNullOrEmpty(value)) o.Remove("en"); else o["en"] = value; return; }
            SetS(k, value);
        }
    }

    /// <summary>One items.json entry of a project.</summary>
    public class ProjectItem : ProjectEntry
    {
        public ProjectItem(JObject o) : base(o) { }

        public bool IsOverride => json["override"] != null;
        /// <summary>The game item an override changes (-1: another mod's "mod:id").</summary>
        public int OverrideIndex => IsOverride && int.TryParse(json["override"].ToString(), out int i) ? i : -1;
        public string LocalId => S("id");
        /// <summary>A new item's base: the game item it is a copy of (-1: another mod's "mod:id").</summary>
        public int BaseIndex => json["base"] is JValue v && int.TryParse(v.ToString(), out int i) ? i : -1;
        /// <summary>The original item it looks and works like: an override's item, a new item's base.</summary>
        public int Original => IsOverride ? OverrideIndex : BaseIndex;
        /// <summary>builder.json's key for it.</summary>
        public string Ref => IsOverride ? "override:" + json["override"] : "id:" + LocalId;

        public string Name { get => Text("name"); set => SetText("name", value); }
        public string Description { get => Text("description"); set => SetText("description", value); }
        /// <summary>Its own shop icon (a 180 x 88 PNG in the project, frame included), null = the base item's.</summary>
        public string Icon { get => S("icon"); set => SetS("icon", value); }
        public int? TechLevel { get => N("techLevel"); set => SetN("techLevel", value); }
        public int? Occurrence { get => N("occurrence"); set => SetN("occurrence", value); }

        /// <summary>The price range: "minPrice" / "maxPrice", or the "price" shorthand (n, or [min, max]).</summary>
        public int? MinPrice => N("minPrice") ?? PriceShorthand(0);
        public int? MaxPrice => N("maxPrice") ?? PriceShorthand(1);

        int? PriceShorthand(int end)
        {
            if (json["price"] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)) return Mathf.RoundToInt((float)v);
            if (json["price"] is JArray a && a.Count >= 2) return Mathf.RoundToInt((float)a[end]);
            return null;
        }

        /// <summary>Sets the range (written as minPrice / maxPrice, the shorthand dropped).</summary>
        public void SetPrices(int? min, int? max)
        {
            json.Remove("price");
            SetN("minPrice", min);
            SetN("maxPrice", max);
        }

        JObject Stats
        {
            get
            {
                if (!(json["stats"] is JObject o)) json["stats"] = o = new JObject();
                return o;
            }
        }

        /// <summary>The value the entry gives an attribute (by any of its stat names, ItemStats), null = the base's.</summary>
        public int? Stat(int attribute)
        {
            if (!(json["stats"] is JObject o)) return null;
            foreach (var p in o.Properties())
                if (ItemStats.IdOf(p.Name) == attribute && p.Value is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float))
                    return Mathf.RoundToInt((float)v);
            return null;
        }

        /// <summary>Sets an attribute under its readable stat name (other names for it dropped); null = the base's again.</summary>
        public void SetStat(int attribute, string name, int? value)
        {
            var o = Stats;
            foreach (var p in o.Properties().ToList()) if (ItemStats.IdOf(p.Name) == attribute) p.Remove();
            if (value != null) o[name] = value.Value;
            if (!o.HasValues) json.Remove("stats");
        }

        /// <summary>fx "turret": the turret's own model (a GLB in the project, ModWeapons.BuildTurret), null = the base
        /// item's. Read from either form ("file" or { "model": ... }), written as the object; the rest of "fx" stays. The
        /// size, offset, muzzle and shot start apply to either model; a "turret" left empty is dropped.</summary>
        public string TurretModel
        {
            get => Turret is JValue v && v.Type == JTokenType.String ? (string)v : Turret is JObject o ? (string)o["model"] : null;
            set
            {
                var o = TurretObject();
                if (string.IsNullOrEmpty(value)) o.Remove("model"); else o["model"] = value;
                DropEmptyTurret();
            }
        }

        /// <summary>fx "turret" "muzzle" / "shotStart": game units along the gun node, null = the base item's.</summary>
        public float? TurretMuzzle { get => TurretNumber("muzzle"); set => SetTurretNumber("muzzle", value); }
        public float? TurretShotStart { get => TurretNumber("shotStart"); set => SetTurretNumber("shotStart", value); }
        /// <summary>fx "turret" "scale": the model's size factor, null = 1 (as made).</summary>
        public float? TurretScale { get => TurretNumber("scale"); set => SetTurretNumber("scale", value); }

        /// <summary>fx "turret" "offset": the model moved off its mount (game units: x toward the pilot's left, y up, z forward).</summary>
        public Vector3Int TurretOffset
        {
            get => Turret is JObject o && o["offset"] is JArray a && a.Count >= 3
                ? new Vector3Int(Mathf.RoundToInt((float)a[0]), Mathf.RoundToInt((float)a[1]), Mathf.RoundToInt((float)a[2])) : Vector3Int.zero;
            set
            {
                var o = TurretObject();
                if (value == Vector3Int.zero) o.Remove("offset"); else o["offset"] = new JArray(value.x, value.y, value.z);
                DropEmptyTurret();
            }
        }

        JToken Turret => json["fx"] is JObject fx ? fx["turret"] : null;

        float? TurretNumber(string k) =>
            Turret is JObject o && o[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (float)v : (float?)null;

        void SetTurretNumber(string k, float? value)
        {
            var o = TurretObject();
            if (value == null) o.Remove(k); else o[k] = value.Value;
            DropEmptyTurret();
        }

        void DropEmptyTurret()
        {
            if (Turret is JObject o && !o.HasValues) SetFx("turret", null);
        }

        /// <summary>fx "turret" as an object (a plain file name turned into { "model": file }).</summary>
        JObject TurretObject()
        {
            if (Turret is JObject o) return o;
            o = new JObject();
            if (Turret is JValue v && v.Type == JTokenType.String) o["model"] = (string)v;
            SetFx("turret", o);
            return o;
        }

        void SetFx(string k, JToken value)
        {
            if (!(json["fx"] is JObject fx))
            {
                if (value == null) return;
                json["fx"] = fx = new JObject();
            }
            if (value == null) fx.Remove(k); else fx[k] = value;
            if (!fx.HasValues) json.Remove("fx");
        }
    }

    /// <summary>One characters.json entry of a project.</summary>
    public class ProjectCharacter : ProjectEntry
    {
        public ProjectCharacter(JObject o) : base(o) { }

        public string Id => S("id");
        public string Ref => "id:" + Id;
        public string Name { get => Text("name"); set => SetText("name", value); }
        public string Portrait { get => S("portrait"); set => SetS("portrait", value); }
        /// <summary>The race word (terran, vossk, nivelian, midorian, pirate, void), null = none given.</summary>
        public string Race { get => S("race"); set => SetS("race", value); }
        public bool Male { get => !string.Equals(S("gender"), "female", System.StringComparison.OrdinalIgnoreCase); set => json["gender"] = value ? "male" : "female"; }
        public bool Mirrored { get => B("mirrored", false); set => SetB("mirrored", value, false); }
        public bool Frame { get => B("frame", true); set => SetB("frame", value, true); }
        public bool Background { get => B("background", true); set => SetB("background", value, true); }
        /// <summary>The story character it stands in for (a speaker's name or number), null = nobody.</summary>
        public string Replaces { get => json["replaces"]?.ToString(); set => SetS("replaces", value); }
    }
}
