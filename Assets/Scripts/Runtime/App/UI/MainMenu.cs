// MainMenu.cs
// Main menu controller (UI Toolkit). Follows the original flow (MTitle -> ModMainMenu -> MenuTouchWindow(0)):
//   1. Splash: FISHLABS then (instead of ABYSS ENGINE) "Made with Unity". In players this is Unity's own
//      splash screen (Player Settings); in the editor the menu shows the FISHLABS logo itself.
//   2. Title: the GoF2 logo fades in over the live 3D scene (3.9 s), "press any key" pulses under it.
//   3. Menu: Resume (only with a save), Start new game -> Select Campaign -> difficulty (Easy / Normal / Hard / Extreme: the PC version's four),
//      Load game (save slots, slot 0 = Auto-save), Options (Sound & Graphics, Controls, Language), About, Exit.
// Text comes from the original table (Localization, text IDs in comments). Sounds: Button_Push on focus
// changes, Button_Release on confirm, Message_Info_Screen for dialogs (FMOD events 124 / 123 / 126).
// Modern additions: keyboard/gamepad navigation, animated transitions, bloom/brightness options.

using System;
using System.Collections;
using System.Collections.Generic;
using GoF2Remake.Data;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PointerType = UnityEngine.UIElements.PointerType;

namespace GoF2Remake.UI
{
    [RequireComponent(typeof(PanelRenderer))]
    [DefaultExecutionOrder(-1000)]   // register for the UI load before PanelRenderer loads the UXML
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public class MainMenu : MonoBehaviour
    {
        [Header("Flow")]
        public bool showSplash = true;
        [Tooltip("Fallback scene for Leave() without a scene name.")]
        public string gameScene = "Space";
        /// <summary>The credit line under the menu and on the About page (the heart is the text icons' sprite, the version
        /// the build's date and time, BuildVersion).</summary>
        static string VersionText =>
            "Galaxy on Fire 2 Unity Remake created with <sprite=\"gof2_text_icons\" name=\"heart\"> by JoppieToppie  ·  " + BuildVersion.Text;

        [Tooltip("Editor only: pretend this build version (e.g. 2026.09.29.2200) so the update check runs; empty = no check.")]
        public string editorTestVersion = "";

        /// <summary>The version the update check compares with the newest release (the Editor's only if pretended).</summary>
        string CheckVersion => Application.isEditor ? editorTestVersion : BuildVersion.Text;

        [Header("Editor splash (players use Unity's splash screen with the same logos)")]
        [Tooltip("MTitle image 7001 (FISHLABS). Each logo: 1 s fade in, 2 s hold, 1 s fade out.")]
        public Texture2D[] editorSplashLogos;

        [Header("Audio")]
        public AudioSource musicSource;
        public AudioSource sfxSource;
        public AudioClip menuMusic;      // event 145 Space_NoCombat_Void
        public AudioClip buttonPush;     // event 124
        public AudioClip buttonRelease;  // event 123
        public AudioClip infoSound;      // event 126
        // Voice volume preview (remake: the original only previewed FX). Same lines in both voice banks;
        // German voices play with the German voice language (by default the German text, like the original's voice bank switch).
        public AudioSource voiceSource;
        public AudioClip[] voicePreviewEnglish;
        public AudioClip[] voicePreviewGerman;

        [Header("Localization (text_<code>.json)")]
        public string[] languageCodes = { "en" };
        public string[] languageNames = { "English" };
        public TextAsset[] languageTables;

        [Header("Post-processing")]
        [Tooltip("The menu's volume (bloom and brightness are applied to every global volume by Bootstrap).")]
        public Volume postVolume;

        enum MenuState { Splash, Title, Menu, Leaving }

        /// <summary>A panel to open as soon as the menu loads ("campaignPanel": the station menu's Start new game).</summary>
        public static string OpenPanelOnStart { get => Data.MenuRequest.OpenPanelOnStart; set => Data.MenuRequest.OpenPanelOnStart = value; }

        VisualElement root, logo, splash, splashLogo, fade, dialog, mainColumn, mainButtons;
        Label pressAnyKey, versionLabel, hintLabel;
        int escFrame = -10, cancelFrame = -10;
        VisualElement menuPrompts;

        /// <summary>The main menu's prompts: select and confirm, back with a panel open, a value and the tabs on Options.</summary>
        MenuKeys.Rows MenuPromptRows()
        {
            var rows = MenuKeys.Rows.Select | MenuKeys.Rows.Confirm;
            if (openPanel == null) return rows;
            rows |= MenuKeys.Rows.Back;
            if (panels.TryGetValue("optionsPanel", out var options) && openPanel == options) rows |= MenuKeys.Rows.Change | MenuKeys.Rows.Tabs;
            return rows;
        }

        Button resumeButton, newGameButton, multiplayerButton, loadButton, optionsButton, modsButton, aboutButton, debugButton, exitButton;
        ModBrowser modBrowser;
        VisualElement updateRow;
        Button updateButton;
        readonly Dictionary<string, VisualElement> panels = new Dictionary<string, VisualElement>();
        VisualElement openPanel;
        readonly List<OptionControl> optionControls = new List<OptionControl>();
        Action dialogYes, dialogNo, dialogAlt;   // dialogAlt: the third button, only for ShowResetChoice
        Func<bool> dialogCheck;   // the Yes button closes the dialog only when this passes (null = always)
        TextField dialogField;
        MenuState screen = MenuState.Splash;
        bool skipRequested;
        Campaign pendingCampaign;
        /// <summary>Remake mods: the mod campaign picked (null: one of the three GoF2 campaigns).</summary>
        Modding.ModCampaigns.Def pendingModCampaign;

        /// <summary>A mod's card, like the campaign cards (290 x 448 art, the hover art fading in while selected, scaled with
        /// them): its art cropped to the card, and unless the art has its own title, the name on a plate at the foot.</summary>
        static Button ModCard(Texture2D art, Texture2D hover, bool showTitle, string title, string subtitle)
        {
            var b = new Button();
            b.AddToClassList("campaign-card");
            b.AddToClassList("mod-card");
            var a = new VisualElement { pickingMode = PickingMode.Ignore };
            a.AddToClassList("card-art");
            a.AddToClassList("mod-card__art");
            if (art != null) a.style.backgroundImage = new StyleBackground(art);
            b.Add(a);
            if (hover != null)
            {
                var h = new VisualElement { pickingMode = PickingMode.Ignore };
                h.AddToClassList("card-art");
                h.AddToClassList("card-art-hover");
                h.AddToClassList("mod-card__art");
                h.style.backgroundImage = new StyleBackground(hover);
                b.Add(h);
            }
            if (showTitle || art == null)
            {
                var plate = new VisualElement { pickingMode = PickingMode.Ignore };
                plate.AddToClassList("mod-card__plate");
                var t = new Label(title.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                t.AddToClassList("mod-card__title");
                t.AddToClassList("gof-semibold");
                plate.Add(t);
                if (!string.IsNullOrEmpty(subtitle))
                {
                    var st = new Label(subtitle) { pickingMode = PickingMode.Ignore };
                    st.AddToClassList("mod-card__sub");
                    plate.Add(st);
                }
                b.Add(plate);
            }
            return b;
        }

        ScrollView campaignCardScroll;

        /// <summary>The mods' campaigns (Modding.ModCampaigns) as cards after the three campaign cards, picked the same way;
        /// built anew each time the panel opens (the mods change in the menu). Four or five cards shrink to fit the row, more
        /// scroll sideways.</summary>
        void RefreshModCampaigns()
        {
            var panel = panels["campaignPanel"];
            var row = panel.Q(className: "card-row");
            if (row == null) return;
            if (campaignCardScroll == null || !panel.Contains(campaignCardScroll))   // made again after a UI reload
            {
                // The row goes into a sideways scroll view (it only scrolls with more than five cards).
                campaignCardScroll = new ScrollView(ScrollViewMode.Horizontal);
                campaignCardScroll.AddToClassList("card-scroll");
                campaignCardScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                campaignCardScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                row.parent.Insert(row.parent.IndexOf(row), campaignCardScroll);
                campaignCardScroll.Add(row);
                new DragScroll(campaignCardScroll);   // a drag scrolls the cards sideways
            }
            row.Query(className: "mod-card").ForEach(e => e.RemoveFromHierarchy());
            var all = Modding.ModCampaigns.All();
            foreach (var c in all)
            {
                var def = c;
                var b = ModCard(Modding.ModCampaigns.Image(def), Modding.ModCampaigns.ImageHover(def), def.showTitle, def.Name,
                                string.Format(Localization.Extra("modCampaignBy", "a mod: {0}"), def.mod.Name));
                b.tooltip = def.Description;
                b.RegisterCallback<PointerDownEvent>(_ => Play(buttonPush), TrickleDown.TrickleDown);
                b.clicked += () => { Play(buttonRelease); PickModCampaign(def); };
                b.RegisterCallback<FocusInEvent>(_ => campaignCardScroll.ScrollTo(b));
                HookFocusSound(b);
                row.Add(b);
            }
            int n = 3 + all.Count;
            row.EnableInClassList("card-row--4", n == 4);
            row.EnableInClassList("card-row--many", n >= 5);
        }

        ScrollView modOptionCards;
        Label modOptionHint;

        /// <summary>Remake mods: the mods' new-game options (Modding.ModGameOptions) as cards like the campaign cards, in a
        /// column beside the game options' toggles (the panel widens for them); a card toggles its option for this new game
        /// (remembered for the next one).</summary>
        void RefreshModOptions()
        {
            var panel = panels["gameOptionsPanel"];
            if (modOptionCards == null || !panel.Contains(modOptionCards))   // made again after a UI reload
            {
                // The toggles into a left column, the cards in a right one that scrolls sideways; Start under both.
                var body = new VisualElement();
                body.AddToClassList("game-options-body");
                var left = new VisualElement();
                left.AddToClassList("game-options-left");
                var anchor = panel.Q("gameOptionsStart");
                anchor.parent.Insert(anchor.parent.IndexOf(anchor), body);
                foreach (var name in new[] { "kaamoToggle", "hardcoreToggle", "tutorialToggle", "capitalToggle" })
                    if (panel.Q(name) is VisualElement e) left.Add(e);
                body.Add(left);
                var right = new VisualElement();
                right.AddToClassList("mod-option-column");
                modOptionCards = new ScrollView(ScrollViewMode.Horizontal);
                modOptionCards.AddToClassList("mod-option-cards");
                modOptionCards.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                modOptionCards.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                modOptionCards.mouseWheelScrollSize = 120f;
                right.Add(modOptionCards);
                modOptionHint = new Label { pickingMode = PickingMode.Ignore };
                modOptionHint.AddToClassList("mod-option-hint");
                right.Add(modOptionHint);
                body.Add(right);
                new DragScroll(modOptionCards);   // a drag (mouse or finger) scrolls the cards sideways
            }
            modOptionCards.Clear();
            var all = Modding.ModGameOptions.All();
            modOptionCards.parent.style.display = all.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            panel.EnableInClassList("panel--wide", all.Count > 0);
            // Two cards show at a time; more scroll sideways (a drag, the wheel, or the focus moving onto one).
            modOptionHint.style.display = all.Count > 2 ? DisplayStyle.Flex : DisplayStyle.None;
            modOptionHint.text = string.Format(Localization.Extra("modOptionsMore", "{0} options  ·  scroll for more"), all.Count);
            foreach (var o in all)
            {
                var def = o;
                var b = ModCard(Modding.ModGameOptions.Image(def), Modding.ModGameOptions.ImageHover(def), def.showTitle, def.Name,
                                string.Format(Localization.Extra("modCampaignBy", "a mod: {0}"), def.mod.Name));
                b.AddToClassList("mod-option-card");
                b.tooltip = def.Description;
                var badge = new Label { pickingMode = PickingMode.Ignore };
                badge.AddToClassList("mod-option-card__badge");
                badge.AddToClassList("gof-semibold");
                b.Add(badge);
                void Show()
                {
                    bool on = Modding.ModGameOptions.Chosen(def);
                    b.EnableInClassList("mod-option-card--on", on);
                    badge.text = (on ? Localization.Extra("modOptionOn", "On") : Localization.Extra("modOptionOff", "Off")).ToUpperInvariant();
                }
                Show();
                b.RegisterCallback<PointerDownEvent>(_ => Play(buttonPush), TrickleDown.TrickleDown);
                b.clicked += () => { Play(buttonRelease); Modding.ModGameOptions.SetChosen(def, !Modding.ModGameOptions.Chosen(def)); Show(); };
                b.RegisterCallback<FocusInEvent>(_ => modOptionCards.ScrollTo(b));
                HookFocusSound(b);
                modOptionCards.Add(b);
            }
        }

        void PickModCampaign(Modding.ModCampaigns.Def c)
        {
            pendingModCampaign = c;
            pendingCampaign = Campaign.GalaxyOnFire2;
            pendingStartIndex = -1;
            OpenPanel("difficultyPanel");
        }
        IVisualElementScheduledItem pulse;
        IDisposable anyKey;
        PanelSettings runtimePanel;
        PanelRenderer panelRenderer;
        bool started;
        int voicePreviewIndex;
        bool touchMode;   // last input was a finger: no hover styles, no focus highlight (see SetTouchMode)
        VisualElement safeArea;
        Vector2Int lastScreen;
        Rect lastSafeArea;
        int lastLayout = -1;

        // ---- setup ----------------------------------------------------------------------------

        void OnEnable()
        {
            if (GoF2Remake.Multiplayer.DedicatedServer.ShutOff(gameObject.scene)) return;   // a dedicated server has no menu
            // Per-instance panel settings: scaling is adapted to the screen shape (see UpdateLayout).
            panelRenderer = GetComponent<PanelRenderer>();
            if (!started) touchMode = Application.isMobilePlatform;
            Settings.Changed += ApplySettings;
            InputMode.Changed -= UpdatePressAnyKey;
            InputMode.Changed += UpdatePressAnyKey;
            UpdateCheck.Changed -= RefreshUpdateButton;
            UpdateCheck.Changed += RefreshUpdateButton;
            // PanelRenderer hands out the UI root when it (re)loads the UXML, including live reloads.
            // Register first: assigning the panel settings below reloads the UI.
            panelRenderer.RegisterUIReloadCallback(OnUIReload);
            if (runtimePanel == null && panelRenderer.panelSettings != null)
            {
                runtimePanel = Instantiate(panelRenderer.panelSettings);
                panelRenderer.panelSettings = runtimePanel;
            }
        }

        void OnUIReload(PanelRenderer renderer, VisualElement rootElement, int version)
        {
            root = rootElement;
            ScrollBars.Watch(rootElement);   // Modified: the hangar's scrollbar on every scroll view that needs one
            root.style.flexGrow = 1;   // the default theme would stretch the document root; ours is custom
            root.EnableInClassList("can-hover", !touchMode);
            safeArea = root.Q("safeArea");
            LoadLanguage(Settings.Language);

            logo = root.Q("logo");
            logo.usageHints = UsageHints.DynamicTransform;
            logo.RegisterCallback<GeometryChangedEvent>(_ => PlaceTitleLogo());
            splash = root.Q("splash");
            splashLogo = root.Q("splashLogo");
            fade = root.Q("fade");
            // Remake mods: the loading screen while the mods load (on the startup splash, and on the fade before a game).
            splashLoading = new ModLoadingView(splash);
            leaveLoading = new ModLoadingView(fade);
            dialog = root.Q("dialog");
            dialogField = root.Q<TextField>("dialogField");
            if (dialogField != null)
            {
                dialogField.maxLength = GoF2Remake.Multiplayer.NetGame.MaxNameLength;
                dialogField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                    e.StopPropagation();
                    ConfirmDialog();
                }, TrickleDown.TrickleDown);
            }
            mainColumn = root.Q("mainColumn");
            mainButtons = root.Q("mainButtons");
            pressAnyKey = root.Q<Label>("pressAnyKey");
            versionLabel = root.Q<Label>("versionLabel");
            hintLabel = root.Q<Label>("hintLabel");
            // Rework (docs/UI-DESIGN.md "Button prompts"): the keys as a prompt row at the footer's right, for the keyboard or
            // the controller in use (none on touch), following the open panel; the top-right "i" that held them went at the
            // user's request (2026-10-22).
            hintLabel.style.display = DisplayStyle.None;
            // Each UI reload builds a new tree: a new bar in its footer (the old one went with the old tree).
            menuPrompts = MenuKeys.Bar(MenuPromptRows);
            menuPrompts.AddToClassList("prompt-bar--footer");
            hintLabel.parent.Add(menuPrompts);

            resumeButton = Bind("resumeButton", () => LoadSlot(SaveGame.MostRecentSlot()));
            newGameButton = Bind("newGameButton", () => OpenPanel("campaignPanel"));
            multiplayerButton = Bind("multiplayerButton", OpenMultiplayer);
            loadButton = Bind("loadButton", () => { SyncSaveFolder(true); BuildSlots(); OpenPanel("loadPanel"); });
            // Remake: every save to one file and back (SaveTransfer), under the Load game list; main menu only (no game is
            // loaded here). After the click: the system's file dialog is modal.
            Bind("exportSavesButton", () => root.schedule.Execute(ExportSaves).ExecuteLater(1));
            Bind("importSavesButton", () => root.schedule.Execute(PickImport).ExecuteLater(1));
            // Remake: a save of the original game in, every slot out as the original's files (OriginalSaveTransfer).
            Bind("importOriginalButton", () => root.schedule.Execute(PickOriginalImport).ExecuteLater(1));
            Bind("exportOriginalButton", () => root.schedule.Execute(AskOriginalExport).ExecuteLater(1));
            optionsButton = Bind("optionsButton", () =>
            {
                // The rows follow what is known now (DLSS / FSR only once URP has made its pipeline, after these rows were built).
                foreach (var c in optionControls) c.Refresh();
                OpenPanel("optionsPanel");
                SelectTab(OptionPages[0].page);
            });
            // Remake: Mods opens a small hub, the Mod Browser (ModBrowser) and the Mod Builder (ModBuilderProjects).
            modsButton = Bind("modsButton", () => OpenPanel(panels.ContainsKey("modsHubPanel") ? "modsHubPanel" : "modsPanel"));
            aboutButton = Bind("aboutButton", () => OpenPanel("aboutPanel"));
            debugButton = Bind("debugButton", OpenDebug);
            UpdateDebugButton();
            exitButton = Bind("exitButton", () => ShowDialog(Localization.Get(390), Localization.Get(53), Quit));
            // Remake: the chosen save folder's newer saves in first (SaveFolder; a no-op without one), then Resume's test.
            OptionsCatalog.SaveFolderPressed = ChooseSaveFolder;
            SyncSaveFolder(true);
            resumeButton.EnableInClassList("menu-button--gone", SaveGame.MostRecentSlot() < 0);   // only with a save
            UpdateColumnFit();
            updateRow = root.Q("updateRow");
            updateButton = Bind("updateButton", () => Application.OpenURL(UpdateCheck.ReleaseUrl ?? UpdateCheck.ReleasesPage));

            foreach (var n in new[] { "campaignPanel", "difficultyPanel", "economyPanel", "gameOptionsPanel", "loadPanel", "optionsPanel", "aboutPanel", "multiplayerPanel" })
            {
                panels[n] = root.Q(n);
                panels[n].usageHints = UsageHints.DynamicTransform;
            }
            foreach (var n in new[] { "aboutScroll", "slotList" })
            {
                var sv = root.Q<ScrollView>(n);
                sv.mode = ScrollViewMode.Vertical;
                sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;     // drag / wheel / focus scrolling instead
                sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                new DragScroll(sv);
            }
            // The option pages and the host card's settings scroll by wheel, touch and focus only (a drag would fight the
            // sliders, and the host card's address rows copy on a tap).
            var noDragScrolls = new List<string> { "mpHostScroll" };
            foreach (var (_, p) in OptionPages) noDragScrolls.Add(p);
            foreach (var pg in noDragScrolls)
            {
                if (!(root.Q(pg) is ScrollView sv)) continue;
                sv.mode = ScrollViewMode.Vertical;
                sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }
            foreach (var n in new[] { "campaignBack", "difficultyBack", "economyBack", "gameOptionsBack", "loadBack", "optionsBack", "aboutBack", "multiplayerBack" })
            {
                var b = root.Q<Button>(n);
                b.clicked += Back;   // Back() plays the release sound itself (also used by Esc)
                HookFocusSound(b);
            }

            Bind("cardGof2", () => PickCampaign(Campaign.GalaxyOnFire2));
            Bind("cardValkyrie", () => PickCampaign(Campaign.Valkyrie));
            Bind("cardSupernova", () => PickCampaign(Campaign.Supernova));
            BuildDebugPanel();
            UpdateDebugButton();
            // The mod browser (remake, ModBrowser): built in code next to the other panels.
            if (root.Q("panelHost") is VisualElement modHost)
            {
                modBrowser = new ModBrowser(modHost, Back, HookFocusSound, () => Play(buttonRelease), () => StartCoroutine(RebuildModCache()),
                                            ShowDialog, ShowNotice);
                panels["modsPanel"] = modBrowser.Panel;
                // The Mod Builder's projects (remake, Modding.ModBuilder): opening one starts its hangar behind the fade.
                modProjects = new ModBuilderProjects(modHost, Back, HookFocusSound, () => Play(buttonRelease), OpenModProject, ShowDialog, ShowNotice);
                panels[Modding.ModBuilder.ProjectsPanel] = modProjects.Panel;
                // The Mod Browser's Edit in Mod Builder: the Mod Builder's page, making a project of that mod.
                modBrowser.EditInBuilder = mod =>
                {
                    OpenPanel(Modding.ModBuilder.ProjectsPanel);
                    modProjects.ImportInstalled(mod);
                };
                panels["modsHubPanel"] = BuildModsHub(modHost);
            }
            SetupMultiplayerPanel();
            Bind("easyButton", () => PickDifficulty(Session.DifficultyEasy));
            Bind("normalButton", () => PickDifficulty(Session.DifficultyNormal));
            Bind("hardButton", () => PickDifficulty(Session.DifficultyHard));
            Bind("extremeButton", () => ShowDialog(Localization.Get(25), Localization.Get(26),
                () => PickDifficulty(Session.DifficultyExtreme)));
            Bind("economyDefaultButton", () => PickEconomy(Economy.Default));
            Bind("economyAndroidButton", () => PickEconomy(Economy.Android));
            Bind("gameOptionsStart", () => BeginGame(pendingEconomy));
            Bind("tutorialToggle", () => { Settings.TutorialHints = !Settings.TutorialHints; RefreshTutorialToggle(); });
            Bind("capitalToggle", () => { Settings.CapitalShips = !Settings.CapitalShips; RefreshCapitalToggle(); });
            Bind("kaamoToggle", () => { KaamoFromStart = !KaamoFromStart; RefreshKaamoToggle(); });
            Bind("hardcoreToggle", () => { hardcoreNext = !hardcoreNext; RefreshHardcoreToggle(); });
            Bind("ngPlusToggle", () => { newGamePlus = !newGamePlus && ngPlusSave != null; RefreshNgPlus(false); });
            // The Supernova Challenge (293, SupernovaChallenge): its description (298) asks "Start Supernova Challenge?".
            Bind("challengeButton", () => ShowDialog(Localization.Get(293), Localization.Get(298).Replace("\r\n", "\n"),
                                                     () => StartCoroutine(Leave(SupernovaChallenge.Start(false)))));
            Bind("dialogYes", ConfirmDialog);
            Bind("dialogNo", () => { var a = dialogNo; CloseDialog(); a?.Invoke(); });
            Bind("dialogAlt", () => { var a = dialogAlt; CloseDialog(); a?.Invoke(); });

            foreach (var (tab, pg) in OptionPages) Bind(tab, () => SelectTab(pg));
            Bind("optionsDefaults", ShowResetChoice);   // 497: asks first (this tab or every tab)
            SetupOptions();

            root.RegisterCallback<NavigationCancelEvent>(_ =>
            {
                cancelFrame = Time.frameCount;
                if (ChoicePopup.IsOpen) { ChoicePopup.Close(); return; }   // an option's open list closes first
                Back();
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(e => { if (e.pointerType == PointerType.mouse) pointerDriven = true; }, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(e =>
            {
                // Space typed into a field is a space, not a press of the menu's button.
                if (TextFieldKeys.IsTyping(e, root.focusController?.focusedElement as VisualElement)) { e.StopPropagation(); root.focusController?.IgnoreEvent(e); return; }
                // A choice keeps its value (left / right pick it; A cycled it, a player's report).
                if (screen == MenuState.Menu && root.focusController?.focusedElement is ChoiceRow) { e.StopPropagation(); root.focusController?.IgnoreEvent(e); return; }
                // A on an Options tab opens it and goes into it (it only selected the tab).
                if (screen == MenuState.Menu && root.focusController?.focusedElement is Button tab && OptionsTabPage(tab) is string tabPage
                    && !dialog.ClassListContains("dialog-backdrop--shown"))
                {
                    Play(buttonRelease);
                    SelectTab(tabPage);
                    FocusPageStart(tab);
                    e.StopPropagation();
                    root.focusController?.IgnoreEvent(e);
                }
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(e => { DragScroll.NotePointer(); if (e.pointerType == PointerType.mouse) SetTouchMode(false); }, TrickleDown.TrickleDown);
            root.RegisterCallback<WheelEvent>(_ => DragScroll.NotePointer(), TrickleDown.TrickleDown);
            // Keys / controller: the focused row scrolls into view (not for the pointer's own focus: DragScroll.PointerActive).
            root.RegisterCallback<FocusInEvent>(e => { if (e.target is VisualElement v && !DragScroll.PointerActive) EnsureVisible(v); });

            ApplySettings();

            // Rework (docs/UI-DESIGN.md "Button prompts"): with a controller, B on every Back and the dialog's No / Cancel,
            // A on the focused dialog button (InputGlyph.PadPrompt; their texts go through InputGlyph.SetText from here).
            root.Query<Button>(className: "back-button").ForEach(b => { if (b.name != "optionsDefaults") InputGlyph.PadPrompt(b, PadButton.B); });
            InputGlyph.PadPrompt(root.Q<Button>("dialogYes"), PadButton.A, true);
            InputGlyph.PadPrompt(root.Q<Button>("dialogAlt"), PadButton.A, true);
            InputGlyph.PadPrompt(root.Q<Button>("dialogNo"), PadButton.B);
            PadKeyboard.Attach(root);   // a controller's keyboard for the text fields (docs/UI-DESIGN.md "Text entry")
            KeyboardLift.Watch(root);   // phones: the focused text field kept above the on-screen keyboard
            UiFocus.SkipScrollBars(root);   // no stop on a scroll bar (docs/UI-DESIGN.md "Focus")
            RefreshTexts();
            lastScreen = Vector2Int.zero;
            UpdateLayout();
            if (!started)
            {
                started = true;
                // The main story's ending plays over this scene's backdrop (ModStation's CutScene(2)), then the station.
                if (Session.EndingPending) { EndingCredits.Begin(gameObject, root, musicSource); return; }
                foreach (var b in mainButtons.Query<Button>().ToList()) b.AddToClassList("menu-button--hidden");
                StartCoroutine(Run());
            }
            else RestoreState();
        }

        /// <summary>After a live UI reload the tree is new: put it back in the current flow state.</summary>
        void RestoreState()
        {
            if (screen == MenuState.Splash) return;
            splash.AddToClassList("splash--gone");
            splash.AddToClassList("splash--removed");
            logo.RemoveFromClassList("logo--instant");
            if (screen == MenuState.Title) { logo.AddToClassList("logo--title-visible"); return; }
            logo.AddToClassList("logo--menu");
            pressAnyKey.AddToClassList("press-any-key--hidden");
            root.AddToClassList("menu-root--menu");
            FocusFirst(mainButtons);
            RefreshUpdateButton();
        }

        [Tooltip("Force the phone layout (for testing in the editor).")]
        public bool simulatePhone;

        /// <summary>The loaded UI root (PanelRenderer has no rootVisualElement; set by the reload callback).</summary>
        public VisualElement Root => root;

        /// <summary>Size the panel renders at: the screen, or its target texture when rendering off-screen.</summary>
        Vector2Int ScreenSize()
        {
            var rt = runtimePanel != null ? runtimePanel.targetTexture : null;
            return rt != null ? new Vector2Int(rt.width, rt.height) : new Vector2Int(Screen.width, Screen.height);
        }

#if UNITY_WSA
        // UWP (#20): the Xbox's system keyboard closed with B counts as Cancel, and the text field then puts back the text
        // it had before (the pilot name typed in vanished). The text the keyboard had typed while it was open is kept.
        TextField keyboardField;
        string keyboardText;
        bool keyboardWasOpen;

        void KeepKeyboardText()
        {
            bool open = TouchScreenKeyboard.visible;
            if (open && root.focusController?.focusedElement is TextField f) { keyboardField = f; keyboardText = f.value; }
            else if (!open && keyboardWasOpen && keyboardField != null && keyboardText != null)
            {
                if (keyboardField.value != keyboardText) keyboardField.value = keyboardText;
                // The pilot name prompt: a name typed and the keyboard closed is the answer (#20: only Start kept it, and
                // B left the prompt without the name, asking again and again).
                if (keyboardField == dialogField && dialog.ClassListContains("dialog-backdrop--shown")
                    && GoF2Remake.Multiplayer.NetGame.Clean(keyboardText).Length > 0)
                    root.schedule.Execute(ConfirmDialog);
            }
            keyboardWasOpen = open;
        }
#endif

        void Update()
        {
            if (root == null) return;
            SyncHover();
            // The layout first: the screen and the options change whatever has the keys (a capture, the controller's keyboard).
            if (lastScreen != ScreenSize() || lastSafeArea != Screen.safeArea || lastLayout != UiLayout.Revision) UpdateLayout();
            if (GoF2Remake.Flight.GameControls.BlocksMenus) return;
#if UNITY_WSA
            KeepKeyboardText();
#endif
            DpadTapNavigation.Pump(root);   // D-pad taps the panel's own navigation drops (the Steam controller)
            // Remake: the debug panel (F10, LB + RB or three fingers held for a second, or five taps on the version text).
            if (screen == MenuState.Menu && Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame) OpenDebug();
            EscBack();
            var pad = Gamepad.current;
            if (screen == MenuState.Menu && ((pad != null && pad.leftShoulder.isPressed && pad.rightShoulder.isPressed) || FingersDown() >= 3))
            {
                debugHoldTime += Time.unscaledDeltaTime;
                if (debugHoldTime >= 1f && debugHoldTime - Time.unscaledDeltaTime < 1f) OpenDebug();
            }
            else debugHoldTime = 0f;
            // Remake mods: the controller's X turns the selected mod on or off while the mod browser is open (no dialog over it).
            if (pad != null && pad.buttonWest.wasPressedThisFrame && modBrowser != null && openPanel == modBrowser.Panel
                && !dialog.ClassListContains("dialog-backdrop--shown"))
                modBrowser.ToggleSelected();
            UpdateOptionTabs(pad);
            KeepControllerFocus();
        }

        /// <summary>#74: the panel only sends a controller's navigation to a focused element, and nothing focuses one when the
        /// controller takes over from touch (Android starts in touch mode) or a closed panel took the focus with it: the
        /// D-pad then did nothing until a touch. With the controller in use and nothing focused, the open dialog's Yes, the
        /// open panel's first item or the main column's.</summary>
        void KeepControllerFocus()
        {
            if (screen != MenuState.Menu || root.focusController == null || root.focusController.focusedElement != null) return;
            // Also on a navigation press outside the controller input mode (Android: B arrives as Escape, some pads send the
            // D-pad as arrow keys; Discord report 2026-10-18).
            if (InputMode.Current != InputKind.Gamepad && !MenuFocus.NavPressed(Gamepad.current, Keyboard.current)) return;
            pointerDriven = false;   // Modified: the keys took over (Select marks again)
            if ((focusRetry -= Time.unscaledDeltaTime) > 0f) return;   // a page with nothing to focus: not every frame
            focusRetry = 0.25f;
            SetTouchMode(false);
            if (dialog.ClassListContains("dialog-backdrop--shown")) Select(root.Q<Button>("dialogYes"));
            else FocusFirst(openPanel ?? mainButtons);
        }

        float focusRetry;

        static int FingersDown()
        {
            var ts = Touchscreen.current;
            if (ts == null) return 0;
            int n = 0;
            foreach (var t in ts.touches) if (t.press.isPressed) n++;
            return n;
        }

        // ---- aspect ratios ---------------------------------------------------------------------

        /// <summary>
        /// Landscape only. UiLayout scales the UI (a 1080-unit-high design, larger on phones, handhelds and TVs, never under
        /// 1280 units wide) and sets the USS layout classes; the safe area keeps it clear of cutouts, a TV's edges and,
        /// on an ultrawide screen, inside the interface width (Options > Graphics).
        /// </summary>
        void UpdateLayout()
        {
            lastScreen = ScreenSize();
            lastSafeArea = Screen.safeArea;
            lastLayout = UiLayout.Revision;
            bool offscreen = runtimePanel != null && runtimePanel.targetTexture != null;
            float w = Mathf.Max(1, lastScreen.x), h = Mathf.Max(1, lastScreen.y);
            // The scale and the layout classes (device, aspect, ui-large) by the shared model, docs/UI-DESIGN.md "Scaling".
            UiLayout.Configure(runtimePanel, root, w, h, simulatePhone);
            ApplySafeArea(w, h, offscreen);
        }

        /// <summary>Safe area insets (the notch / Dynamic Island, the home indicator, cutouts) as the safe area's margins,
        /// see SafeArea (its children are all absolutely placed: the padding it had before never moved them).</summary>
        void ApplySafeArea(float w, float h, bool offscreen) => SafeArea.Apply(root, safeArea, w, h, offscreen, SafeArea.Mode.Margins);

        void OnDestroy()
        {
            // The per-scene PanelSettings clone: left alive, every menu entry kept another UI Toolkit panel (its atlas and
            // GPU buffers) for the rest of the run.
            if (runtimePanel != null) Destroy(runtimePanel);
        }

        void OnDisable()
        {
            panelRenderer?.UnregisterUIReloadCallback(OnUIReload);
            Settings.Changed -= ApplySettings;
            InputMode.Changed -= UpdatePressAnyKey;
            UpdateCheck.Changed -= RefreshUpdateButton;
            if (OptionsCatalog.SaveFolderPressed == (Action)ChooseSaveFolder) OptionsCatalog.SaveFolderPressed = null;
            anyKey?.Dispose();
        }

        /// <summary>Modified: Esc goes back as the footer says. The panel only sends its Cancel to a focused element, and after a
        /// click outside the buttons (the mouse) nothing has the focus: Esc did nothing. A press the panel didn't turn into
        /// a Cancel within two frames goes back here (closing an option's open list first).</summary>
        void EscBack()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) escFrame = Time.frameCount;
            if (escFrame < 0 || Time.frameCount < escFrame + 2) return;
            bool handled = cancelFrame >= escFrame;
            escFrame = -10;
            if (handled || screen != MenuState.Menu) return;
            if (ChoicePopup.IsOpen) { ChoicePopup.Close(); return; }
            Back();
        }

        /// <summary>The title prompt follows the input scheme: any key on the keyboard, any button on a controller, a tap
        /// on touch.</summary>
        void UpdatePressAnyKey()
        {
            // The controller focus look (GoF2Common.uss .input-gamepad, like the station and the flight HUD).
            root?.EnableInClassList("input-gamepad", InputMode.Current == InputKind.Gamepad);
            root?.EnableInClassList("input-touch", InputMode.Current == InputKind.Touch);   // the key bindings' × stays shown (no hover)
            if (pressAnyKey == null) return;
            pressAnyKey.text = InputMode.Current switch
            {
                InputKind.Gamepad => Localization.Extra("pressAnyButton", "PRESS ANY BUTTON"),
                InputKind.Touch => Localization.Extra("tapToStart", "TAP TO START"),
                _ => Localization.Extra("pressAnyKey", "PRESS ANY KEY"),
            };
        }

        Button Bind(string name, Action onClick)
        {
            var b = root.Q<Button>(name);
            if (b == null) { Debug.LogWarning($"MainMenu: button '{name}' missing"); return null; }
            b.clicked += () => { Play(buttonRelease); onClick(); };
            HookFocusSound(b);
            return b;
        }

        void HookFocusSound(VisualElement e)
        {
            // Mouse only: a finger sliding over the UI must not drag the selection along with it.
            // Only when the mouse moved: a row that slides under a resting cursor as the list scrolls doesn't take the focus.
            e.RegisterCallback<PointerEnterEvent>(ev => { if (ev.pointerType == PointerType.mouse && InputMode.MouseMovedRecently && e.enabledInHierarchy && e.focusable) e.Focus(); });
            // Modified: with the mouse in use, marked only while the mouse is over it (it stayed marked after the mouse left,
            // and, already focused, made no sound when the mouse came back).
            e.RegisterCallback<PointerLeaveEvent>(ev => { if (ev.pointerType == PointerType.mouse && pointerDriven && InputMode.Current != InputKind.Gamepad && e.focusController?.focusedElement == e) e.Blur(); });
            // Modified: under the mouse only the main column's buttons and the tabs sound (UiHover.MenuTarget); the keys / a
            // controller sound on every move (they clear pointerDriven first: OnNavigate, KeepControllerFocus).
            e.RegisterCallback<FocusInEvent>(_ =>
            {
                if (screen != MenuState.Menu) return;
                if (pointerDriven && !(mainButtons != null && mainButtons.Contains(e)) && UiHover.MenuTarget(e) == null) return;
                Play(buttonPush, 0.45f);
            });
        }

        // Touch has no hover, and a highlight that follows the finger (or stays on whatever was last pressed)
        // looks broken. In touch mode the selection highlight is off: hover styles need the root's can-hover
        // class, presses don't move focus and menus don't pre-select their first item. A mouse or
        // keyboard/controller navigation switches back.
        void SetTouchMode(bool on)
        {
            if (root == null || on == touchMode && root.ClassListContains("can-hover") != on) return;
            touchMode = on;
            SyncHover();
            if (on && root.focusController?.focusedElement is VisualElement focused) focused.Blur();
        }

        /// <summary>Hover highlights only while the mouse is the thing in use (InputMode.MousePointing; see StationMenu).</summary>
        void SyncHover() => root?.EnableInClassList("can-hover", !touchMode && InputMode.MousePointing);

        void OnPointerDown(PointerDownEvent e)
        {
            DragScroll.NotePointer();
            if (e.pointerType == PointerType.mouse) { SetTouchMode(false); pointerDriven = true; return; }
            // Don't focus what the finger presses, except a text field (the address, the code, the debug search): it needs
            // the focus for the on-screen keyboard. Touch mode without the blur there: tapping the focused field again blurred
            // it first, which closed the keyboard.
            if (TextFieldKeys.InTextField(e.target)) { touchMode = true; root.EnableInClassList("can-hover", false); return; }
            SetTouchMode(true);
            root.focusController?.IgnoreEvent(e);
        }

        void Select(VisualElement e)
        {
            if (touchMode) return;
            // Modified: with the mouse in use a panel opens with nothing marked (its first button looked hovered); the keys
            // and a controller still get their selection (KeepControllerFocus, OnNavigate).
            if (pointerDriven && InputMode.Current != InputKind.Gamepad)
            {
                if (root?.focusController?.focusedElement is Button b) b.Blur();
                return;
            }
            e?.Focus();
        }

        /// <summary>Modified: the mouse moved or pressed last; the keys / a controller clear it.</summary>
        bool pointerDriven = true;

        // ---- flow ------------------------------------------------------------------------------

        /// <summary>Remake mods: a mod's menu theme that finished loading takes over from the original (ModMusic).</summary>
        void SwapModMusic()
        {
            if (this == null || musicSource == null || menuMusic == null || musicSource.clip != menuMusic) return;
            var mod = Modding.ModMusic.Replace(menuMusic);
            if (mod == menuMusic) return;
            musicSource.clip = mod;
            musicSource.Play();
        }

        ModLoadingView splashLoading, leaveLoading;

        /// <summary>Remake mods: waits on the loading screen in 'view' while the mods that are on load (at most 'limit' s).</summary>
        IEnumerator WaitForMods(ModLoadingView view, float limit)
        {
            if (!Modding.ModLoading.Busy) yield break;   // (this first check starts every loader)
            if (!Modding.ModLoading.HasWork)
            {
                // Nothing to load (no mods, or only quests): the loaders finish within a frame or two, without the screen.
                for (float t = 0f; t < limit && Modding.ModLoading.Busy; t += Time.unscaledDeltaTime) yield return null;
                yield break;
            }
            view.Show(true);
            for (float t = 0f; t < limit && Modding.ModLoading.Busy; t += Time.unscaledDeltaTime)
            {
                view.Update();
                yield return null;
            }
            view.Update();
            yield return new WaitForSecondsRealtime(0.35f);   // the full bar for a moment
            view.Show(false);
            yield return new WaitForSecondsRealtime(0.3f);
        }

        IEnumerator Run()
        {
            // Whatever the last scene left: never a muted listener here (a pause menu open when a multiplayer session
            // ended loads the menu without closing it), and a finished session's game is gone.
            AudioListener.pause = false;
            GoF2Remake.Multiplayer.NetGame.OnMainMenu();
            Modding.ModShips.Preload();   // the active mods' ship models, built while the menu shows
            Modding.ModStations.Preload();   // their station models and planet / sun textures
            Modding.ModWeapons.Preload();   // their weapons' own fx and sounds
            Modding.ModSounds.Preload();   // their sound effects
            Modding.ModTextures.Preload();   // their texture replacements (skins)
            Modding.ModCharacters.Preload();   // their characters' portraits
            Modding.ModMusic.Preload();   // and their music (a replaced menu theme swaps in when it has loaded)
            Modding.ModMusic.Changed -= SwapModMusic;
            Modding.ModMusic.Changed += SwapModMusic;
            if (musicSource != null && menuMusic != null)
            {
                musicSource.clip = Modding.ModMusic.Replace(menuMusic);
                musicSource.loop = true;
                musicSource.volume = 0f;
                musicSource.Play();      // MTitle starts the menu theme with the first logo
            }
            StartCoroutine(FadeMusic(Settings.MusicVolume, 3f));

            // The station menu's Start new game (MenuTouchWindow mode 2, 28): straight to the menu with that panel open.
            if (OpenPanelOnStart != null && panels.ContainsKey(OpenPanelOnStart))
            {
                string name = OpenPanelOnStart;
                OpenPanelOnStart = null;
                splash.AddToClassList("splash--gone");
                splash.AddToClassList("splash--removed");
                EnterMenu();
                // Out of a multiplayer session (the host left, the connection dropped): the reason, taken now (a -mpjoin
                // client's next try starts a new session at once, which clears the status).
                string reason = null;
                bool popup = name == "multiplayerPanel" && GoF2Remake.Multiplayer.NetGame.TakePopup(out reason);
                root.schedule.Execute(() =>
                {
                    OpenPanel(name);
                    if (popup) ShowNotice(Localization.Extra("multiplayer", "Multiplayer"), reason);
                }).ExecuteLater(400);
                if (GoF2Remake.Multiplayer.NetGame.AutoJoinAddress != null) StartCoroutine(AutoJoin(GoF2Remake.Multiplayer.NetGame.AutoJoinAddress));
                yield break;
            }
            OpenPanelOnStart = null;
            // Multiplayer testing (-mpjoin <address>, e.g. a second Windows player next to the Editor's host): straight to the
            // menu, then join.
            if (GoF2Remake.Multiplayer.NetGame.AutoJoinAddress != null || GoF2Remake.Multiplayer.NetGame.TestHost)
            {
                splash.AddToClassList("splash--gone");
                splash.AddToClassList("splash--removed");
                EnterMenu();
                root.schedule.Execute(() => OpenPanel("multiplayerPanel")).ExecuteLater(400);   // its status shows the tries
                if (GoF2Remake.Multiplayer.NetGame.AutoJoinAddress != null) StartCoroutine(AutoJoin(GoF2Remake.Multiplayer.NetGame.AutoJoinAddress));
                else StartCoroutine(TestHostSoon());   // -mphost (development builds)
                yield break;
            }
            WatchAnyKey();
            bool splashLogos = showSplash && Application.isEditor && editorSplashLogos != null;
            if (splashLogos)
            {
                foreach (var tex in editorSplashLogos)
                {
                    if (tex == null) continue;
                    skipRequested = false;
                    splashLogo.style.backgroundImage = new StyleBackground(tex);
                    splashLogo.AddToClassList("splash-logo--visible");
                    yield return Wait(3f);                        // 1 s fade in + 2 s hold
                    splashLogo.RemoveFromClassList("splash-logo--visible");
                    yield return Wait(1f);                        // 1 s fade out
                }
            }
            // Remake mods: everything they bring loads at once in the background since the menu opened; the black splash
            // shows the loading screen until it is done, then the title comes.
            yield return WaitForMods(splashLoading, 60f);
            splash.AddToClassList("splash--gone");
            screen = MenuState.Title;
            yield return new WaitForSeconds(0.2f);
            logo.AddToClassList("logo--title-visible");
            yield return new WaitForSeconds(splashLogos ? 1.5f : 0.3f);
            splash.AddToClassList("splash--removed");
            pulse = pressAnyKey.schedule.Execute(() => pressAnyKey.ToggleInClassList("press-any-key--on")).Every(1050);
            skipRequested = false;
            while (!skipRequested) yield return null;
            EnterMenu();
        }

        IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds && !skipRequested; t += Time.unscaledDeltaTime) yield return null;
        }

        void WatchAnyKey()
        {
            anyKey?.Dispose();
            anyKey = InputSystem.onAnyButtonPress.Call(_ => { if (screen == MenuState.Splash || screen == MenuState.Title) skipRequested = true; });
        }

        /// <summary>
        /// The logo lives at its menu spot (top-left); on the title screen it is moved to the centre and enlarged
        /// with translate + scale only, so the move to the menu is a cheap GPU transform animation.
        /// </summary>
        void PlaceTitleLogo()
        {
            if (screen == MenuState.Menu || screen == MenuState.Leaving || logo.parent == null) return;
            var p = logo.parent.layout;
            var el = logo.layout;
            if (el.width <= 0f || el.height <= 0f || p.width <= 0f) return;
            // logo_gof2_remake.png's own proportions (the remake's title logo; #67 redrew it at 953 x 360)
            var logoTex = logo.resolvedStyle.backgroundImage.texture;
            float aspect = logoTex != null && logoTex.height > 0 ? (float)logoTex.width / logoTex.height : 953f / 360f;
            float imgW = Mathf.Min(el.width, el.height * aspect);     // scale-to-fit, left aligned
            var imgCenter = new Vector2(el.x + imgW * 0.5f, el.y + el.height * 0.5f);
            float targetW = Mathf.Min(p.width * 0.56f, p.height * 0.34f * aspect);
            float k = targetW / imgW;
            var target = new Vector2(p.width * 0.5f, p.height * 0.41f);
            logo.style.transformOrigin = new TransformOrigin(Length.Pixels(imgW * 0.5f), Length.Percent(50f));
            logo.style.translate = new Translate(target.x - imgCenter.x, target.y - imgCenter.y);
            logo.style.scale = new Scale(new Vector2(k, k));
            // First placement happens without animation; transitions are enabled afterwards.
            if (logo.ClassListContains("logo--instant"))
                logo.schedule.Execute(() => logo.RemoveFromClassList("logo--instant")).ExecuteLater(50);
        }

        void EnterMenu()
        {
            screen = MenuState.Menu;
            anyKey?.Dispose();
            pulse?.Pause();
            pressAnyKey.AddToClassList("press-any-key--hidden");
            // Back to the logo's own (menu) placement: a transform-only transition, no relayout per frame.
            // Swap classes first and start the move once the styles are resolved again: a transition that starts
            // before that still uses the title fade's 3.9 s duration instead of the 0.9 s of .logo.
            logo.RemoveFromClassList("logo--title-visible");
            logo.AddToClassList("logo--menu");
            IVisualElementScheduledItem move = null;
            move = logo.schedule.Execute(() =>
            {
                foreach (var d in logo.resolvedStyle.transitionDuration)
                    if ((d.unit == TimeUnit.Millisecond ? d.value / 1000f : d.value) > 2f) return;
                logo.style.translate = StyleKeyword.Null;
                logo.style.scale = StyleKeyword.Null;
                move.Pause();
            }).Every(0);
            root.AddToClassList("menu-root--menu");
            mainButtons.AddToClassList("main-buttons--revealing");
            // Remake: is a newer release out (once per run; the button shows when the answer comes, RefreshUpdateButton)?
            UpdateCheck.Start(CheckVersion);
            RefreshUpdateButton();
            root.schedule.Execute(() =>
            {
                foreach (var b in mainButtons.Query<Button>().ToList()) b.RemoveFromClassList("menu-button--hidden");
                FocusFirst(mainButtons);
            }).ExecuteLater(200);
            root.schedule.Execute(() => mainButtons.RemoveFromClassList("main-buttons--revealing")).ExecuteLater(1200);
        }

        // ---- panels ----------------------------------------------------------------------------

        void OpenPanel(string name)
        {
            if (openPanel != null) HidePanel(openPanel);
            var p = panels[name];
            openPanel = p;
            p.AddToClassList("panel--shown");
            p.schedule.Execute(() => p.AddToClassList("panel--visible")).ExecuteLater(16);
            mainColumn.AddToClassList("main-column--dimmed");
            root.AddToClassList("menu-root--panel");
            root.schedule.Execute(() => MenuKeys.Refresh(menuPrompts));
            SetFocusable(mainButtons, false);
            RefreshUpdateButton();
            p.schedule.Execute(() => FocusFirst(p)).ExecuteLater(30);
            if (name == "campaignPanel") { RefreshNgPlus(true); RefreshModCampaigns(); RefreshChallenge(); }
            if (name == "modsPanel") modBrowser?.Open();
            if (name == Modding.ModBuilder.ProjectsPanel) modProjects?.Open();
            if (name == "multiplayerPanel")
            {
                RefreshAddresses();   // the adapters may have changed
                if (serverBrowse == null) serverBrowse = StartCoroutine(BrowseServers());
            }
        }

        void HidePanel(VisualElement p)
        {
            p.RemoveFromClassList("panel--visible");
            p.RemoveFromClassList("panel--shown");
        }

        void Back()
        {
            if (screen != MenuState.Menu) return;
            if (dialog.ClassListContains("dialog-backdrop--shown")) { var no = dialogNo; CloseDialog(); no?.Invoke(); return; }
            if (openPanel == null) return;
            Play(buttonRelease);
            if (openPanel == panels["gameOptionsPanel"]) { OpenPanel("economyPanel"); return; }
            if (openPanel == panels["economyPanel"]) { OpenPanel("difficultyPanel"); return; }
            if (openPanel == panels["difficultyPanel"]) { OpenPanel(pendingStartIndex >= 0 && panels.ContainsKey("debugPanel") ? "debugPanel" : "campaignPanel"); return; }
            // The Mod Browser and the Mod Builder go back to the Mods hub.
            if (panels.TryGetValue(Modding.ModBuilder.ProjectsPanel, out var mbp) && openPanel == mbp)
            {
                if (modProjects != null && modProjects.CancelImport()) return;   // the import list: the projects again
                modProjects?.Close();
            }
            if (panels.ContainsKey("modsHubPanel") && (openPanel == panels["modsPanel"] || openPanel == mbp))
            {
                var from = openPanel == mbp ? modsHubBuilder : modsHubBrowser;
                OpenPanel("modsHubPanel");
                openPanel.schedule.Execute(() => Select(from)).ExecuteLater(60);
                return;
            }
            var closing = openPanel;
            HidePanel(closing);
            openPanel = null;
            mainColumn.RemoveFromClassList("main-column--dimmed");
            root.RemoveFromClassList("menu-root--panel");
            root.schedule.Execute(() => MenuKeys.Refresh(menuPrompts));
            SetFocusable(mainButtons, true);
            RefreshUpdateButton();
            var target = closing == panels["campaignPanel"] || panels.TryGetValue("debugPanel", out var ap) && closing == ap ? newGameButton
                : closing == panels["loadPanel"] ? loadButton
                : closing == panels["optionsPanel"] ? optionsButton
                : closing == panels["multiplayerPanel"] ? multiplayerButton
                : panels.TryGetValue("modsPanel", out var mp) && closing == mp || panels.TryGetValue("modsHubPanel", out var mh) && closing == mh ? modsButton : aboutButton;
            Select(target);
        }

        void PickCampaign(Campaign c)
        {
            pendingModCampaign = null;
            pendingCampaign = c;
            pendingStartIndex = -1;
            OpenPanel("difficultyPanel");
        }

        float pendingDifficulty = Session.DifficultyNormal;

        void PickDifficulty(float difficulty)
        {
            pendingDifficulty = difficulty;
            hardcoreNext = false;   // never carried over from an earlier new game: chosen each time
            RefreshHardcoreToggle();
            OpenPanel("economyPanel");
        }

        Economy pendingEconomy = Economy.Default;

        /// <summary>The economy picked: then the game options (the Kaamo Club from the start, hardcore) and Start.</summary>
        void PickEconomy(Economy economy)
        {
            pendingEconomy = economy;
            RefreshTutorialToggle();   // the option may have changed in Options meanwhile
            RefreshCapitalToggle();
            RefreshModOptions();   // remake mods: their new-game options
            var desc = root.Q<Label>("gameOptionsStartDesc");
            if (desc != null) desc.text = $"{Session.DifficultyName(pendingDifficulty)}  ·  {Session.EconomyName(economy)}";
            OpenPanel("gameOptionsPanel");
        }

        /// <summary>Remake: the next new game is hardcore (permadeath, Session.Hardcore): off whenever a difficulty is picked.</summary>
        bool hardcoreNext;

        void RefreshHardcoreToggle()
        {
            var b = root.Q<Button>("hardcoreToggle");
            if (b == null) return;
            b.EnableInClassList("choice-button--on", hardcoreNext);
            var label = root.Q<Label>("hardcoreLabel");
            if (label != null)
                label.text = $"{Localization.Extra("hardcoreTitle", "Hardcore")}: {(hardcoreNext ? Localization.Extra("hardcoreOn", "Permadeath") : Localization.Extra("hardcoreOff", "Off"))}".ToUpperInvariant();
            var desc = root.Q<Label>("hardcoreDesc");
            if (desc != null)
                desc.text = Localization.Extra("hardcoreDesc",
                    "One life: saves only on docking; if your ship is destroyed, this game's saves are deleted.");
        }

        /// <summary>The Supernova Challenge's entry under the campaign cards (the remake's: the original offered it nowhere,
        /// supernova_challenge.md 1): its name (293) and the highscore (294) with the best kills (176), else the
        /// description's first paragraph (298).</summary>
        void RefreshChallenge()
        {
            var label = root.Q<Label>("challengeLabel");
            if (label != null) label.text = Localization.Get(293).ToUpperInvariant();
            var desc = root.Q<Label>("challengeDesc");
            if (desc == null) return;
            int best = SupernovaChallenge.Highscore;
            string intro = Localization.Get(298).Replace("\r\n", "\n");
            int cut = intro.IndexOf("\n\n", System.StringComparison.Ordinal);
            desc.text = best > 0
                ? $"{Localization.Get(294)}: {SupernovaChallenge.Digits(best)}  ·  {Localization.Get(176)}: {SupernovaChallenge.BestKills}"
                : cut > 0 ? intro.Substring(0, cut) : intro;
        }

        // Remake (GitHub #4, NewGamePlus): with a finished game in the save slots, the campaign panel offers New Game+.
        bool newGamePlus;
        SaveData ngPlusSave;

        void RefreshNgPlus(bool rescan)
        {
            var b = root.Q<Button>("ngPlusToggle");
            if (b == null) return;
            if (rescan) { ngPlusSave = NewGamePlus.FindFinished(out _); newGamePlus = false; }
            b.style.display = ngPlusSave != null ? DisplayStyle.Flex : DisplayStyle.None;
            b.EnableInClassList("choice-button--on", newGamePlus);
            var label = root.Q<Label>("ngPlusLabel");
            if (label != null)
                label.text = $"{Localization.Extra("ngPlus", "New Game+")}: {(newGamePlus ? Localization.Extra("on", "On") : Localization.Extra("off", "Off"))}".ToUpperInvariant();
            var desc = root.Q<Label>("ngPlusDesc");
            if (desc != null && ngPlusSave != null)
                desc.text = string.Format(Localization.Extra("ngPlusDesc",
                    "Start again with your finished game's ({0}, {1}) credits, blueprints, medals and Kaamo Club."),
                    (Campaign)ngPlusSave.campaign switch { Campaign.Valkyrie => "Valkyrie", Campaign.Supernova => "Supernova", _ => "Galaxy on Fire 2" }, ItemInfo.Credits(ngPlusSave.credits));
        }

        /// <summary>Remake (GitHub #8): the original's Kaamo Club expansion (an in-app purchase, texts 78 / 88 / 93) as a new
        /// game's choice: Status::resetGame sets the club's state to 3 (owned) while it is bought, so no siege, no purchase.
        /// Remembered for the next new game (PlayerPrefs "newgame_kaamo").</summary>
        static bool KaamoFromStart
        {
            get => PlayerPrefs.GetInt("newgame_kaamo", 0) == 1;
            set { PlayerPrefs.SetInt("newgame_kaamo", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        void RefreshKaamoToggle()
        {
            var b = root.Q<Button>("kaamoToggle");
            if (b == null) return;
            bool on = KaamoFromStart;
            b.EnableInClassList("choice-button--on", on);
            var label = root.Q<Label>("kaamoLabel");
            if (label != null)
                label.text = $"{Localization.Get(78)}: {(on ? Localization.Extra("kaamoOwned", "Owned from the start") : Localization.Extra("kaamoNotOwned", "Win it in the game"))}".ToUpperInvariant();
            var desc = root.Q<Label>("kaamoDesc");
            if (desc != null)
                desc.text = Localization.Extra("kaamoDesc",
                    "Own the Kaamo Club in Shima from the start: no siege, no 30 million.");
        }

        /// <summary>Remake: the tutorial popups (Settings.TutorialHints, also in Options > Gameplay) as a game option before the
        /// start (it was a question after the economy).</summary>
        void RefreshTutorialToggle()
        {
            var b = root.Q<Button>("tutorialToggle");
            if (b == null) return;
            bool on = Settings.TutorialHints;
            b.EnableInClassList("choice-button--on", on);
            var label = root.Q<Label>("tutorialLabel");
            if (label != null)
                label.text = $"{Localization.Extra("tutorialTitle", "Tutorials")}: {(on ? Localization.Extra("tutorialOn", "On") : Localization.Extra("tutorialOff", "Off"))}".ToUpperInvariant();
            var desc = root.Q<Label>("tutorialDesc");
            if (desc != null)
                desc.text = Localization.Extra("tutorialDesc",
                    "Popups explain the controls, hangar, map and missions the first time.");
        }

        /// <summary>Remake (players' suggestion): the capital ship enhancements (Settings.CapitalShips, also in Options >
        /// Gameplay) as a game option before the start.</summary>
        void RefreshCapitalToggle()
        {
            var b = root.Q<Button>("capitalToggle");
            if (b == null) return;
            bool on = Settings.CapitalShips;
            b.EnableInClassList("choice-button--on", on);
            var label = root.Q<Label>("capitalLabel");
            if (label != null)
                label.text = $"{Localization.Extra("capitalTitle", "Capital ships")}: {(on ? Localization.Extra("capitalOn", "Enhanced") : Localization.Extra("capitalOff", "Original"))}".ToUpperInvariant();
            var desc = root.Q<Label>("capitalDesc");
            if (desc != null)
                desc.text = Localization.Extra("capitalDesc",
                    "Capital ships get escorts and stronger turrets; carriers and Vossk battleships can be destroyed and docked at.");
        }

        // ---- remake: the Mods hub and the Mod Builder ----

        ModBuilderProjects modProjects;
        Button modsHubBrowser, modsHubBuilder;

        /// <summary>The Mods button's panel: the Mod Browser (installed mods) and the Mod Builder (your own projects).</summary>
        VisualElement BuildModsHub(VisualElement host)
        {
            var p = new VisualElement { name = "modsHubPanel" };
            p.AddToClassList("panel");
            p.usageHints = UsageHints.DynamicTransform;
            var title = new Label(Localization.Extra("modsButton", "Mods").ToUpperInvariant());
            title.AddToClassList("panel-title");
            title.AddToClassList("gof-semibold");
            p.Add(title);
            var accent = new VisualElement();
            accent.AddToClassList("panel-accent");
            p.Add(accent);
            Button Choice(string heading, string desc, Action onClick)
            {
                var b = new Button();
                b.AddToClassList("choice-button");
                var t = new Label(heading.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                t.AddToClassList("choice-title");
                t.AddToClassList("gof-semibold");
                b.Add(t);
                var d = new Label(desc) { pickingMode = PickingMode.Ignore };
                d.AddToClassList("choice-desc");
                b.Add(d);
                b.clicked += () => { Play(buttonRelease); onClick(); };
                HookFocusSound(b);
                p.Add(b);
                return b;
            }
            modsHubBrowser = Choice(Localization.Extra("modBrowser", "Mod Browser"),
                Localization.Extra("modBrowserDesc", "Turn installed mods on and off, set their order, import new ones."),
                () => OpenPanel("modsPanel"));
            modsHubBuilder = Choice(Localization.Extra("mbTitle", "Mod Builder"),
                Localization.Extra("modBuilderDesc", "Make ships, items, characters and more in the game, test them, then export them."),
                () => OpenPanel(Modding.ModBuilder.ProjectsPanel));
            var back = new Button(Back) { text = "‹  " + Localization.Get(170).ToUpperInvariant() };
            back.AddToClassList("menu-button");
            back.AddToClassList("back-button");
            back.AddToClassList("gof-semibold");
            HookFocusSound(back);
            p.Add(back);
            host.Add(p);
            return p;
        }

        /// <summary>A Mod Builder project opened: its hangar (Modding.ModBuilder), once the project's models have loaded.</summary>
        void OpenModProject(Modding.ModProject project)
        {
            if (screen != MenuState.Menu) return;
            Modding.ModBuilder.Begin(project);
            StartCoroutine(Leave("Station"));
        }

        void BeginGame(Economy economy)
        {
            Session.ResetNewGame();   // Status::resetGame: Phantom at Var Hastra (Mido)
            Session.Hardcore = hardcoreNext;   // remake: permadeath (a new RunId came with ResetNewGame)
            Session.Campaign = pendingCampaign;
            Session.Difficulty = pendingDifficulty;
            Session.Economy = economy;   // before the Database: it loads that economy's tables
            if (KaamoFromStart) Session.KaamoState = 3;   // resetGame with the expansion bought: the club owned, its storage empty
            Modding.ModGameOptions.ApplyChoices();   // remake mods: the options picked in the Game options panel
            var db = Database.Load();
            // Remake mods: a mod's campaign (Modding.ModCampaigns): its start, docked at its station; no GoF2 story.
            if (pendingModCampaign != null)
            {
                string modScene = Modding.ModCampaigns.Start(db, pendingModCampaign, out string error);
                if (modScene == null) { ShowNotice(pendingModCampaign.Name, error); return; }
                StartCoroutine(Leave(modScene));
                return;
            }
            // Remake: the mission select starts a new game at the chosen story step (Story.StartAtMission).
            if (pendingStartIndex >= 0) { StartCoroutine(Leave(Story.StartAtMission(db, pendingStartIndex))); return; }
            // MenuTouchWindow::startGOF2 / startValkyrie / startSupernova: the story's first step (Story).
            string scene = Story.StartCampaign(db, pendingCampaign);
            if (newGamePlus && ngPlusSave != null) NewGamePlus.Apply(ngPlusSave);   // remake: New Game+ carries over
            StartCoroutine(Leave(scene));
        }

        // ---- multiplayer (remake-only MVP, NetGame: host or join by address, one shared orbit) ----

        TextField mpAddress, mpName, mpPort;
        Label mpStatus;
        VisualElement mpAddressList, mpLocalBox;
        TextField mpJoinPassword, mpSessionName;

        /// <summary>The name a public game is listed under: the Game name field, else "<pilot>'s universe".</summary>
        string SessionName()
        {
            string n = mpSessionName != null ? mpSessionName.value.Replace("<", "").Replace(">", "").Trim() : "";
            return n.Length > 0 ? n : GoF2Remake.Multiplayer.NetGame.DefaultSessionName();
        }
        ScrollView mpServerList;
        Coroutine serverBrowse;
        bool serverQueryRunning;
        string serverListKey = "";
        /// <summary>One per listed row: writes its ping again when a measurement lands (NetPing.Revision), so a new
        /// reading never rebuilds the list under the player's hand.</summary>
        readonly List<Action> serverPingRows = new List<Action>();
        int serverPingRevision = -1;

        // The host card's choice (PlayerPrefs "mp_mode"): online and listed in the server browser, online by join code only,
        // or on the local network by address.
        enum HostMode { Public = 0, Private = 1, Local = 2 }
        static HostMode Mode
        {
            get { int m = PlayerPrefs.GetInt("mp_mode", 0); return m >= 0 && m <= 2 ? (HostMode)m : HostMode.Public; }
            set => PlayerPrefs.SetInt("mp_mode", (int)value);
        }
        const float ServerRefreshSeconds = 5f;

        void SetupMultiplayerPanel()
        {
            mpAddress = root.Q<TextField>("mpAddress");
            mpStatus = root.Q<Label>("mpStatus");
            if (mpAddress != null) mpAddress.value = PlayerPrefs.GetString("mp_address", "127.0.0.1");
            mpName = root.Q<TextField>("mpName");
            if (mpName != null)
            {
                mpName.maxLength = GoF2Remake.Multiplayer.NetGame.MaxNameLength;
                mpName.value = GoF2Remake.Multiplayer.NetGame.PlayerName;
                mpName.RegisterValueChangedCallback(e => { GoF2Remake.Multiplayer.NetGame.PlayerName = e.newValue; ApplyHostMode(); });
            }
            mpAddressList = root.Q("mpAddressList");
            mpPort = root.Q<TextField>("mpPort");
            if (mpPort != null)
            {
                mpPort.maxLength = 5;
                mpPort.keyboardType = TouchScreenKeyboardType.NumberPad;   // the on-screen keyboard's number pad
                mpPort.value = GoF2Remake.Multiplayer.NetGame.HostPort.ToString();
                mpPort.RegisterValueChangedCallback(e =>
                {
                    if (ushort.TryParse(e.newValue, out ushort p) && p >= 1024) GoF2Remake.Multiplayer.NetGame.HostPort = p;
                    RefreshAddresses();
                });
            }
            mpLocalBox = root.Q("mpLocalBox");
            SetupHostMore();
            mpSessionName = root.Q<TextField>("mpSessionName");
            if (mpSessionName != null)
            {
                mpSessionName.maxLength = 48;
                mpSessionName.value = PlayerPrefs.GetString("mp_session_name", "");
                mpSessionName.RegisterValueChangedCallback(e => PlayerPrefs.SetString("mp_session_name", e.newValue.Trim()));
            }
            var hostPassword = root.Q<TextField>("mpHostPassword");
            if (hostPassword != null)
            {
                hostPassword.maxLength = GoF2Remake.Multiplayer.NetGame.MaxPasswordLength;
                hostPassword.isPasswordField = true;
                hostPassword.value = PlayerPrefs.GetString("mp_host_password", "");
                GoF2Remake.Multiplayer.NetGame.HostPassword = hostPassword.value;
                hostPassword.RegisterValueChangedCallback(e =>
                {
                    GoF2Remake.Multiplayer.NetGame.HostPassword = e.newValue;
                    PlayerPrefs.SetString("mp_host_password", GoF2Remake.Multiplayer.NetGame.CleanPassword(e.newValue));
                });
            }
            var maxPlayers = root.Q<TextField>("mpMaxPlayers");
            if (maxPlayers != null)
            {
                // The session's size, the host included (2..100, PlayerPrefs "mp_max_players"); shown clamped once left.
                maxPlayers.maxLength = 3;
                maxPlayers.keyboardType = TouchScreenKeyboardType.NumberPad;
                GoF2Remake.Multiplayer.NetGame.MaxPlayers = PlayerPrefs.GetInt("mp_max_players", GoF2Remake.Multiplayer.NetGame.DefaultMaxPlayers);
                maxPlayers.value = GoF2Remake.Multiplayer.NetGame.MaxPlayers.ToString();
                maxPlayers.RegisterValueChangedCallback(e =>
                {
                    if (!int.TryParse(e.newValue, out int n)) return;
                    GoF2Remake.Multiplayer.NetGame.MaxPlayers = n;
                    PlayerPrefs.SetInt("mp_max_players", GoF2Remake.Multiplayer.NetGame.MaxPlayers);
                });
                maxPlayers.RegisterCallback<FocusOutEvent>(_ => maxPlayers.SetValueWithoutNotify(GoF2Remake.Multiplayer.NetGame.MaxPlayers.ToString()));
            }
            // The Debug menu in the hosted session (PlayerPrefs "mp_allow_debug", off by default; NetGame.HostAllowsDebug).
            GoF2Remake.Multiplayer.NetGame.HostAllowsDebug = PlayerPrefs.GetInt("mp_allow_debug", 0) != 0;
            Bind("mpDebugOff", () => SetHostDebug(false));
            Bind("mpDebugOn", () => SetHostDebug(true));
            ApplyHostDebug();
            // Combat in the hosted session (PlayerPrefs "mp_pvp", PvE by default; NetGame.FreePvp): PvE = players fight only in
            // arena matches and faction sieges, PvP = anywhere. The server browser shows it as a chip.
            GoF2Remake.Multiplayer.NetGame.FreePvp = PlayerPrefs.GetInt("mp_pvp", 0) != 0;
            Bind("mpPvpOff", () => SetHostPvp(false));
            Bind("mpPvpOn", () => SetHostPvp(true));
            ApplyHostPvp();
            // The hosted world (PlayerPrefs "mp_persistent", fresh by default): persistent keeps every player's progress, the
            // factions, bans and news on this device (NetGame.HostWantsPersistent; the host is its master admin).
            GoF2Remake.Multiplayer.NetGame.HostWantsPersistent = PlayerPrefs.GetInt("mp_persistent", 0) != 0;
            Bind("mpWorldFresh", () => SetHostWorld(false));
            Bind("mpWorldPersistent", () => SetHostWorld(true));
            ApplyHostWorld();
            // Modded content in the hosted session (PlayerPrefs "mp_allow_mods", off by default; NetMods.HostAllowsMods).
            GoF2Remake.Multiplayer.NetMods.HostAllowsMods = PlayerPrefs.GetInt("mp_allow_mods", 0) != 0;
            Bind("mpModsOff", () => SetHostMods(false));
            Bind("mpModsOn", () => SetHostMods(true));
            ApplyHostMods();
            mpJoinPassword = root.Q<TextField>("mpJoinPassword");
            if (mpJoinPassword != null)
            {
                mpJoinPassword.maxLength = GoF2Remake.Multiplayer.NetGame.MaxPasswordLength;
                mpJoinPassword.isPasswordField = true;
                mpJoinPassword.value = GoF2Remake.Multiplayer.NetGame.JoinPassword;   // this run's only (not saved)
                mpJoinPassword.RegisterValueChangedCallback(e => GoF2Remake.Multiplayer.NetGame.JoinPassword = e.newValue);
            }
            mpServerList = root.Q<ScrollView>("mpServerList");
            Bind("mpModePublic", () => SetHostMode(HostMode.Public));
            Bind("mpModePrivate", () => SetHostMode(HostMode.Private));
            Bind("mpModeLocal", () => SetHostMode(HostMode.Local));
            ApplyHostMode();
            Bind("mpHost", () => WithName(() => StartCoroutine(LeaveForMultiplayer(null))));
            Bind("mpJoin", () =>
            {
                string address = mpAddress != null ? mpAddress.value.Trim() : "";
                if (address.Length == 0) return;
                PlayerPrefs.SetString("mp_address", address);
                WithName(() => StartCoroutine(LeaveForMultiplayer(address)));
            });
        }

        /// <summary>-mphost: hosts once the menu is up (the panel has listed the addresses).</summary>
        IEnumerator TestHostSoon()
        {
            yield return new WaitForSeconds(2f);
            while (screen != MenuState.Menu) yield return null;
            yield return LeaveForMultiplayer(null);
        }

        /// <summary>-mpjoin: joins that address, again every 2 s until a host answers (and after a session ends).</summary>
        IEnumerator AutoJoin(string address)
        {
            while (true)
            {
                while (screen != MenuState.Menu) yield return null;
                yield return LeaveForMultiplayer(address);   // returns only when the connection failed
                yield return new WaitForSeconds(2f);
            }
        }

        void SetHostDebug(bool allowed)
        {
            GoF2Remake.Multiplayer.NetGame.HostAllowsDebug = allowed;
            PlayerPrefs.SetInt("mp_allow_debug", allowed ? 1 : 0);
            ApplyHostDebug();
        }

        void ApplyHostDebug()
        {
            bool allowed = GoF2Remake.Multiplayer.NetGame.HostAllowsDebug;
            root.Q<Button>("mpDebugOff")?.EnableInClassList("choice-segment--active", !allowed);
            root.Q<Button>("mpDebugOn")?.EnableInClassList("choice-segment--active", allowed);
        }

        void SetHostPvp(bool pvp)
        {
            GoF2Remake.Multiplayer.NetGame.FreePvp = pvp;
            PlayerPrefs.SetInt("mp_pvp", pvp ? 1 : 0);
            ApplyHostPvp();
        }

        void ApplyHostPvp()
        {
            bool pvp = GoF2Remake.Multiplayer.NetGame.FreePvp;
            root.Q<Button>("mpPvpOff")?.EnableInClassList("choice-segment--active", !pvp);
            root.Q<Button>("mpPvpOn")?.EnableInClassList("choice-segment--active", pvp);
        }

        void SetHostWorld(bool persistent)
        {
            GoF2Remake.Multiplayer.NetGame.HostWantsPersistent = persistent;
            PlayerPrefs.SetInt("mp_persistent", persistent ? 1 : 0);
            ApplyHostWorld();
        }

        void ApplyHostWorld()
        {
            bool persistent = GoF2Remake.Multiplayer.NetGame.HostWantsPersistent;
            root.Q<Button>("mpWorldFresh")?.EnableInClassList("choice-segment--active", !persistent);
            root.Q<Button>("mpWorldPersistent")?.EnableInClassList("choice-segment--active", persistent);
        }

        void SetHostMods(bool allowed)
        {
            GoF2Remake.Multiplayer.NetMods.HostAllowsMods = allowed;
            PlayerPrefs.SetInt("mp_allow_mods", allowed ? 1 : 0);
            ApplyHostMods();
        }

        void ApplyHostMods()
        {
            bool allowed = GoF2Remake.Multiplayer.NetMods.HostAllowsMods;
            root.Q<Button>("mpModsOff")?.EnableInClassList("choice-segment--active", !allowed);
            root.Q<Button>("mpModsOn")?.EnableInClassList("choice-segment--active", allowed);
            var label = root.Q<Label>("mpModsLabel");
            if (label != null)
            {
                int n = Modding.ModManager.SinglePlayerActive.Count;
                label.text = (n == 0 ? Localization.Extra("mpMods", "Mods")
                    : string.Format(Localization.Extra("mpModsCount", "Mods ({0} on)"), n)).ToUpperInvariant();
            }
        }

        /// <summary>The host card's settings scroll when they don't fit (several network adapters, a short screen), and the
        /// scroll bars are hidden everywhere: "More options" under them says so while settings are cut off below, and a
        /// tap scrolls down to them.</summary>
        void SetupHostMore()
        {
            var scroll = root.Q<ScrollView>("mpHostScroll");
            var more = root.Q<Label>("mpHostMore");
            if (scroll == null || more == null) return;
            more.text = "▼ " + Localization.Extra("mpMoreOptions", "More options").ToUpperInvariant();
            void Update()
            {
                float hidden = scroll.contentContainer.layout.height - scroll.contentViewport.layout.height;
                more.EnableInClassList("mp-host-more--shown", hidden > 1f && scroll.scrollOffset.y < hidden - 1f);
            }
            scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => Update());
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => Update());
            scroll.verticalScroller.valueChanged += _ => Update();
            more.RegisterCallback<ClickEvent>(_ =>
                scroll.scrollOffset = new Vector2(0f, scroll.scrollOffset.y + scroll.contentViewport.layout.height * 0.8f));
        }

        void SetHostMode(HostMode mode)
        {
            Mode = mode;
            ApplyHostMode();
        }

        /// <summary>The host card for its choice: the segments, the text, the addresses only for the local network.</summary>
        void ApplyHostMode()
        {
            var mode = Mode;
            void Seg(string name, HostMode m) => root.Q<Button>(name)?.EnableInClassList("choice-segment--active", mode == m);
            Seg("mpModePublic", HostMode.Public);
            Seg("mpModePrivate", HostMode.Private);
            Seg("mpModeLocal", HostMode.Local);
            if (mpLocalBox != null) mpLocalBox.style.display = mode == HostMode.Local ? DisplayStyle.Flex : DisplayStyle.None;
            // Online: the address box's room goes (the fill still pushes Host to the bottom, level with Join).
            root.Q("mpHostFill")?.EnableInClassList("mp-card-fill--empty", mode != HostMode.Local);
            if (mpSessionName != null)
            {
                // Only a listed game has a name; the placeholder shows the default (the pilot name may have changed).
                mpSessionName.style.display = mode == HostMode.Public ? DisplayStyle.Flex : DisplayStyle.None;
                mpSessionName.textEdition.placeholder = string.Format(Localization.Extra("mpSessionNameHint", "Game name: {0}"), GoF2Remake.Multiplayer.NetGame.DefaultSessionName());
            }
            var text = root.Q<Label>("mpHostText");
            if (text != null)
                text.text = mode == HostMode.Public
                    ? Localization.Extra("mpHostPublicText", "Listed in the server browser; you also get a join code.")
                    : mode == HostMode.Private
                        ? Localization.Extra("mpHostPrivateText", "Not listed: friends join with the code you get.")
                        : Localization.Extra("mpHostText", "Players on your network join with an address (tap to copy):");
        }

        /// <summary>While the Multiplayer panel is open: the server list, again every 5 s.</summary>
        IEnumerator BrowseServers()
        {
            float next = 0f;
            while (openPanel != null && panels.TryGetValue("multiplayerPanel", out var mp) && openPanel == mp)
            {
                if (Time.unscaledTime >= next && !serverQueryRunning && screen == MenuState.Menu)
                {
                    next = Time.unscaledTime + ServerRefreshSeconds;
                    yield return QueryServers();
                }
                if (serverPingRevision != GoF2Remake.Multiplayer.NetPing.Revision)
                {
                    serverPingRevision = GoF2Remake.Multiplayer.NetPing.Revision;
                    foreach (var fill in serverPingRows) fill();
                }
                yield return null;
            }
            serverBrowse = null;
        }

        IEnumerator QueryServers()
        {
            if (mpServerList == null) yield break;
            serverQueryRunning = true;
            if (mpServerList.childCount == 0) ServerListNote(Localization.Extra("mpSearching", "Looking for games..."), true);
            var query = GoF2Remake.Multiplayer.NetLobby.Query();
            while (!query.IsCompleted) yield return null;
            serverQueryRunning = false;
            var list = query.IsFaulted ? null : query.Result;
            var count = root.Q<Label>("mpServerCount");
            if (count != null)
                count.text = list == null ? "" : list.Count == 1 ? Localization.Extra("mpOneGame", "1 game")
                    : string.Format(Localization.Extra("mpGames", "{0} games"), list.Count);
            if (list == null) { ServerListNote(GoF2Remake.Multiplayer.NetGame.Status); yield break; }
            if (list.Count == 0) { ServerListNote(Localization.Extra("mpNoGames", "No public games right now. Host one, or join with a code.")); yield break; }
            // The round trip to each listed game's Relay region (NetPing: one QoS measurement per region, at most
            // once a minute); the rows fill their ping in when it lands.
            GoF2Remake.Multiplayer.NetPing.Measure(list.ConvertAll(x => x.region));
            // Unchanged since the last refresh: the rows stay (a controller's focus on one too).
            var key = new System.Text.StringBuilder();
            foreach (var e in list) key.Append(e.code).Append(e.name).Append(e.players).Append('/').Append(e.maxPlayers).Append(e.password).Append(e.mods).Append(e.pvp).Append('|');
            if (key.ToString() == serverListKey) yield break;
            serverListKey = key.ToString();
            mpServerList.Clear();
            serverPingRows.Clear();
            foreach (var e in list)
            {
                var entry = e;
                // Two lines: the name and its tags, then the host, the players and the version.
                var row = new Button { focusable = true };
                row.AddToClassList("mp-address-row");
                row.AddToClassList("mp-server-row");
                var missingMods = entry.MissingMods;   // a modded game: the mods this game lacks (NetMods)
                bool joinable = !entry.Full && entry.SameVersion && missingMods.Count == 0;
                row.EnableInClassList("mp-server-row--full", !joinable);
                var top = new VisualElement { pickingMode = PickingMode.Ignore };
                top.AddToClassList("mp-server-line");
                var name = new Label(entry.name) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("mp-server-name");
                name.AddToClassList("gof-semibold");
                top.Add(name);
                void Tag(string text, string extra)
                {
                    var tag = new Label(text.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                    tag.AddToClassList("mp-server-tag");
                    if (extra != null) tag.AddToClassList(extra);
                    top.Add(tag);
                }
                if (entry.dedicated) Tag(Localization.Extra("mpServerTag", "Server"), null);
                if (entry.password) Tag(Localization.Extra("mpPasswordTag", "Password"), "mp-server-tag--password");
                if (entry.Modded) Tag(Localization.Extra("mpModdedTag", "Modded"), "mp-server-tag--mods");
                if (entry.pvp.HasValue)   // players fight anywhere / only in arenas and sieges (NetGame.FreePvp)
                    Tag(entry.pvp.Value ? Localization.Extra("mpPvp", "PvP") : Localization.Extra("mpPve", "PvE"),
                        entry.pvp.Value ? "mp-server-tag--pvp" : "mp-server-tag--pve");
                // Where the game is and how far away: the short name of its Relay region and the round trip to it,
                // with a green / amber / red dot (NetPing). A game listed by a build from before the region key has
                // none; a ping not measured yet reads "..." and fills in by itself.
                if (!string.IsNullOrEmpty(entry.region))
                {
                    var ping = new VisualElement { pickingMode = PickingMode.Ignore };
                    ping.AddToClassList("mp-server-ping");
                    var regionLabel = new Label(GoF2Remake.Multiplayer.NetPing.RegionName(entry.region)) { pickingMode = PickingMode.Ignore };
                    regionLabel.AddToClassList("mp-server-region");
                    ping.Add(regionLabel);
                    var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                    dot.AddToClassList("mp-server-ping-dot");
                    ping.Add(dot);
                    var msLabel = new Label { pickingMode = PickingMode.Ignore };
                    msLabel.AddToClassList("mp-server-ping-ms");
                    ping.Add(msLabel);
                    top.Add(ping);
                    void FillPing()
                    {
                        int ms = GoF2Remake.Multiplayer.NetPing.Of(entry.region);
                        int quality = GoF2Remake.Multiplayer.NetPing.Quality(ms);
                        msLabel.text = ms < 0 ? "..." : string.Format(Localization.Extra("mpPingMs", "{0} ms"), ms);
                        dot.EnableInClassList("mp-server-ping-dot--good", quality == 0);
                        dot.EnableInClassList("mp-server-ping-dot--fair", quality == 1);
                        dot.EnableInClassList("mp-server-ping-dot--poor", quality == 2);
                    }
                    FillPing();
                    serverPingRows.Add(FillPing);
                }
                row.Add(top);
                var bottom = new VisualElement { pickingMode = PickingMode.Ignore };
                bottom.AddToClassList("mp-server-line");
                string info = (entry.dedicated ? "" : entry.host + "  ·  ") + $"{entry.players} / {entry.maxPlayers}"
                              + (entry.Full ? "  ·  " + Localization.Extra("mpFull", "full") : "")
                              + (missingMods.Count > 0 ? "  ·  " + string.Format(Localization.Extra("mpNeedsModsShort", "needs mods: {0}"), string.Join(", ", missingMods))
                                 : entry.Modded ? "  ·  " + entry.modNames : "");
                var infoLabel = new Label(info) { pickingMode = PickingMode.Ignore };
                infoLabel.AddToClassList("mp-server-info");
                bottom.Add(infoLabel);
                // The version: every row; another version's says it can't be joined from here.
                var version = new Label(entry.SameVersion ? entry.version
                    : entry.version == GoF2Remake.Multiplayer.NetGame.Version ? Localization.Extra("mpOtherBuild", "another build")
                    : string.Format(Localization.Extra("mpNeedsVersion", "needs {0}"), entry.version)) { pickingMode = PickingMode.Ignore };
                version.AddToClassList("mp-server-version");
                version.EnableInClassList("mp-server-version--other", !entry.SameVersion);
                bottom.Add(version);
                row.Add(bottom);
                row.clicked += () =>
                {
                    if (joinable && entry.password && GoF2Remake.Multiplayer.NetGame.CleanPassword(GoF2Remake.Multiplayer.NetGame.JoinPassword).Length == 0)
                    {
                        // A password game: the field first (the server checks it).
                        SetMpStatus(Localization.Extra("mpEnterPassword", "This game needs a password: enter it under Join."));
                        mpJoinPassword?.Focus();
                    }
                    else if (joinable) WithName(() => StartCoroutine(LeaveForMultiplayer(entry.code)));
                    else if (mpStatus != null)
                        mpStatus.text = entry.Full ? Localization.Extra("mpGameFull", "That game is full.")
                            : entry.SameVersion && missingMods.Count > 0 ? string.Format(Localization.Extra("mpNeedsMods",
                                "Needs mods you don't have (or another version): {0}."), string.Join(", ", missingMods))
                            : entry.version == GoF2Remake.Multiplayer.NetGame.Version
                            ? string.Format(Localization.Extra("mpOtherBuildText", "That game runs another build of {0}: only the same build can join."), entry.version)
                            : string.Format(Localization.Extra("mpOtherVersion", "That game runs version {0}, yours is {1}."), entry.version, GoF2Remake.Multiplayer.NetGame.Version);
                };
                mpServerList.Add(row);
            }
        }

        void ServerListNote(string text, bool busy = false)
        {
            serverListKey = "";
            mpServerList.Clear();
            serverPingRows.Clear();
            var note = new Label(text);
            note.AddToClassList("mp-server-empty");
            UiBusy.Mark(note, busy);
            mpServerList.Add(note);
        }

        /// <summary>The Multiplayer panel's status line; 'busy' puts the waiting arc before it (docs/UI-DESIGN.md "Waiting").</summary>
        void SetMpStatus(string text, bool busy = false)
        {
            if (mpStatus == null) return;
            mpStatus.text = text;
            UiBusy.Mark(mpStatus, busy && !string.IsNullOrEmpty(text));
        }

        void OpenMultiplayer()
        {
            SetMpStatus(GoF2Remake.Multiplayer.NetGame.Status);   // why the last session ended
            OpenPanel("multiplayerPanel");
            // The first visit: the name first; Back there leaves the panel (#20: with a controller it came back to the
            // prompt on every move).
            if (NeedsName(null)) dialogNo = () => { if (openPanel == panels["multiplayerPanel"]) Back(); };
        }

        /// <summary>Multiplayer needs a pilot name (the others see it on the lock plate, in the chat and the pilot lists):
        /// without one (NetGame.PlayerName empty) a prompt asks for it, then runs 'then'. False = a name is set already.</summary>
        bool NeedsName(Action then)
        {
            if (GoF2Remake.Multiplayer.NetGame.Clean(GoF2Remake.Multiplayer.NetGame.PlayerName).Length > 0 || dialogField == null) return false;
            ShowDialog(Localization.Extra("mpNameTitle", "Pilot name"),
                       Localization.Extra("mpNameText", "The name other pilots see. Change it later at the top of the Multiplayer panel."), then);
            dialogField.value = "";
            dialogField.textEdition.placeholder = Localization.Extra("mpNamePlaceholder", "Your pilot name");
            dialogField.style.display = DisplayStyle.Flex;
            InputGlyph.SetText(root.Q<Button>("dialogYes"), "OK");
            InputGlyph.SetText(root.Q<Button>("dialogNo"), Localization.Get(170).ToUpperInvariant());   // Back
            dialogCheck = () =>
            {
                string name = GoF2Remake.Multiplayer.NetGame.Clean(dialogField.value);
                if (name.Length == 0) { dialogField.Focus(); return false; }
                GoF2Remake.Multiplayer.NetGame.PlayerName = name;
                if (mpName != null) mpName.SetValueWithoutNotify(GoF2Remake.Multiplayer.NetGame.PlayerName);
                ApplyHostMode();   // the default game name is the pilot's
                return true;
            };
            // With a controller the prompt opens on OK (A on it with no name, or Up, goes to the field): a field focused
            // at once brought the Xbox's keyboard up before the prompt had settled, and what it typed got lost (#20).
            if (InputMode.Current == InputKind.Gamepad) Select(root.Q<Button>("dialogYes"));
            else dialogField.Focus();
            return true;
        }

        /// <summary>Hosting and joining: 'action' once there is a pilot name (asked first when there is none).</summary>
        void WithName(Action action)
        {
            if (!NeedsName(action)) action();
        }

        /// <summary>The host card's addresses: one row per adapter (NetGame.LocalAddresses) with the port when it isn't the
        /// default; a tap copies it (what a friend types into Join).</summary>
        void RefreshAddresses()
        {
            if (mpAddressList == null) return;
            mpAddressList.Clear();
            ushort port = GoF2Remake.Multiplayer.NetGame.HostPort;
            var addresses = GoF2Remake.Multiplayer.NetGame.LocalAddresses();
            if (Debug.isDebugBuild) Debug.Log("Multiplayer addresses: " + string.Join(", ", addresses));
            if (addresses.Count == 0)
            {
                // No network: hosting still works on this device, but nobody else could join yet.
                var none = new Label(Localization.Extra("mpNoNetwork", "No network: connect to Wi-Fi or a hotspot so others can join."));
                none.AddToClassList("mp-card-text");
                mpAddressList.Add(none);
            }
            foreach (var (name, address) in addresses)
            {
                string join = port == GoF2Remake.Multiplayer.NetGame.DefaultPort ? address : $"{address}:{port}";
                var row = new Button { focusable = true };
                row.AddToClassList("mp-address-row");
                var n = new Label(name) { pickingMode = PickingMode.Ignore };
                n.AddToClassList("mp-address-name");
                var v = new Label(join) { pickingMode = PickingMode.Ignore };
                v.AddToClassList("mp-address-value");
                v.AddToClassList("gof-semibold");
                var c = new Label(Localization.Extra("mpCopy", "copy")) { pickingMode = PickingMode.Ignore };
                c.AddToClassList("mp-address-copy");
                row.Add(n); row.Add(v); row.Add(c);
                row.clicked += () =>
                {
                    GUIUtility.systemCopyBuffer = join;
                    foreach (var other in mpAddressList.Children()) other.RemoveFromClassList("mp-address-row--copied");
                    foreach (var other in mpAddressList.Query<Label>(className: "mp-address-copy").ToList()) other.text = Localization.Extra("mpCopy", "copy");
                    row.AddToClassList("mp-address-row--copied");
                    c.text = Localization.Extra("mpCopied", "copied");
                };
                mpAddressList.Add(row);
            }
        }

        /// <summary>Fades out, then hosts (address null: NetGame loads Space for everyone) or joins (the host's scene loads
        /// once connected). A failed start or connection fades the menu back in with the reason.</summary>
        IEnumerator LeaveForMultiplayer(string address)
        {
            if (screen != MenuState.Menu) yield break;
            screen = MenuState.Leaving;
            bool started;
            if (address == null && Mode != HostMode.Local)
            {
                // Online: the Relay session (and its listing) is reserved before the fade, so a failure shows here.
                SetMpStatus(Localization.Extra("mpOnlineStarting", "Starting an online session..."), true);
                var prep = GoF2Remake.Multiplayer.NetGame.PrepareOnlineHost(Mode == HostMode.Public ? SessionName() : null);
                while (!prep.IsCompleted) yield return null;
                if (prep.IsFaulted) GoF2Remake.Multiplayer.NetGame.AbortStart(prep.Exception);
                if (prep.IsFaulted || !prep.Result)
                {
                    SetMpStatus(GoF2Remake.Multiplayer.NetGame.Status);
                    screen = MenuState.Menu;
                    yield break;
                }
                SetMpStatus("");
                fade.AddToClassList("fade--on");
                StartCoroutine(FadeMusic(0f, 1.2f));
                yield return new WaitForSeconds(1.3f);
                started = TryStart(GoF2Remake.Multiplayer.NetGame.StartHost);
            }
            else if (address == null)
            {
                // Only fade out when it can start: a port in use says so at once (with a free one in the port field).
                if (!GoF2Remake.Multiplayer.NetGame.CanHost())
                {
                    SetMpStatus(GoF2Remake.Multiplayer.NetGame.Status);
                    if (GoF2Remake.Multiplayer.NetGame.SuggestedPort > 0 && mpPort != null) mpPort.value = GoF2Remake.Multiplayer.NetGame.SuggestedPort.ToString();
                    screen = MenuState.Menu;
                    yield break;
                }
                SetMpStatus("");
                fade.AddToClassList("fade--on");
                StartCoroutine(FadeMusic(0f, 1.2f));
                yield return new WaitForSeconds(1.3f);
                started = TryStart(GoF2Remake.Multiplayer.NetGame.StartHost);
            }
            else
            {
                // Joining: the menu stays while it connects (a missing host would be a long black screen); the fade once connected.
                SetMpStatus(string.Format(Localization.Extra("mpConnecting", "Connecting to {0}..."), address), true);
                if (GoF2Remake.Multiplayer.NetGame.IsJoinCode(address))
                {
                    // A join code: Relay finds the session, then it connects like an address.
                    var join = GoF2Remake.Multiplayer.NetGame.StartClientOnline(address);
                    while (!join.IsCompleted) yield return null;
                    if (join.IsFaulted) GoF2Remake.Multiplayer.NetGame.AbortStart(join.Exception);
                    started = !join.IsFaulted && join.Result;
                }
                else started = TryStart(() => GoF2Remake.Multiplayer.NetGame.StartClient(address));
                while (started && GoF2Remake.Multiplayer.NetGame.Active && !GoF2Remake.Multiplayer.NetGame.Connected) yield return null;
                if (started && GoF2Remake.Multiplayer.NetGame.Active)
                {
                    fade.AddToClassList("fade--on");
                    StartCoroutine(FadeMusic(0f, 1.2f));
                }
            }
            while (started && GoF2Remake.Multiplayer.NetGame.Active) yield return null;   // the scene change ends this
            SetMpStatus(GoF2Remake.Multiplayer.NetGame.Status);
            // The port was in use: the free one it found goes into the port field (hosting again uses it).
            if (address == null && GoF2Remake.Multiplayer.NetGame.SuggestedPort > 0 && mpPort != null)
                mpPort.value = GoF2Remake.Multiplayer.NetGame.SuggestedPort.ToString();
            fade.RemoveFromClassList("fade--on");
            screen = MenuState.Menu;
            StartCoroutine(FadeMusic(Settings.MusicVolume, 1f));
        }

        /// <summary>A host / join start that threw ends with its reason in the status line (NetGame.AbortStart), not a
        /// coroutine stopped on the fade or on "Connecting..." for good (a Discord report: unable to play multiplayer at all).</summary>
        static bool TryStart(System.Func<bool> start)
        {
            try { return start(); }
            catch (System.Exception e) { GoF2Remake.Multiplayer.NetGame.AbortStart(e); return false; }
        }

        // ---- debug panel (remake-only testing tools: F10 or five taps on the version text) -------

        int pendingStartIndex = -1;
        ScrollView missionList;
        TextField missionFilter;
        /// <summary>The list's rows with their search text, and each campaign heading with its rows.</summary>
        readonly List<(VisualElement row, string text)> missionRows = new List<(VisualElement, string)>();
        readonly List<(VisualElement heading, List<VisualElement> rows)> missionSections = new List<(VisualElement, List<VisualElement>)>();
        int versionTaps;
        float versionTapTime, debugHoldTime;
        readonly List<OptionControl> debugControls = new List<OptionControl>();

        /// <summary>A hidden panel next to the others: the mission list (a new game from any story step, no intro) on the
        /// left, the cheat toggles on the right.</summary>
        VisualElement debugMissionFocus, debugCheatFocus;   // where the focus was in each Debug column (OnNavigate)

        void BuildDebugPanel()
        {
            // A UI reload (PanelRenderer) rebuilds the tree: drop the old panel's elements.
            debugControls.Clear();
            missionRows.Clear();
            missionSections.Clear();
            var host = root.Q("panelHost");
            if (host == null) return;
            var panel = new VisualElement { name = "debugPanel" };
            panel.AddToClassList("panel");
            panel.AddToClassList("panel--wide");
            panel.AddToClassList("debug-panel");
            panel.usageHints = UsageHints.DynamicTransform;
            var title = new Label { name = "debugTitle" };
            title.AddToClassList("panel-title");
            title.AddToClassList("gof-semibold");
            panel.Add(title);
            var accent = new VisualElement();
            accent.AddToClassList("panel-accent");
            panel.Add(accent);
            var columns = new VisualElement();
            columns.AddToClassList("debug-columns");
            var left = new VisualElement();
            left.AddToClassList("debug-column");
            left.AddToClassList("debug-column--missions");
            // The cheats scroll on a short screen (the panel is sized to the screen, MainMenu.uss .debug-panel); the heading
            // and the note under them stay put (the note scrolled with them and showed cut off mid-line).
            var right = new VisualElement();
            right.AddToClassList("debug-column");
            right.AddToClassList("debug-column--cheats");
            var cheatList = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            cheatList.AddManipulator(new DragScroll(cheatList));
            cheatList.RegisterCallback<FocusInEvent>(e => { if (e.target is VisualElement v && v != cheatList) cheatList.ScrollTo(v); });   // follows the D-pad
            cheatList.AddToClassList("debug-cheat-list");
            columns.Add(left);
            columns.Add(right);
            panel.Add(columns);
            var back = new Button { name = "debugBack" };
            back.AddToClassList("menu-button");
            back.AddToClassList("back-button");
            back.AddToClassList("gof-semibold");
            back.clicked += Back;
            HookFocusSound(back);
            panel.Add(back);
            host.Add(panel);
            panels["debugPanel"] = panel;
            BuildMissionList(left);

            // The cheat toggles (Cheats; the actions are on the pause menu's / station's Debug page, in a running game).
            var heading = new Label { name = "debugCheatsTitle", pickingMode = PickingMode.Ignore };
            heading.AddToClassList("debug-heading");
            heading.AddToClassList("gof-semibold");
            right.Add(heading);
            right.Add(cheatList);
            foreach (var def in CheatsCatalog.Toggles())
            {
                var c = new OptionControl(def);
                c.Field.AddToClassList("option-row");
                c.Root.AddToClassList("debug-option");
                HookFocusSound(c.Field);
                c.Changed += () => Play(buttonRelease);
                cheatList.Add(c.Root);
                debugControls.Add(c);
            }
            var note = new Label { name = "debugNote", pickingMode = PickingMode.Ignore };
            note.AddToClassList("debug-note");
            right.Add(note);

            // Touch: five taps on the version text, each within 1 s of the last (its tap zone reaches well past the
            // small text, .footer-version).
            if (versionLabel != null)
            {
                versionLabel.pickingMode = PickingMode.Position;
                versionLabel.AddToClassList("footer-version");
                versionLabel.RegisterCallback<PointerDownEvent>(_ =>
                {
                    if (Time.unscaledTime - versionTapTime > 1f) versionTaps = 0;
                    versionTapTime = Time.unscaledTime;
                    if (++versionTaps >= 5) { versionTaps = 0; OpenDebug(); }
                });
            }
        }

        /// <summary>The main menu's Debug button: only with the debug tools on (Options > Gameplay, or opened once).</summary>
        void UpdateDebugButton()
        {
            debugButton?.EnableInClassList("menu-button--gone", !Cheats.Unlocked || !panels.ContainsKey("debugPanel"));
            UpdateColumnFit();
        }

        /// <summary>Resume and Debug both shown (every row, eight): the column moves up so Exit stays clear of the version
        /// text and its tap zone at the bottom left (.main-column--full).</summary>
        void UpdateColumnFit()
        {
            if (mainColumn == null) return;
            bool resume = resumeButton != null && !resumeButton.ClassListContains("menu-button--gone");
            bool debug = debugButton != null && !debugButton.ClassListContains("menu-button--gone");
            mainColumn.EnableInClassList("main-column--full", resume && debug);
        }

        /// <summary>"Update available" (UpdateCheck): in the menu, a newer release out and no panel open (the panels reach
        /// the bottom centre). Keys / controller reach it with Down from the last menu row.</summary>
        void RefreshUpdateButton()
        {
            if (updateRow == null || updateButton == null) return;
            bool shown = UpdateCheck.Available && screen == MenuState.Menu && openPanel == null;
            if (UpdateCheck.Available)
                updateButton.text = string.IsNullOrEmpty(UpdateCheck.LatestTag)
                    ? Localization.Extra("updateAvailable", "Update available").ToUpperInvariant()
                    : $"{Localization.Extra("updateAvailable", "Update available").ToUpperInvariant()}  ·  {UpdateCheck.LatestTag}";
            updateRow.EnableInClassList("update-row--shown", shown);
            updateButton.focusable = shown;
        }

        void OpenDebug()
        {
            if (!panels.ContainsKey("debugPanel") || openPanel == panels["debugPanel"]) return;
            if (dialog.ClassListContains("dialog-backdrop--shown")) return;
            Play(buttonRelease);
            Cheats.Unlocked = true;   // from now on the pause menu and the station's system menu have a Debug page
            UpdateDebugButton();
            foreach (var c in debugControls) c.Refresh();
            OpenPanel("debugPanel");
        }

        /// <summary>A search field and every story step, grouped by campaign: "index  title  station" over an optional
        /// subtitle (StepSummaries). A row starts that step (the difficulty panel first).</summary>
        void BuildMissionList(VisualElement parent)
        {
            var heading = new Label { name = "debugMissionsTitle", pickingMode = PickingMode.Ignore };
            heading.AddToClassList("debug-heading");
            heading.AddToClassList("gof-semibold");
            parent.Add(heading);
            missionFilter = new TextField { name = "debugFilter" };
            missionFilter.AddToClassList("debug-filter");
            missionFilter.textEdition.hidePlaceholderOnFocus = true;
            missionFilter.RegisterValueChangedCallback(e => FilterMissions(e.newValue));
            parent.Add(missionFilter);
            missionList = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            missionList.AddToClassList("debug-mission-list");
            new DragScroll(missionList);
            parent.Add(missionList);

            var db = Database.Load();
            Campaign? section = null;
            List<VisualElement> sectionRows = null;
            for (int i = 0; i <= Story.LastIndex; i++)
            {
                // Steps never current on their own (53 / 129 skipped, 42 inside 41's level, 46 / 107 passed through, 149 / 150
                // dialogue slots): the step that plays them stands for them.
                if (Story.MissionSelectStart(i) != i) continue;
                var campaign = CampaignOf(i);
                if (section != campaign)
                {
                    section = campaign;
                    var h = new Label(campaign switch
                    {
                        Campaign.Valkyrie => "VALKYRIE  ·  45-83",
                        Campaign.Supernova => "SUPERNOVA  ·  84-162",
                        _ => "GALAXY ON FIRE 2  ·  0-44",
                    }) { pickingMode = PickingMode.Ignore };
                    h.AddToClassList("debug-mission-section");
                    h.AddToClassList("gof-semibold");
                    missionList.Add(h);
                    sectionRows = new List<VisualElement>();
                    missionSections.Add((h, sectionRows));
                }
                var step = StoryTable.Step(i);
                var info = StepSummaries.Get(i);
                string station = step == null ? "" : step.station >= 0 ? db.Stations.Find(s => s.index == step.station)?.name ?? ""
                               : step.station == Session.VoidOrbit ? "Void" : "";
                // A one-line title over an optional subtitle (the objective texts repeat, e.g. the whole tutorial is
                // "I'm on my way to Var Hastra.", so they only stand in when a step has no title).
                string summary = info?.summary ?? "";
                string name = !string.IsNullOrEmpty(info?.title) ? info.title : Story.StepLabel(db, i);
                if (string.IsNullOrEmpty(name)) name = station;

                var row = new Button();
                row.AddToClassList("debug-mission");
                var top = new VisualElement { pickingMode = PickingMode.Ignore };
                top.AddToClassList("debug-mission-top");
                var num = new Label(i.ToString()) { pickingMode = PickingMode.Ignore };
                num.AddToClassList("debug-mission-index");
                num.AddToClassList("gof-semibold");
                top.Add(num);
                var label = new Label(name) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("debug-mission-title");
                label.AddToClassList("gof-semibold");
                top.Add(label);
                if (!string.IsNullOrEmpty(station) && station != name)
                {
                    var where = new Label(station) { pickingMode = PickingMode.Ignore };
                    where.AddToClassList("debug-mission-station");
                    top.Add(where);
                }
                row.Add(top);
                if (!string.IsNullOrEmpty(summary))
                {
                    var sum = new Label(summary) { pickingMode = PickingMode.Ignore };
                    sum.AddToClassList("debug-mission-summary");
                    row.Add(sum);
                }
                int index = i;
                row.clicked += () => { Play(buttonRelease); StartAtStep(index); };
                HookFocusSound(row);
                missionList.Add(row);
                missionRows.Add((row, $"{i} {name} {station} {summary}".ToLowerInvariant()));
                sectionRows.Add(row);
            }
        }

        static Campaign CampaignOf(int index) =>
            index >= Story.Dlc1WonIndex ? Campaign.Supernova : index >= Story.GameWonIndex ? Campaign.Valkyrie : Campaign.GalaxyOnFire2;

        void StartAtStep(int index)
        {
            pendingStartIndex = index;
            pendingCampaign = CampaignOf(index);
            OpenPanel("difficultyPanel");
        }

        /// <summary>Rows whose index, title, station or summary contain every word typed (a number matches the index
        /// exactly); a campaign heading hides with all its rows.</summary>
        void FilterMissions(string query)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var (row, text) in missionRows)
            {
                bool match = true;
                foreach (var w in words)
                {
                    if (int.TryParse(w, out _)) { if (!text.StartsWith(w + " ")) { match = false; break; } }
                    else if (!text.Contains(w)) { match = false; break; }
                }
                row.style.display = match ? DisplayStyle.Flex : DisplayStyle.None;
            }
            foreach (var (heading, rows) in missionSections)
                heading.style.display = rows.Exists(r => r.style.display != DisplayStyle.None) ? DisplayStyle.Flex : DisplayStyle.None;
            missionList.scrollOffset = Vector2.zero;
        }

        /// <summary>GameRecord::load: the saved (docked) state, then the station.</summary>
        void LoadSlot(int slot)
        {
            if (slot < 0) return;
            // A save made with mods that aren't on now: say what happens to their items first (Modding.ModSaves).
            var missing = Modding.ModSaves.MissingMods(SaveGame.Preview(slot));
            if (missing.Count > 0)
            {
                ShowDialog(Localization.Extra("modsMissingTitle", "Mods missing"), string.Format(Localization.Extra("modsMissingText",
                    "This game was saved with mods that aren't on: {0}.\nTheir items will be removed and refunded. Turn the mods on in Mods to keep them.\n\nLoad anyway?"),
                    string.Join(", ", missing)), () => { if (SaveGame.Load(slot)) StartCoroutine(Leave("Station")); });
                return;
            }
            if (!SaveGame.Load(slot)) return;
            StartCoroutine(Leave("Station"));
        }

        /// <summary>The mod browser's "Rebuild cache": every mod cache goes (ModTextureCache.ClearAll: textures, hangar shadows,
        /// unpacked zips) and the mods load again, behind the black fade with the loading screen.</summary>
        IEnumerator RebuildModCache()
        {
            if (screen != MenuState.Menu) yield break;
            screen = MenuState.Leaving;   // no menu input meanwhile
            fade.AddToClassList("fade--on");
            yield return new WaitForSecondsRealtime(0.4f);
            Modding.ModTextureCache.ClearAll();
            Modding.ModManager.Scan();   // a new revision: every loader starts again
            yield return WaitForMods(leaveLoading, 120f);
            fade.RemoveFromClassList("fade--on");
            modBrowser?.Rebuild();
            screen = MenuState.Menu;
        }

        IEnumerator Leave(string scene = null)
        {
            scene ??= gameScene;
            screen = MenuState.Leaving;
            fade.AddToClassList("fade--on");
            StartCoroutine(FadeMusic(0f, 1.2f));
            yield return new WaitForSeconds(1.3f);
            // The mods (Modding.ModLoading: ship models, music; loading since the menu opened or the mods changed), at most
            // 60 s more, with the loading screen on the black fade.
            yield return WaitForMods(leaveLoading, 60f);
            if (Application.CanStreamedLevelBeLoaded(scene)) SceneManager.LoadScene(scene);
            else
            {
                Debug.LogWarning($"MainMenu: scene '{scene}' is not in the build settings.");
                fade.RemoveFromClassList("fade--on");
                screen = MenuState.Menu;
                StartCoroutine(FadeMusic(Settings.MusicVolume, 1f));
            }
        }

        void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---- dialog ----------------------------------------------------------------------------

        /// <summary>What had the focus when the dialog opened: closing gives it back (CloseDialog). The panel's first item
        /// got it instead, and in the Mod Browser focusing the top row selects it, so No on Delete moved the selection.</summary>
        VisualElement dialogReturn;

        void ShowDialog(string title, string text, Action onYes)
        {
            if (!dialog.ClassListContains("dialog-backdrop--shown")) dialogReturn = root.focusController?.focusedElement as VisualElement;
            dialogYes = onYes;
            root.Q<Label>("dialogTitle").text = title.ToUpperInvariant();
            root.Q<Label>("dialogText").text = text;
            dialog.AddToClassList("dialog-backdrop--shown");
            Play(infoSound);
            Select(root.Q<Button>("dialogNo"));
        }

        /// <summary>The dialog as a notice: one button (the Yes button as OK), no question.</summary>
        void ShowNotice(string title, string text)
        {
            Debug.Log($"MainMenu: notice '{text}'");
            ShowDialog(title, text, null);
            var no = root.Q<Button>("dialogNo");
            var yes = root.Q<Button>("dialogYes");
            no.style.display = DisplayStyle.None;
            InputGlyph.SetText(yes, "OK");
            Select(yes);
        }

        void ConfirmDialog()
        {
            if (dialogCheck != null && !dialogCheck()) return;
            var a = dialogYes;
            CloseDialog();
            a?.Invoke();
        }

        /// <summary>Default settings (497), remake (players' request): the dialog asks whether to reset only the open tab or
        /// every tab (a third button), Cancel = nothing; a stray press reset everything before.</summary>
        void ShowResetChoice()
        {
            var page = CurrentOptionPage();
            ShowDialog(Localization.Get(497), OptionsCatalog.ResetQuestion, () => { OptionsCatalog.ResetPage(page); RefreshTexts(); });
            dialogAlt = () => { Settings.ResetToDefaults(); RefreshTexts(); };
            var yes = root.Q<Button>("dialogYes");
            InputGlyph.SetText(yes, OptionsCatalog.ResetTabLabel(page).ToUpperInvariant());
            var alt = root.Q<Button>("dialogAlt");
            InputGlyph.SetText(alt, OptionsCatalog.ResetAllLabel.ToUpperInvariant());
            alt.style.display = DisplayStyle.Flex;
            InputGlyph.SetText(root.Q<Button>("dialogNo"), Localization.Get(425).ToUpperInvariant());   // Cancel
            Select(yes);
        }

        /// <summary>The Options tab on show (SelectTab).</summary>
        OptionPage CurrentOptionPage()
        {
            foreach (OptionPage p in Enum.GetValues(typeof(OptionPage)))
                if (PageName(p) == currentOptionsPage) return p;
            return OptionPage.Sound;
        }

        string currentOptionsPage = "soundPage";

        void CloseDialog()
        {
            var no = root.Q<Button>("dialogNo");
            var alt = root.Q<Button>("dialogAlt");
            if (alt != null && alt.style.display == DisplayStyle.Flex)
            {
                // The reset choice: back to Yes / No.
                alt.style.display = DisplayStyle.None;
                InputGlyph.SetText(no, Localization.Get(135).ToUpperInvariant());
                InputGlyph.SetText(root.Q<Button>("dialogYes"), Localization.Get(134).ToUpperInvariant());
            }
            dialogAlt = null;
            if (no.style.display == DisplayStyle.None || dialogField != null && dialogField.style.display == DisplayStyle.Flex)
            {
                no.style.display = StyleKeyword.Null;
                InputGlyph.SetText(no, Localization.Get(135).ToUpperInvariant());
                InputGlyph.SetText(root.Q<Button>("dialogYes"), Localization.Get(134).ToUpperInvariant());
            }
            if (dialogField != null) dialogField.style.display = StyleKeyword.Null;
            dialog.RemoveFromClassList("dialog-backdrop--shown");
            dialogYes = dialogNo = null;
            dialogCheck = null;
            var back = dialogReturn;
            dialogReturn = null;
            if (back != null && back.panel != null && openPanel != null && openPanel.Contains(back) && back.enabledInHierarchy
                && back.resolvedStyle.display != DisplayStyle.None) Select(back);
            else if (openPanel != null) FocusFirst(openPanel);
            else Select(exitButton);
        }

        // ---- save slots (RecordHandler: 12 slots, slot 0 = Auto-save; SaveGame) ------------

        /// <summary>Modified: a save clicked to load flashes for this long, then fades back while the load waits as long again;
        /// clicks meanwhile are ignored (slotChosen).</summary>
        const long SlotFlashMs = 180;
        bool slotChosen;

        void BuildSlots()
        {
            var list = root.Q<ScrollView>("slotList");
            list.Clear();
            var db = Database.Load();
            for (int i = 0; i < SaveGame.SlotCount; i++)
            {
                int slot = i;
                var save = SaveGame.Preview(i);
                var row = SaveSlotRow.Build(db, i, save, Localization.Extra("autosaveHint", "Saved automatically when you dock"), DeleteSlot);
                row.clicked += () =>
                {
                    if (SaveSlotRow.HoldUsed(row)) return;
                    if (save == null) { Play(buttonPush); return; }
                    if (slotChosen) return;
                    // Modified: a save chosen to load sounds and flashes like the menu buttons (their click sound; the pressed
                    // amber kept a moment: slot-row--chosen, MainMenu.uss) before the game loads.
                    Play(buttonRelease);
                    slotChosen = true;
                    row.AddToClassList("slot-row--chosen");
                    row.schedule.Execute(() => row.RemoveFromClassList("slot-row--chosen")).ExecuteLater(SlotFlashMs);
                    row.schedule.Execute(() => { slotChosen = false; LoadSlot(slot); }).ExecuteLater(SlotFlashMs * 2);
                };
                HookFocusSound(row);
                list.Add(row);
            }
            SaveSlotRow.AttachHint(list);
        }

        /// <summary>Remake: a slot held down (SaveSlotRow): deleted, the list rebuilt with the same row selected.</summary>
        void DeleteSlot(int slot)
        {
            if (!SaveGame.Delete(slot)) return;
            Play(buttonRelease);
            root.schedule.Execute(() =>
            {
                BuildSlots();
                var list = root.Q<ScrollView>("slotList");
                if (slot < list.contentContainer.childCount) Select(list.contentContainer.ElementAt(slot));
                resumeButton.EnableInClassList("menu-button--gone", SaveGame.MostRecentSlot() < 0);
                UpdateColumnFit();
            });
        }

        // ---- options ---------------------------------------------------------------------------

        static readonly (string tab, string page)[] OptionPages =
        {
            ("tabSound", "soundPage"), ("tabGraphics", "graphicsPage"), ("tabControls", "controlsPage"), ("tabBindings", "bindingsPage"), ("tabGameplay", "gameplayPage"),
            ("tabAccessibility", "accessibilityPage"), ("tabLanguage", "languagePage"),
        };

        static string PageName(OptionPage p) => p switch
        {
            OptionPage.Sound => "soundPage",
            OptionPage.Graphics => "graphicsPage",
            OptionPage.Controls => "controlsPage",
            OptionPage.Bindings => "bindingsPage",   // remake: the key bindings on their own tab
            OptionPage.Language => "languagePage",   // under the language buttons
            OptionPage.Accessibility => "accessibilityPage",
            _ => "gameplayPage",
        };

        /// <summary>One row per OptionsCatalog entry on its tab, then the language buttons.</summary>
        void SetupOptions()
        {
            optionControls.Clear();
            foreach (var def in OptionsCatalog.All())
            {
                if (def.inGameOnly) continue;
                var c = new OptionControl(def);
                c.Field.AddToClassList("option-row");
                HookFocusSound(c.Field);
                if (def.kind == OptionKind.Choice || def.kind == OptionKind.Toggle) c.Changed += () => Play(buttonRelease);
                // Options depend on each other (STP turns MSAA off): every row follows a change.
                c.Changed += () => { foreach (var o in optionControls) if (o != c) o.Refresh(); UpdateDebugButton(); };
                // Original: the FX volume plays a sample on release; remake: the voice volume a voice line.
                if (def.id == "sfx") c.Field.RegisterCallback<PointerCaptureOutEvent>(_ => Play(infoSound));
                if (def.id == "voice") c.Field.RegisterCallback<PointerCaptureOutEvent>(_ => PlayVoicePreview());
                if (def.page == OptionPage.Language) c.Root.AddToClassList("language-voice-row");
                root.Q(PageName(def.page)).Add(c.Root);
                optionControls.Add(c);
            }

            var langList = root.Q("languageList");
            for (int i = 0; i < languageCodes.Length && i < languageNames.Length; i++)
            {
                string code = languageCodes[i];
                var b = new Button { text = languageNames[i], name = "lang_" + code };
                b.AddToClassList("menu-button");
                b.AddToClassList("language-button");
                b.clicked += () => { Play(buttonRelease); Settings.Language = code; LoadLanguage(code); RefreshTexts(); };
                HookFocusSound(b);
                langList.Add(b);
            }
        }

        string SaveTransferTitle(bool import) => (import ? Localization.Extra("importSaves", "Import saves") : Localization.Extra("exportSaves", "Export saves"));

        void ExportSaves()
        {
            if (dialog.ClassListContains("dialog-backdrop--shown")) return;
            var r = SaveTransfer.Export();
            switch (r.outcome)
            {
                case SaveTransfer.Outcome.Done:
                    ShowNotice(SaveTransferTitle(false), string.Format(Localization.Extra("exportDone", "{0} saves exported to:\n{1}"), r.slots, r.path));
                    break;
                case SaveTransfer.Outcome.NothingToExport:
                    ShowNotice(SaveTransferTitle(false), Localization.Extra("exportNothing", "There are no saves to export."));
                    break;
                case SaveTransfer.Outcome.Failed:
                    ShowNotice(SaveTransferTitle(false), Localization.Extra("exportFailed", "Export failed:") + " " + r.problem);
                    break;
            }
        }

        /// <summary>Import, step 1: the file (picked, or the newest in the Transfer folder), checked whole; then the warning.</summary>
        void PickImport()
        {
            if (dialog.ClassListContains("dialog-backdrop--shown")) return;
            string path = SaveTransfer.PickImportFile();
            var r = SaveTransfer.Check(path);
            switch (r.outcome)
            {
                case SaveTransfer.Outcome.Cancelled:
                    return;
                case SaveTransfer.Outcome.NoFile:
                    ShowNotice(SaveTransferTitle(true), string.Format(Localization.Extra("importNoFile", "No exported saves (.{0}) found in:\n{1}"),
                        SaveTransfer.Extension, SaveTransfer.TransferFolder));
                    return;
                case SaveTransfer.Outcome.Invalid:
                case SaveTransfer.Outcome.Failed:
                    ShowNotice(SaveTransferTitle(true), string.Format(Localization.Extra("importInvalid", "This file can't be imported, your saves are unchanged.\n{0}: {1}"),
                        System.IO.Path.GetFileName(path), r.problem));
                    return;
            }
            // Step 2: the warning; nothing is written before Yes.
            ShowDialog(SaveTransferTitle(true),
                string.Format(Localization.Extra("importWarning",
                    "WARNING: importing overwrites ALL your existing saves, the auto-save too, with the {0} saves in \"{1}\". Export them first if you want to keep them. Import anyway?"),
                    r.slots, System.IO.Path.GetFileName(path)),
                () => root.schedule.Execute(() => ImportSaves(path)).ExecuteLater(1));
        }

        void ImportSaves(string path)
        {
            var r = SaveTransfer.Import(path);
            BuildSlots();   // the Load game list shows the imported saves
            resumeButton.EnableInClassList("menu-button--gone", SaveGame.MostRecentSlot() < 0);
            UpdateColumnFit();
            if (r.outcome == SaveTransfer.Outcome.Done)
                ShowNotice(SaveTransferTitle(true), string.Format(Localization.Extra("importDone", "{0} saves imported."), r.slots));
            else
                ShowNotice(SaveTransferTitle(true), string.Format(Localization.Extra("importInvalid", "This file can't be imported, your saves are unchanged.\n{0}: {1}"),
                    System.IO.Path.GetFileName(path), r.problem));
        }

        // ---- the original game's saves (OriginalSaveTransfer) ----------------------------------------------------------

        bool pickingOriginal;
        string OriginalImportTitle => Localization.Extra("importOriginalSave", "Import original save");
        string OriginalExportTitle => Localization.Extra("exportOriginalSaves", "Export for original game");

        /// <summary>Import, step 1: the system's file picker (polled until it answers, like the mod browser's).</summary>
        void PickOriginalImport()
        {
            if (pickingOriginal || dialog.ClassListContains("dialog-backdrop--shown")) return;
            pickingOriginal = true;
            var poll = Modding.ModImport.Pick(OriginalImportTitle, "Galaxy on Fire 2 saves (gof2_save_game_N)", new[] { "*" });
            Modding.ModImport.Picked picked = null;
            root.schedule.Execute(() =>
            {
                picked = poll();
                if (picked != null) { pickingOriginal = false; OriginalPicked(picked); }
            }).Every(200).Until(() => picked != null);
        }

        /// <summary>Step 2: the file read and converted (nothing written yet), then the question with what it is.</summary>
        void OriginalPicked(Modding.ModImport.Picked p)
        {
            if (p.cancelled) return;
            if (p.error != null) { ShowNotice(OriginalImportTitle, Localization.Extra("originalImportFailed", "The save couldn't be imported:") + " " + p.error); return; }
            var imported = OriginalSaveTransfer.Inspect(p.path);
            Modding.ModImport.Discard(p.path);
            if (imported.problem != null)
            {
                ShowNotice(OriginalImportTitle, Localization.Extra("originalImportFailed", "The save couldn't be imported:") + "\n" + p.name + ": " + imported.problem
                    + "\n" + Localization.Extra("originalImportHint", "Pick a gof2_save_game_N file from the original game's save folder."));
                return;
            }
            int slot = OriginalSaveTransfer.FreeSlot();
            if (slot < 0)
            {
                ShowNotice(OriginalImportTitle, Localization.Extra("originalImportNoSlot", "No empty save slot: hold a save in the list to delete it."));
                return;
            }
            string notes = imported.notes.Count > 0 ? "\n(" + string.Join("; ", imported.notes) + ")" : "";
            ShowDialog(OriginalImportTitle,
                string.Format(Localization.Extra("originalImportAsk", "{0}\nfrom {1}.\nImport it into save slot {2}?"), imported.summary, imported.version, slot) + notes,
                () => root.schedule.Execute(() =>
                {
                    string problem = OriginalSaveTransfer.Import(imported, slot);
                    BuildSlots();
                    resumeButton.EnableInClassList("menu-button--gone", SaveGame.MostRecentSlot() < 0);
                    UpdateColumnFit();
                    ShowNotice(OriginalImportTitle, problem == null
                        ? string.Format(Localization.Extra("originalImportDone", "Imported into save slot {0}. Load it from the list."), slot)
                        : Localization.Extra("originalImportFailed", "The save couldn't be imported:") + " " + problem);
                }).ExecuteLater(1));
        }

        /// <summary>Export: what it does and which game takes the files, then every slot written.</summary>
        void AskOriginalExport()
        {
            if (dialog.ClassListContains("dialog-backdrop--shown")) return;
            ShowDialog(OriginalExportTitle, Localization.Extra("originalExportAsk",
                "Write every slot as original Android save files into a new folder? They load in the community builds; mod items are left out."),
                () => root.schedule.Execute(() =>
                {
                    var r = OriginalSaveTransfer.Export();
                    if (r.problem != null) { ShowNotice(OriginalExportTitle, Localization.Extra("exportFailed", "Export failed:") + " " + r.problem); return; }
                    string notes = r.notes.Count > 0 ? "\n(" + string.Join("; ", r.notes) + ")" : "";
                    ShowNotice(OriginalExportTitle, string.Format(Localization.Extra("exportDone", "{0} saves exported to:\n{1}"), r.slots, r.folder) + notes);
                }).ExecuteLater(1));
        }

        void SelectTab(string page)
        {
            currentOptionsPage = page;
            foreach (var (tab, p) in OptionPages)
            {
                root.Q(tab).EnableInClassList("tab-button--active", p == page);
                root.Q(p).EnableInClassList("tab-page--active", p == page);
            }
        }

        void ApplySettings()
        {
            if (musicSource != null && screen != MenuState.Leaving && !fadingMusic) musicSource.volume = Settings.MusicVolume;
            if (sfxSource != null) sfxSource.volume = Settings.SfxVolume;
            if (voiceSource != null) voiceSource.volume = Settings.VoiceVolume;
        }

        // ---- text ------------------------------------------------------------------------------

        void LoadLanguage(string code)
        {
            int i = Array.IndexOf(languageCodes, code);
            if (i < 0 || languageTables == null || i >= languageTables.Length || languageTables[i] == null) i = 0;
            if (languageTables != null && languageTables.Length > 0) Localization.Load(languageCodes[i], languageTables[i]);
        }

        void RefreshTexts()
        {
            string T(int id) => Localization.Get(id).ToUpperInvariant();
            void Set(string name, string text)
            {
                var e = root.Q<TextElement>(name);
                if (e is Button b) InputGlyph.SetText(b, text);   // its text may be in a label beside a glyph (PadPrompt)
                else if (e != null) e.text = text;
            }

            Set("resumeButton", T(41));
            Set("newGameButton", T(28));
            Set("loadButton", T(29));
            Set("optionsButton", T(31));
            Set("modsButton", Localization.Extra("modsButton", "Mods").ToUpperInvariant());
            Set("aboutButton", T(43));
            Set("exitButton", T(33));
            Set("debugButton", Localization.Extra("debugButton", "Debug").ToUpperInvariant());
            foreach (var n in new[] { "campaignBack", "difficultyBack", "economyBack", "gameOptionsBack", "loadBack", "optionsBack", "aboutBack", "multiplayerBack" }) Set(n, "‹  " + T(170));
            string mp = Localization.Extra("multiplayer", "Multiplayer").ToUpperInvariant();
            Set("multiplayerButton", mp);
            Set("multiplayerTitle", mp);
            Set("mpBadge", Localization.Extra("mpExperimental", "Experimental").ToUpperInvariant());
            Set("mpIntro", Localization.Extra("mpIntroShort", "Meet other pilots, form squads and fly missions together."));
            Set("mpNameLabel", Localization.Extra("mpNameLabel", "Pilot name").ToUpperInvariant());
            if (mpName != null) mpName.textEdition.placeholder = Localization.Extra("mpNamePlaceholder", "Your pilot name");
            Set("mpHostTitle", Localization.Extra("mpHostTitle", "Host a game").ToUpperInvariant());
            Set("mpModePublic", Localization.Extra("mpModePublic", "Public").ToUpperInvariant());
            Set("mpModePrivate", Localization.Extra("mpModePrivate", "Invite only").ToUpperInvariant());
            Set("mpModeLocal", Localization.Extra("mpModeLocal", "Local network").ToUpperInvariant());
            ApplyHostMode();
            var hostPw = root.Q<TextField>("mpHostPassword");
            if (hostPw != null) hostPw.textEdition.placeholder = Localization.Extra("mpHostPasswordNone", "No password");   // empty: anyone joins
            Set("mpMaxPlayersLabel", Localization.Extra("mpMaxPlayers", "Max players").ToUpperInvariant());
            Set("mpDebugLabel", Localization.Extra("mpDebugMenu", "Debug menu").ToUpperInvariant());
            Set("mpDebugOff", Localization.Extra("mpDebugOff", "Off").ToUpperInvariant());
            Set("mpDebugOn", Localization.Extra("mpDebugAllowed", "Allowed").ToUpperInvariant());
            Set("mpPvpLabel", Localization.Extra("mpCombat", "Combat").ToUpperInvariant());
            Set("mpPvpOff", Localization.Extra("mpPve", "PvE").ToUpperInvariant());
            Set("mpPvpOn", Localization.Extra("mpPvp", "PvP").ToUpperInvariant());
            Set("mpWorldLabel", Localization.Extra("mpWorld", "World").ToUpperInvariant());
            Set("mpWorldFresh", Localization.Extra("mpWorldFresh", "Fresh").ToUpperInvariant());
            Set("mpWorldPersistent", Localization.Extra("mpWorldPersistent", "Persistent").ToUpperInvariant());
            Set("mpModsOff", Localization.Extra("mpDebugOff", "Off").ToUpperInvariant());
            Set("mpModsOn", Localization.Extra("mpDebugAllowed", "Allowed").ToUpperInvariant());
            ApplyHostMods();
            if (mpJoinPassword != null) mpJoinPassword.textEdition.placeholder = Localization.Extra("mpJoinPasswordHint", "Password");
            Set("mpPortLabel", Localization.Extra("mpPortLabel", "Port").ToUpperInvariant());
            Set("mpHost", Localization.Extra("mpHost", "Host").ToUpperInvariant());
            Set("mpJoinTitle", Localization.Extra("mpBrowserTitle", "Server browser").ToUpperInvariant());
            Set("mpJoinText", Localization.Extra("mpJoinCodeLabel", "Or join with a code or an address").ToUpperInvariant());
            if (mpAddress != null) mpAddress.textEdition.placeholder = "ABC123  /  192.168.1.20";
            Set("mpJoin", Localization.Extra("mpJoin", "Join").ToUpperInvariant());

            Set("campaignTitle", T(103));
            Set("debugTitle", Localization.Extra("debugTitle", "Debug").ToUpperInvariant());
            Set("debugBack", "‹  " + T(170));
            Set("debugCheatsTitle", Localization.Extra("debugCheats", "Cheats").ToUpperInvariant());
            Set("debugNote", Localization.Extra("debugNote", "Credits, repairs, ammo and more: the Debug page of the pause and station menus."));
            foreach (var c in debugControls) c.Refresh();
            Set("debugMissionsTitle", Localization.Extra("missionSelect", "Start at mission").ToUpperInvariant());
            if (missionFilter != null) missionFilter.textEdition.placeholder = Localization.Extra("missionSearch", "Search: a step number, a station, a word...");
            Set("difficultyTitle", T(517));
            Set("easyLabel", T(518));
            Set("easyDesc", Localization.Extra("easyDesc", "Weaker enemies in smaller groups."));
            Set("normalLabel", T(519));
            Set("normalDesc", Localization.Extra("normalDesc", "The classic Galaxy on Fire 2 experience."));
            Set("hardLabel", T(520));
            Set("hardDesc", Localization.Extra("hardDesc", "Tougher enemies in bigger groups; the economy as on Normal."));
            Set("economyTitle", Localization.Extra("economyTitle", "Select the economy").ToUpperInvariant());
            Set("gameOptionsTitle", Localization.Extra("gameOptionsTitle", "Game options").ToUpperInvariant());
            Set("gameOptionsStartLabel", Localization.Extra("gameOptionsStart", "Start game").ToUpperInvariant());
            Set("economyDefaultLabel", Session.EconomyName(Economy.Default).ToUpperInvariant());
            Set("economyDefaultDesc", Localization.Extra("economyDefaultDesc",
                "PC / Mac / iPhone prices: cheaper goods and blueprints, dearer ships."));
            Set("economyAndroidLabel", Session.EconomyName(Economy.Android).ToUpperInvariant());
            Set("economyAndroidDesc", Localization.Extra("economyAndroidDesc",
                "Android prices: dearer goods and gear, bigger blueprints, cheaper ships."));
            RefreshKaamoToggle();
            RefreshHardcoreToggle();
            RefreshTutorialToggle();
            RefreshCapitalToggle();
            Set("extremeLabel", T(25));
            Set("extremeDesc", Localization.Extra("extremeDesc", "For veterans: tougher enemies and a harsher economy."));
            Set("loadTitle", T(29));
            Set("exportSavesButton", Localization.Extra("exportSaves", "Export saves").ToUpperInvariant());
            Set("importSavesButton", Localization.Extra("importSaves", "Import saves").ToUpperInvariant());
            Set("importOriginalButton", Localization.Extra("importOriginalSave", "Import original save").ToUpperInvariant());
            Set("exportOriginalButton", Localization.Extra("exportOriginalSaves", "Export for original game").ToUpperInvariant());
            var originalRow = root.Q("originalSaveRow");
            if (originalRow != null) originalRow.style.display = Modding.ModImport.Available ? DisplayStyle.Flex : DisplayStyle.None;
            // Without a file dialog (Linux, Android) the files go to and come from the Transfer folder: shown under the buttons.
            var transferNote = root.Q<Label>("saveTransferNote");
            if (transferNote != null)
            {
                transferNote.text = SaveTransfer.HasFileDialog ? ""
                    : Localization.Extra("saveTransferFolder", "Exported saves go to, and are imported from:") + " " + SaveTransfer.TransferFolder;
                transferNote.style.display = SaveTransfer.HasFileDialog ? DisplayStyle.None : DisplayStyle.Flex;
            }
            Set("optionsTitle", T(31));
            Set("tabSound", OptionsCatalog.PageTitle(OptionPage.Sound).ToUpperInvariant());
            Set("tabGraphics", OptionsCatalog.PageTitle(OptionPage.Graphics).ToUpperInvariant());
            Set("tabControls", OptionsCatalog.PageTitle(OptionPage.Controls).ToUpperInvariant());
            Set("tabBindings", OptionsCatalog.PageTitle(OptionPage.Bindings).ToUpperInvariant());
            Set("tabGameplay", OptionsCatalog.PageTitle(OptionPage.Gameplay).ToUpperInvariant());
            Set("tabAccessibility", OptionsCatalog.PageTitle(OptionPage.Accessibility).ToUpperInvariant());
            Set("tabLanguage", T(0));
            Set("optionsDefaults", T(497));
            Set("aboutTitle", T(43));
            Set("dialogYes", T(134));
            Set("dialogNo", T(135));

            foreach (var c in optionControls) c.Refresh();
            foreach (var b in root.Q("languageList").Query<Button>().ToList())
                b.EnableInClassList("language-button--active", b.name == "lang_" + Localization.Language);

            UpdatePressAnyKey();
            versionLabel.text = VersionText;
            RefreshUpdateButton();
            hintLabel.text = Localization.Extra("hint", "↑ ↓  NAVIGATE     ENTER  SELECT     ESC  " + T(170));
            var aboutText = root.Q<Label>("aboutText");
            aboutText.text = $"{VersionText}\n\n{AboutText.Get()}\n\n{Localization.Get(48)}";
            AboutText.Hook(aboutText);
        }

        // ---- navigation ------------------------------------------------------------------------

        void OnNavigate(NavigationMoveEvent e)
        {
            pointerDriven = false;
            if (screen != MenuState.Menu) return;
            var focused = root.focusController?.focusedElement as VisualElement;
            // Typing into a field (the address, the code, a name): W A S D and the side arrows are letters and the cursor.
            if (TextFieldKeys.IsTyping(e, focused))
            {
                e.StopPropagation();
                root.focusController?.IgnoreEvent(e);
                return;
            }
            SetTouchMode(false);
            bool vertical = e.direction == NavigationMoveEvent.Direction.Up || e.direction == NavigationMoveEvent.Direction.Down;
            bool horizontal = e.direction == NavigationMoveEvent.Direction.Left || e.direction == NavigationMoveEvent.Direction.Right;
            if (!vertical && !horizontal) return;

            if (horizontal && focused is ChoiceRow choiceRow)
            {
                choiceRow.Step(e.direction == NavigationMoveEvent.Direction.Left ? -1 : 1);
                e.StopPropagation();
                root.focusController?.IgnoreEvent(e);
                return;
            }
            if (horizontal && focused is BindingRow bindingRow)
            {
                bindingRow.Step(e.direction == NavigationMoveEvent.Direction.Left ? -1 : 1);
                e.StopPropagation();
                root.focusController?.IgnoreEvent(e);
                return;
            }

            // Sliders and toggles keep left/right for themselves.
            if (horizontal && focused is BaseField<float> || horizontal && focused is BaseField<int>) return;

            // The Debug panel's two columns (#24): left / right jump between the mission list and the cheats (a controller had
            // to step through all 160 missions to reach them), back to where the focus was in that column.
            if (horizontal && focused != null && panels.TryGetValue("debugPanel", out var debugPanel) && openPanel == debugPanel)
            {
                var missionsColumn = debugPanel.Q(className: "debug-column--missions");
                var cheatsColumn = debugPanel.Q(className: "debug-column--cheats");
                bool inMissions = missionsColumn != null && missionsColumn.Contains(focused);
                bool inCheats = cheatsColumn != null && cheatsColumn.Contains(focused);
                var to = e.direction == NavigationMoveEvent.Direction.Right && inMissions ? cheatsColumn
                       : e.direction == NavigationMoveEvent.Direction.Left && inCheats ? missionsColumn : null;
                if (to != null)
                {
                    if (inMissions) debugMissionFocus = focused; else debugCheatFocus = focused;
                    var remembered = to == cheatsColumn ? debugCheatFocus : debugMissionFocus;
                    var columnItems = Focusables(to).FindAll(v => !(v is TextField));
                    var target = remembered != null && columnItems.Contains(remembered) ? remembered : columnItems.Count > 0 ? columnItems[0] : null;
                    if (target != null) { target.Focus(); EnsureVisible(target); }
                    e.StopPropagation();
                    root.focusController?.IgnoreEvent(e);
                    return;
                }
            }

            if (NavigateGameOptions(e, focused)) return;
            if (NavigateOptions(e, focused)) return;
            if (NavigateMultiplayer(e, focused)) return;

            var scope = dialog.ClassListContains("dialog-backdrop--shown") ? dialog : openPanel ?? mainButtons;
            var items = Focusables(scope);
            if (scope == mainButtons && updateButton != null && updateButton.focusable && updateRow.ClassListContains("update-row--shown"))
                items.Add(updateButton);   // "Update available" below the list
            if (items.Count == 0) return;
            int i = focused != null ? items.IndexOf(focused) : -1;
            int step = e.direction == NavigationMoveEvent.Direction.Up || e.direction == NavigationMoveEvent.Direction.Left ? -1 : 1;
            // The main list wraps round (docs/UI-DESIGN.md "Navigation": a single-axis list wraps, XAG 112); a panel's
            // items and a dialog's buttons stop at their ends.
            bool wrap = scope == mainButtons && vertical;
            int next = i < 0 ? 0 : wrap ? (i + step + items.Count) % items.Count : Mathf.Clamp(i + step, 0, items.Count - 1);
            items[next].Focus();
            e.StopPropagation();
            root.focusController?.IgnoreEvent(e);
        }

        /// <summary>The Options panel (a player's report; the in-game Options' rules, OptionsView): up / down walk the active
        /// tab, the active page's items and the footer (Back and Default settings as one row), stopping at the ends; left /
        /// right switch tabs on the tab row, flip a toggle (sliders, choices and bindings step themselves, above), move
        /// between the language buttons and between the footer's buttons, and leave the other rows alone. Before, the whole
        /// panel was one list: down stepped through the tabs, right ran on to the next row. LB / RB (Q / E) switch tabs
        /// anywhere (UpdateOptionTabs), A on a tab goes into it. False: not this panel, or a dialog is open.</summary>
        bool NavigateOptions(NavigationMoveEvent e, VisualElement focused)
        {
            if (dialog.ClassListContains("dialog-backdrop--shown")) return false;
            if (!panels.TryGetValue("optionsPanel", out var panel) || openPanel != panel) return false;
            var activeTab = ActiveOptionsTab();
            var items = OptionPageItems();
            var footer = Focusables(panel.Q(className: "options-footer"));
            bool onTab = focused is Button fb && OptionsTabPage(fb) != null;
            bool inFooter = focused != null && footer.Contains(focused);
            int i = focused != null ? items.IndexOf(focused) : -1;
            bool languagePage = currentOptionsPage == "languagePage";
            VisualElement to = focused;
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Down:
                    if (onTab) to = items.Count > 0 ? items[0] : footer.Count > 0 ? footer[0] : focused;
                    else if (i >= 0)
                    {
                        var below = languagePage ? SpatialNav.Next(items, focused, 0, 1) : i + 1 < items.Count ? items[i + 1] : null;
                        to = below ?? (footer.Count > 0 ? footer[0] : focused);
                    }
                    else if (!inFooter) to = activeTab;   // nothing of this page had the focus
                    break;
                case NavigationMoveEvent.Direction.Up:
                    if (inFooter) to = items.Count > 0 ? items[items.Count - 1] : activeTab;
                    else if (i >= 0)
                    {
                        var above = languagePage ? SpatialNav.Next(items, focused, 0, -1) : i > 0 ? items[i - 1] : null;
                        to = above ?? activeTab;
                    }
                    else if (!onTab) to = activeTab;
                    break;
                case NavigationMoveEvent.Direction.Left:
                case NavigationMoveEvent.Direction.Right:
                {
                    int dir = e.direction == NavigationMoveEvent.Direction.Left ? -1 : 1;
                    if (onTab) { StepOptionsTab(dir, false); return Swallow(e); }
                    if (inFooter) to = footer[Mathf.Clamp(footer.IndexOf(focused) + dir, 0, footer.Count - 1)];
                    else if (focused is Toggle) optionControls.Find(c => c.Field == focused)?.Step(dir);
                    else if (languagePage && focused is Button && i >= 0) to = SpatialNav.Next(items, focused, dir, 0) ?? focused;
                    break;
                }
                default:
                    return false;
            }
            if (to != null && to != focused) Select(to);
            return Swallow(e);
        }

        bool Swallow(NavigationMoveEvent e)
        {
            e.StopPropagation();
            root.focusController?.IgnoreEvent(e);
            return true;
        }

        /// <summary>The page an Options tab button shows (null: not a tab).</summary>
        static string OptionsTabPage(Button b)
        {
            if (b == null) return null;
            foreach (var (tab, page) in OptionPages) if (b.name == tab) return page;
            return null;
        }

        Button ActiveOptionsTab()
        {
            foreach (var (tab, page) in OptionPages) if (page == currentOptionsPage) return root.Q<Button>(tab);
            return null;
        }

        /// <summary>What the keys walk on the active Options page, top to bottom (a greyed-out row not).</summary>
        List<VisualElement> OptionPageItems()
        {
            var page = root.Q(currentOptionsPage);
            return page != null ? Focusables(page, scopeShown: true) : new List<VisualElement>();
        }

        /// <summary>The Options tab beside the active one, wrapping round (LB / RB, Q / E, left / right on the tab row). The focus
        /// goes with it: to the new tab from the tab row, else to the new page's first item.</summary>
        void StepOptionsTab(int dir, bool intoPage)
        {
            int at = 0;
            for (int k = 0; k < OptionPages.Length; k++) if (OptionPages[k].page == currentOptionsPage) at = k;
            var (tab, page) = OptionPages[((at + dir) % OptionPages.Length + OptionPages.Length) % OptionPages.Length];
            SelectTab(page);
            var tabButton = root.Q<Button>(tab);
            Select(tabButton);
            if (intoPage) FocusPageStart(tabButton);
        }

        /// <summary>The active page's first item takes the focus once the page is laid out (a page just shown has no layout
        /// in this frame, so its items don't count as shown yet); the tab keeps it meanwhile, and when the page has none.</summary>
        void FocusPageStart(Button tab)
        {
            string page = currentOptionsPage;
            root.schedule.Execute(() =>
            {
                if (currentOptionsPage != page || !panels.TryGetValue("optionsPanel", out var panel) || openPanel != panel) return;
                var first = OptionPageItems().Find(v => true);
                if (first != null && root.focusController?.focusedElement == tab) Select(first);
            }).ExecuteLater(1);
        }

        /// <summary>LB / RB (Q / E) on the Options panel switch tabs, like the in-game Options and the hangar's (they did
        /// nothing here). Not with both shoulders held (the Debug panel's LB + RB).</summary>
        void UpdateOptionTabs(Gamepad pad)
        {
            if (screen != MenuState.Menu || dialog.ClassListContains("dialog-backdrop--shown")) return;
            if (!panels.TryGetValue("optionsPanel", out var panel) || openPanel != panel) return;
            var kb = Keyboard.current;
            int dir = 0;
            if (pad != null && pad.leftShoulder.wasPressedThisFrame && !pad.rightShoulder.isPressed) dir = -1;
            else if (pad != null && pad.rightShoulder.wasPressedThisFrame && !pad.leftShoulder.isPressed) dir = 1;
            else if (kb != null && kb.qKey.wasPressedThisFrame) dir = -1;
            else if (kb != null && kb.eKey.wasPressedThisFrame) dir = 1;
            if (dir == 0) return;
            var focused = root.focusController?.focusedElement as VisualElement;
            StepOptionsTab(dir, !(focused is Button b && OptionsTabPage(b) != null));
        }

        VisualElement lastOptionCard, lastOptionToggle;

        /// <summary>The Game options panel with the mods' option cards (RefreshModOptions): two columns. Left / right step
        /// through the cards (the first card's left goes back to the toggles), right from a toggle or Start goes to the cards
        /// (the one last on); up / down stay in the toggles, Start and Back (down from a card: Start). False: not this case.</summary>
        bool NavigateGameOptions(NavigationMoveEvent e, VisualElement focused)
        {
            if (focused == null || modOptionCards == null || dialog.ClassListContains("dialog-backdrop--shown")) return false;
            if (!panels.TryGetValue("gameOptionsPanel", out var panel) || openPanel != panel) return false;
            if (modOptionCards.parent == null || modOptionCards.parent.resolvedStyle.display == DisplayStyle.None) return false;
            var cards = Focusables(modOptionCards.contentContainer);
            if (cards.Count == 0) return false;
            bool onCard = modOptionCards.Contains(focused);
            var d = e.direction;
            VisualElement to = null;
            if (onCard)
            {
                lastOptionCard = focused;
                int i = cards.IndexOf(focused);
                if (d == NavigationMoveEvent.Direction.Right) to = cards[Mathf.Min(i + 1, cards.Count - 1)];
                else if (d == NavigationMoveEvent.Direction.Left)
                    to = i > 0 ? cards[i - 1] : lastOptionToggle != null && panel.Contains(lastOptionToggle) ? lastOptionToggle : panel.Q("kaamoToggle");
                else if (d == NavigationMoveEvent.Direction.Down) to = panel.Q("gameOptionsStart");
                else to = focused;   // up: stays
            }
            else
            {
                var column = Focusables(panel).FindAll(v => !modOptionCards.Contains(v));
                if (d == NavigationMoveEvent.Direction.Right)
                {
                    lastOptionToggle = focused;
                    to = lastOptionCard != null && cards.Contains(lastOptionCard) ? lastOptionCard : cards[0];
                }
                else if (d == NavigationMoveEvent.Direction.Left) to = focused;
                else
                {
                    int i = column.IndexOf(focused);
                    if (i < 0) return false;
                    to = column[Mathf.Clamp(i + (d == NavigationMoveEvent.Direction.Up ? -1 : 1), 0, column.Count - 1)];
                }
            }
            if (to == null) return false;
            if (to != focused) Select(to);
            e.StopPropagation();
            root.focusController?.IgnoreEvent(e);
            return true;
        }

        /// <summary>The Multiplayer panel (a Discord report: with a controller the host card couldn't be reached from the server
        /// browser): the neighbour on screen (SpatialNav), so left / right cross between the browser and the host card and up /
        /// down stay in a column; across with nothing in the same row, the nearest item that way; the target is scrolled into
        /// view (the host card's settings scroll). Before, the panel was one list in document order. False: not this panel,
        /// or a dialog is open.</summary>
        bool NavigateMultiplayer(NavigationMoveEvent e, VisualElement focused)
        {
            if (dialog.ClassListContains("dialog-backdrop--shown")) return false;
            if (!panels.TryGetValue("multiplayerPanel", out var panel) || openPanel != panel) return false;
            var items = Focusables(panel);
            if (items.Count == 0) return false;
            VisualElement to;
            if (focused == null || !items.Contains(focused)) to = items.Find(v => !(v is TextField)) ?? items[0];
            else
            {
                int dx = e.direction == NavigationMoveEvent.Direction.Left ? -1 : e.direction == NavigationMoveEvent.Direction.Right ? 1 : 0;
                int dy = e.direction == NavigationMoveEvent.Direction.Up ? -1 : e.direction == NavigationMoveEvent.Direction.Down ? 1 : 0;
                // Up / down stay in the card (the two cards' rows don't line up: the nearest row was often the other card's),
                // then leave it (the name field above, Back below).
                var card = CardOf(focused);
                if (dy != 0)
                {
                    to = card != null ? SpatialNav.Next(items, focused, 0, dy, v => CardOf(v) == card) : null;
                    to ??= SpatialNav.Next(items, focused, 0, dy, card != null ? v => CardOf(v) == null : null);
                }
                else if (card != null)
                {
                    // Across: the row's neighbour in either card, else the nearest item of the other card on that side.
                    to = SpatialNav.Next(items, focused, dx, 0, v => CardOf(v) != null);
                    if (to == null)
                    {
                        var f = focused.worldBound;
                        float best = float.MaxValue;
                        foreach (var c in items)
                        {
                            var r = c.worldBound;
                            var other = CardOf(c);
                            if (other == null || other == card || float.IsNaN(r.x) || (r.center.x - f.center.x) * dx <= 1f) continue;
                            float d = (r.center - f.center).sqrMagnitude;
                            if (d < best) { best = d; to = c; }
                        }
                    }
                }
                else to = SpatialNav.Next(items, focused, dx, 0, v => CardOf(v) == null);   // the name field, Back
                to ??= focused;
            }
            if (to != focused) { to.Focus(); EnsureVisible(to); }
            e.StopPropagation();
            root.focusController?.IgnoreEvent(e);
            return true;
        }

        /// <summary>The multiplayer panel's card (the server browser or the host card) 'v' is in, null = neither.</summary>
        static VisualElement CardOf(VisualElement v)
        {
            for (var p = v; p != null; p = p.parent)
                if (p.ClassListContains("mp-card")) return p;
            return null;
        }

        /// <summary>What the keys can reach in 'scope', in order. 'scopeShown': the scope itself counts as shown whatever its
        /// resolved style says (an Options page just switched to: its style resolves with the next repaint).</summary>
        static List<VisualElement> Focusables(VisualElement scope, bool scopeShown = false)
        {
            var list = new List<VisualElement>();
            var stop = scopeShown ? scope : scope.parent;
            scope.Query<VisualElement>().Where(v => v.focusable && v.canGrabFocus && v.enabledInHierarchy && (v == scope || v.resolvedStyle.display != DisplayStyle.None)
                                                     && !(v.parent is BaseField<float>) && !(v.parent is BaseField<int>) && !(v.parent is Toggle)
                                                     && !InsideTextField(v) && IsShown(v, stop))
                .ForEach(list.Add);
            return list;
        }

        /// <summary>A text field's own parts (its TextInput and text element): the field is the one stop. Listed too, the
        /// D-pad stepped from the field onto its own input, which hands the focus back to the field: the multiplayer panel's
        /// name field (its first item) never let a controller go (#20).</summary>
        static bool InsideTextField(VisualElement v)
        {
            for (var p = v.parent; p != null; p = p.parent)
                if (p is TextField) return true;
            return false;
        }

        /// <summary>No element from 'v' up to (not including) 'stop' is display: none.</summary>
        static bool IsShown(VisualElement v, VisualElement stop)
        {
            for (var p = v; p != null && p != stop; p = p.parent)
                if (p.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }

        void FocusFirst(VisualElement scope)
        {
            var items = Focusables(scope);
            // The difficulty list starts on Normal (the original's first entry), Easy above it; the economy on the last one picked.
            var normal = scope.name == "difficultyPanel" ? scope.Q<Button>("normalButton")
                : scope.name == "economyPanel" ? scope.Q<Button>(Session.Economy == Economy.Android ? "economyAndroidButton" : "economyDefaultButton")
                : scope.name == "gameOptionsPanel" ? scope.Q<Button>("gameOptionsStart") : null;
            // Not on a text field (the multiplayer panel's name is its first item): focused, it takes the keys, and on the
            // Xbox it can bring up the system keyboard, which then takes B (#20). The fields are one step away.
            var first = items.Find(v => !(v is TextField));
            if (normal != null && items.Contains(normal)) Select(normal);
            else if (first != null) Select(first);
            else if (items.Count > 0) Select(items[0]);
        }

        static void SetFocusable(VisualElement scope, bool on)
        {
            // Modified: and clickable: the dimmed main buttons behind an open panel still took clicks and taps.
            foreach (var b in scope.Query<Button>().ToList())
            {
                b.focusable = on;
                b.pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
            }
        }

        static void EnsureVisible(VisualElement v)
        {
            for (var p = v.parent; p != null; p = p.parent)
                if (p is ScrollView sv) { sv.ScrollTo(v); return; }
        }

        // ---- audio -----------------------------------------------------------------------------

        bool fadingMusic;

        IEnumerator FadeMusic(float target, float seconds)
        {
            if (musicSource == null) yield break;
            fadingMusic = true;
            float start = musicSource.volume;
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                musicSource.volume = Mathf.Lerp(start, target, t / seconds);
                yield return null;
            }
            musicSource.volume = target;
            fadingMusic = false;
        }

        void PlayVoicePreview()
        {
            var clips = Settings.GermanVoices && voicePreviewGerman != null && voicePreviewGerman.Length > 0
                ? voicePreviewGerman : voicePreviewEnglish;
            if (voiceSource == null || clips == null || clips.Length == 0) return;
            voiceSource.Stop();   // one line at a time, a new release restarts it
            voiceSource.clip = GoF2Remake.Modding.ModSounds.Get(clips[voicePreviewIndex++ % clips.Length]);
            voiceSource.volume = Settings.VoiceVolume;
            voiceSource.Play();
        }

        void Play(AudioClip clip, float volume = 1f)
        {
            if (clip != null && sfxSource != null) sfxSource.PlayOneShot(GoF2Remake.Modding.ModSounds.Get(clip), volume);
        }

        // ---- save folder (remake, a player's request; Data.SaveFolder) ------------------------------------
        // The Options > Gameplay row's press (OptionsCatalog.SaveFolderPressed) asks whether to pick another folder or go back
        // to this device only, runs the system's folder picker, and for a folder that already holds saves (or with saves on
        // this device) asks how to start: keep both (the newer save of each slot wins, the other is kept in Saves/Deleted)
        // or only the folder's (this device's set aside in Saves/BeforeFolder). The menu also syncs with the folder on entry
        // and before the Load list, and says once per run when the folder can't be reached.

        static bool saveFolderNoticeShown;
        bool pickingSaveFolder;

        static string X(string key, string english) => Localization.Extra(key, english);
        static string SaveFolderTitle => X("saveFolderTitle", "Save folder");

        /// <summary>On entering the menu and before a save list: the folder's newer saves in, this device's out.</summary>
        void SyncSaveFolder(bool tellIfUnreachable)
        {
            if (!SaveFolder.Chosen) return;
            var r = SaveFolder.Sync();
            if (r.outcome == SaveFolder.Outcome.Unreachable && tellIfUnreachable && !saveFolderNoticeShown)
            {
                saveFolderNoticeShown = true;
                root.schedule.Execute(() =>
                {
                    if (dialog.ClassListContains("dialog-backdrop--shown")) return;
                    ShowNotice(SaveFolderTitle, string.Format(X("saveFolderUnreachableNotice",
                        "Can't reach the save folder {0} ({1}). Saves stay on this device and sync when it's back."),
                        SaveFolder.DisplayName, r.problem));
                }).ExecuteLater(300);
            }
            else if (r.outcome == SaveFolder.Outcome.Done) saveFolderNoticeShown = false;
            if (r.Changed) RefreshAfterSaveFolder();
        }

        void RefreshAfterSaveFolder()
        {
            resumeButton?.EnableInClassList("menu-button--gone", SaveGame.MostRecentSlot() < 0);
            UpdateColumnFit();
            foreach (var o in optionControls) o.Refresh();
            if (openPanel != null && openPanel.name == "loadPanel") BuildSlots();
        }

        /// <summary>The row's press: pick a folder, or (one is chosen) pick another or go back to this device only.</summary>
        void ChooseSaveFolder()
        {
            if (pickingSaveFolder || dialog.ClassListContains("dialog-backdrop--shown")) return;
            if (!SaveFolder.Chosen) { root.schedule.Execute(PickSaveFolder).ExecuteLater(1); return; }
            ShowChoice(SaveFolderTitle,
                string.Format(X("saveFolderCurrent", "Your saves are also kept in:\n{0}"), SaveFolder.DisplayName),
                X("saveFolderOther", "Choose another"), () => root.schedule.Execute(PickSaveFolder).ExecuteLater(1),
                X("saveFolderUseDefault", "This device only"), () =>
                {
                    SaveFolder.UseDefault();
                    ShowNotice(SaveFolderTitle, X("saveFolderDefaultDone", "Saves are kept on this device only again."));
                    RefreshAfterSaveFolder();
                });
        }

        void PickSaveFolder()
        {
            if (pickingSaveFolder) return;
            pickingSaveFolder = true;
            var poll = SaveFolderPicker.Pick();
            SaveFolderPicker.Picked picked = null;
            root.schedule.Execute(() =>
            {
                picked = poll();
                if (picked != null) { pickingSaveFolder = false; SaveFolderPicked(picked); }
            }).Every(200).Until(() => picked != null);
        }

        void SaveFolderPicked(SaveFolderPicker.Picked p)
        {
            if (p.cancelled) return;
            if (p.error != null) { ShowNotice(SaveFolderTitle, X("saveFolderFailed", "The folder can't be used:") + " " + p.error); return; }
            if (p.target == SaveFolder.Target) { SyncSaveFolder(false); return; }
            if (SamePlace(p.target, SaveGame.Folder))
            {
                ShowNotice(SaveFolderTitle, X("saveFolderOwn", "That is the game's own save folder. Choose another one."));
                return;
            }
            var s = SaveFolder.Inspect(p.target);
            if (s.problem != null) { ShowNotice(SaveFolderTitle, X("saveFolderFailed", "The folder can't be used:") + " " + s.problem); return; }
            void Take(SaveFolder.Start start)
            {
                var r = SaveFolder.Choose(p.target, p.name, start);
                RefreshAfterSaveFolder();
                ShowNotice(SaveFolderTitle, r.outcome == SaveFolder.Outcome.Done
                    ? string.Format(X("saveFolderDone", "Your saves are now also kept in:\n{0}"), SaveFolder.DisplayName)
                    : X("saveFolderFailed", "The folder can't be used:") + " " + r.problem);
            }
            if (s.localSaves == 0) { Take(SaveFolder.Start.Merge); return; }
            if (s.folderSaves == 0)
                ShowChoice(SaveFolderTitle,
                    string.Format(X("saveFolderCopyQuestion", "Copy your {0} saves into {1}?"), s.localSaves, p.name),
                    X("saveFolderCopy", "Copy them"), () => Take(SaveFolder.Start.Merge),
                    X("saveFolderStartEmpty", "Start empty"), () => Take(SaveFolder.Start.FolderOnly));
            else
                ShowChoice(SaveFolderTitle,
                    string.Format(X("saveFolderMergeQuestion",
                        "{0} holds {1} saves, this device {2}. Keep the newer save of each slot, or only the folder's?"),
                        p.name, s.folderSaves, s.localSaves),
                    X("saveFolderMerge", "Keep the newer"), () => Take(SaveFolder.Start.Merge),
                    X("saveFolderUseFolder", "Only the folder's"), () => Take(SaveFolder.Start.FolderOnly));
        }

        static bool SamePlace(string a, string b)
        {
            try
            {
                if (a.StartsWith("content://", StringComparison.Ordinal)) return false;
                return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The dialog with two answers and Cancel (the reset choice's third button).</summary>
        void ShowChoice(string title, string text, string yesLabel, Action onYes, string altLabel, Action onAlt)
        {
            ShowDialog(title, text, onYes);
            dialogAlt = onAlt;
            var yes = root.Q<Button>("dialogYes");
            InputGlyph.SetText(yes, yesLabel.ToUpperInvariant());
            var alt = root.Q<Button>("dialogAlt");
            InputGlyph.SetText(alt, altLabel.ToUpperInvariant());
            alt.style.display = DisplayStyle.Flex;
            InputGlyph.SetText(root.Q<Button>("dialogNo"), Localization.Get(425).ToUpperInvariant());   // Cancel
            Select(yes);
        }

    }
}
