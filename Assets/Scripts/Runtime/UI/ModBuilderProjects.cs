// ModBuilderProjects.cs
// Remake-only: the main menu's Mod Builder page (Mods > Mod Builder): the player's mod projects (Modding.ModProject,
// Application.persistentDataPath/ModProjects). On the left the projects (the newest first) under a "new project" row
// (a name and Create); on the right the selected project: its name, author, version and description (saved as they are
// typed), what it holds by kind of content (Ships opens the hangar editor; the other kinds show "Coming soon" for now),
// and Open in hangar, Export as mod (zips it into the Mods folder, asks before replacing an installed mod with its id),
// Open folder (desktop) and Delete (asks).
// Plain C#: MainMenu creates it in its panel host and hands it its sound, focus, dialog hooks and the opening of a project
// (Modding.ModBuilder.Begin, then the Station scene behind the menu's fade).

using System;
using System.Collections.Generic;
using System.Linq;
using GoF2Remake.Data;
using GoF2Remake.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public class ModBuilderProjects
    {
        public VisualElement Panel { get; }

        readonly Action<VisualElement> hookFocus;
        readonly Action click;
        readonly Action<ModProject> open;
        readonly Action<string, string, Action> ask;
        readonly Action<string, string> notice;
        readonly ScrollView list;
        readonly Label count, empty, note, title, meta;
        readonly VisualElement detail, content;
        readonly TextField newName, nameField, authorField, versionField, descField;
        readonly Button openButton, exportButton, deleteButton;
        List<ModProject> projects = new List<ModProject>();
        ModProject selected;
        bool filling;

        static string T(string key, string english) => Localization.Extra(key, english);

        /// <summary>The kinds of content a mod can hold, in the order the page shows them; only ships can be built so far.</summary>
        static readonly (string key, string english, string desc)[] Kinds =
        {
            ("mbShips", "Ships", "New ships from a .glb model, or changes to the game's ships: stats, hardpoints, test flights."),
            ("mbItems", "Items", "New weapons and equipment, or changes to the game's items."),
            ("mbFx", "Weapon fx", "Projectiles, muzzle flashes, impacts and shot sounds."),
            ("mbSystems", "Systems", "New star systems and stations on the map."),
            ("mbInteriors", "Hangars", "Custom hangar and Space Lounge rooms."),
            ("mbQuests", "Quests", "Event graphs: quests and missions with dialogue."),
            ("mbCharacters", "Characters", "Portraits and speakers for conversations."),
            ("mbCampaigns", "Campaigns", "A new game of your own: start, galaxy, ships."),
            ("mbAudio", "Audio", "Replace or add music, sound effects and voices."),
            ("mbSkins", "Skins", "New looks for the game's ships, stations and planets."),
            ("mbBlueprints", "Blueprints", "Ships and items built from ingredients."),
        };

        public ModBuilderProjects(VisualElement host, Action back, Action<VisualElement> hookFocus, Action click, Action<ModProject> open,
                                  Action<string, string, Action> ask, Action<string, string> notice)
        {
            this.hookFocus = hookFocus;
            this.click = click;
            this.open = open;
            this.ask = ask;
            this.notice = notice;
            Panel = new VisualElement { name = ModBuilder.ProjectsPanel };
            Panel.AddToClassList("panel");
            Panel.AddToClassList("mods-panel");
            Panel.usageHints = UsageHints.DynamicTransform;

            var titleRow = Add(Panel, new VisualElement(), "mods-title-row");
            Add(titleRow, new Label(T("mbTitle", "Mod Builder").ToUpperInvariant()), "panel-title", "gof-semibold");
            Add(titleRow, new Label(T("mpExperimental", "Experimental").ToUpperInvariant()), "mp-badge");
            Add(titleRow, new VisualElement(), "mp-title-spacer");
            count = Add(titleRow, new Label(), "mods-count");
            Add(Panel, new VisualElement(), "panel-accent");

            var body = Add(Panel, new VisualElement(), "mods-body");
            var left = Add(body, new VisualElement(), "mods-left");
            var newRow = Add(left, new VisualElement(), "mb-new-row");
            newName = Add(newRow, new TextField { maxLength = 40 }, "mb-field", "mb-new-name");
            newName.textEdition.placeholder = T("mbNewPlaceholder", "New project name");
            newName.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                Create();
            }, TrickleDown.TrickleDown);
            ActionButton(newRow, T("mbCreate", "Create"), Create, "mb-create");
            list = Add(left, new ScrollView(ScrollViewMode.Vertical), "mods-list");
            list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            new DragScroll(list);
            empty = Add(left, new Label(), "mods-empty", "mb-empty");

            detail = Add(body, new VisualElement(), "mods-detail");
            var detailScroll = Add(detail, new ScrollView(ScrollViewMode.Vertical), "mb-detail-scroll");
            detailScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            detailScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            title = Add(detailScroll, new Label(), "mods-name", "gof-semibold");
            meta = Add(detailScroll, new Label(), "mods-meta");
            var fields = Add(detailScroll, new VisualElement(), "mb-fields");
            nameField = Field(fields, T("mbName", "Name"), 60, v => { selected.Name = v.Trim().Length > 0 ? v.Trim() : selected.Id; title.text = selected.Name; });
            var pair = Add(fields, new VisualElement(), "mb-field-pair");
            authorField = Field(pair, T("mbAuthor", "Author"), 40, v => selected.Author = v.Trim());
            versionField = Field(pair, T("mbVersion", "Version"), 16, v => selected.Version = v.Trim().Length > 0 ? v.Trim() : "1.0");
            descField = Field(fields, T("mbDescription", "Description"), 600, v => selected.Description = v, true);
            Add(detailScroll, new Label(T("mbContent", "Content").ToUpperInvariant()), "mb-section", "gof-semibold");
            content = Add(detailScroll, new VisualElement(), "mb-kinds");

            var actions = Add(detail, new VisualElement(), "mods-actions", "mb-actions");
            openButton = ActionButton(actions, T("mbOpen", "Open in hangar"), () => { if (selected != null) { selected.Save(); open(selected); } }, "mods-toggle");
            exportButton = ActionButton(actions, T("mbExport", "Export as mod"), Export);
            if (!Application.isMobilePlatform)
                ActionButton(actions, T("mbFolder", "Open folder"), () => { if (selected != null) Application.OpenURL("file:///" + selected.Folder.Replace('\\', '/')); });
            deleteButton = ActionButton(actions, T("modsDelete", "Delete"), AskDelete, "mods-delete");

            var footer = Add(Panel, new VisualElement(), "mods-footer");
            note = Add(footer, new Label(), "mods-note");
            var backButton = Add(Panel, new Button(back) { text = "‹  " + Localization.Get(170).ToUpperInvariant() }, "menu-button", "back-button", "gof-semibold");
            hookFocus(backButton);
            host.Add(Panel);
        }

        static TE Add<TE>(VisualElement parent, TE e, params string[] classes) where TE : VisualElement
        {
            foreach (var c in classes) e.AddToClassList(c);
            parent.Add(e);
            return e;
        }

        Button ActionButton(VisualElement parent, string text, Action onClick, string extra = null)
        {
            var b = new Button(() => { click(); onClick(); }) { text = text.ToUpperInvariant() };
            b.AddToClassList("mods-action");
            if (extra != null) b.AddToClassList(extra);
            hookFocus(b);
            parent.Add(b);
            return b;
        }

        /// <summary>A labelled text field whose edits go straight into the selected project (saved when the page is left).</summary>
        TextField Field(VisualElement parent, string label, int max, Action<string> set, bool multiline = false)
        {
            var box = Add(parent, new VisualElement(), "mb-field-box");
            Add(box, new Label(label.ToUpperInvariant()), "mb-field-label");
            var f = Add(box, new TextField { maxLength = max, multiline = multiline }, "mb-field");
            if (multiline) f.AddToClassList("mb-field--multi");
            f.RegisterValueChangedCallback(e => { if (!filling && selected != null) { set(e.newValue); dirty = true; } });
            f.RegisterCallback<FocusOutEvent>(_ => SaveIfDirty());
            return f;
        }

        bool dirty;

        void SaveIfDirty()
        {
            if (!dirty || selected == null) return;
            dirty = false;
            selected.Save();
            RebuildRows();
        }

        /// <summary>Opening the page: the projects read again.</summary>
        public void Open()
        {
            string keep = selected?.Folder;
            projects = ModProject.List();
            selected = projects.FirstOrDefault(p => p.Folder == keep) ?? projects.FirstOrDefault();
            RebuildRows();
            Refresh();
        }

        /// <summary>Leaving the page: what was typed is kept.</summary>
        public void Close() => SaveIfDirty();

        void RebuildRows()
        {
            list.Clear();
            foreach (var p in projects)
            {
                var row = new Button(() => { click(); Select(p); });
                row.AddToClassList("mods-row");
                var thumb = Add(row, new VisualElement { pickingMode = PickingMode.Ignore }, "mods-thumb", "mb-thumb");
                var icon = ProjectIcon(p);
                if (icon != null) thumb.style.backgroundImage = new StyleBackground(icon);
                var text = Add(row, new VisualElement { pickingMode = PickingMode.Ignore }, "mods-row-text");
                Add(text, new Label(p.Name) { pickingMode = PickingMode.Ignore }, "mods-row-name");
                Add(text, new Label(Sub(p)) { pickingMode = PickingMode.Ignore }, "mods-row-sub");
                row.EnableInClassList("mods-row--selected", p == selected);
                int hoverFrame = -10;
                row.RegisterCallback<PointerEnterEvent>(_ => hoverFrame = Time.frameCount);
                hookFocus(row);
                row.RegisterCallback<FocusInEvent>(_ => { if (Time.frameCount - hoverFrame > 1) Select(p); });
                list.Add(row);
            }
            empty.text = T("mbEmpty", "No projects yet. Type a name above and press Create to start your first mod.");
            empty.style.display = projects.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            count.text = projects.Count == 1 ? T("mbCountOne", "1 project") : string.Format(T("mbCount", "{0} projects"), projects.Count);
            note.text = string.Format(T("mbNote", "Projects are kept in {0}. Export as mod puts a finished one into your Mods folder, where it is turned on in the Mod Browser."),
                                      ModProject.Root);
        }

        /// <summary>The first ship's shop icon (an original's, for an override), else none.</summary>
        static Texture2D ProjectIcon(ModProject p)
        {
            var s = p.Ships.FirstOrDefault(x => x.IsOverride);
            return s != null ? ItemInfo.ShipIcon(s.OverrideIndex) : null;
        }

        static string Sub(ModProject p)
        {
            int ships = p.Ships.Count;
            string what = ships == 1 ? T("mbOneShip", "1 ship") : string.Format(T("mbShipsN", "{0} ships"), ships);
            return $"{what}  ·  v{p.Version}  ·  {p.Modified.ToLocalTime():yyyy-MM-dd HH:mm}";
        }

        void Select(ModProject p)
        {
            if (selected == p) return;
            SaveIfDirty();
            selected = p;
            foreach (var row in list.Children())
                row.EnableInClassList("mods-row--selected", projects.IndexOf(p) == list.IndexOf(row));
            Refresh();
        }

        void Refresh()
        {
            detail.style.visibility = selected != null ? Visibility.Visible : Visibility.Hidden;
            if (selected == null) return;
            filling = true;
            title.text = selected.Name;
            meta.text = selected.Id + (selected.LoadError != null ? "\n✖ " + selected.LoadError : "");
            nameField.value = selected.Name;
            authorField.value = selected.Author;
            versionField.value = selected.Version;
            descField.value = selected.Description;
            filling = false;
            content.Clear();
            for (int i = 0; i < Kinds.Length; i++)
            {
                var (key, english, desc) = Kinds[i];
                bool ships = i == 0;
                var tile = new Button(() => { if (ships && selected != null) { click(); selected.Save(); open(selected); } });
                tile.AddToClassList("mb-kind");
                tile.SetEnabled(ships);
                if (!ships) tile.AddToClassList("mb-kind--soon");
                Add(tile, new Label(T(key, english).ToUpperInvariant()) { pickingMode = PickingMode.Ignore }, "mb-kind-title", "gof-semibold");
                string state = ships ? (selected.Ships.Count == 1 ? T("mbOneShip", "1 ship") : string.Format(T("mbShipsN", "{0} ships"), selected.Ships.Count))
                                     : T("mbSoon", "Coming soon");
                Add(tile, new Label(state.ToUpperInvariant()) { pickingMode = PickingMode.Ignore }, "mb-kind-state");
                tile.tooltip = T(key + "Desc", desc);
                if (ships) hookFocus(tile);
                content.Add(tile);
            }
            openButton.SetEnabled(selected.LoadError == null);
            deleteButton.SetEnabled(true);
            exportButton.SetEnabled(selected.LoadError == null);
        }

        void Create()
        {
            string name = newName.value?.Trim();
            if (string.IsNullOrEmpty(name)) { newName.Focus(); return; }
            ModProject p;
            try { p = ModProject.Create(name, GoF2Remake.Multiplayer.NetGame.PlayerName); }
            catch (Exception e) { notice(T("mbTitle", "Mod Builder"), string.Format(T("mbCreateFailed", "The project couldn't be created: {0}"), e.Message)); return; }
            newName.value = "";
            projects.Insert(0, p);
            selected = p;
            RebuildRows();
            Refresh();
        }

        void Export()
        {
            if (selected == null) return;
            SaveIfDirty();
            string heading = T("mbExport", "Export as mod");
            void Run()
            {
                string path = selected.Export(out string error);
                if (path == null) notice(heading, string.Format(T("mbExportFailed", "The mod couldn't be exported: {0}"), error));
                else notice(heading, string.Format(T("mbExported", "\"{0}\" is in your Mods folder:\n{1}\nTurn it on in the Mod Browser to play with it."), selected.Name, path));
            }
            if (selected.Ships.Count == 0) { notice(heading, T("mbExportEmpty", "This project has nothing in it yet. Open it in the hangar and add a ship first.")); return; }
            var old = selected.InstalledMod;
            if (old != null)
                ask(heading, string.Format(T("mbExportReplace", "A mod with the id \"{0}\" is already installed (\"{1}\" v{2}). Replace it with this project?"),
                                           selected.Id, old.Name, old.Version), Run);
            else Run();
        }

        void AskDelete()
        {
            if (selected == null) return;
            var p = selected;
            string heading = T("mbDeleteTitle", "Delete project");
            ask(heading, string.Format(T("mbDeleteText", "Delete the project \"{0}\" and all its files? This can't be undone. (A mod you exported from it stays installed.)"), p.Name), () =>
            {
                if (!p.Delete(out string error)) { notice(heading, string.Format(T("mbDeleteFailed", "The project couldn't be deleted: {0}"), error)); return; }
                projects.Remove(p);
                selected = projects.FirstOrDefault();
                RebuildRows();
                Refresh();
            });
        }
    }
}
