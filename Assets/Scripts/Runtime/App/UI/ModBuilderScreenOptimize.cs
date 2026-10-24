// ModBuilderScreenOptimize.cs
// The Mod Builder's Optimize window (the Menu's "Optimize models and textures", the Ship tab's "Optimize…"; the user's request
// 2026-10-22): the project's models and image files with their sizes (and a model's triangles), the largest texture size
// (1K / 2K / 4K), the JPEG quality, the model textures' format and the share of the triangles kept; Optimize runs
// Modding.ModOptimizer over the files ticked, one image decode a frame (UpdateOptimize from UpdateCommon), then the game reads
// the project again. The originals stay in the undo history (an undo step before and after), so Undo puts them back.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using GoF2Remake.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public abstract partial class ModBuilderScreen
    {
        const long OptimizeTickedFrom = 1 << 20;   // files of 1 MB and more start ticked

        ModOptimizer optimizeJob;
        HashSet<string> optimizePicked;
        string optimizeFocus, optimizeSummary;
        Label optimizeProgress;

        static int OptPref(string key, int fallback, int count)
        {
            try { return Mathf.Clamp(PlayerPrefs.GetInt(key, fallback), 0, count - 1); } catch { return fallback; }
        }

        static void SetOptPref(string key, int value)
        {
            try { PlayerPrefs.SetInt(key, value); } catch { }
        }

        static string Megabytes(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576f:0.0} MB" : $"{bytes / 1024f:0} KB";

        /// <summary>Opens the Optimize window afresh (the last run's summary gone).</summary>
        protected void OpenOptimize(string focus = null)
        {
            if (optimizeJob == null) optimizeSummary = null;
            ShowOptimize(focus);
        }

        /// <summary>The Optimize window; 'focus' (a project file, e.g. the ship's model) is ticked alone the first time.</summary>
        void ShowOptimize(string focus = null)
        {
            if (Project == null) return;
            if (focus != optimizeFocus || optimizePicked == null)
            {
                optimizeFocus = focus;
                optimizePicked = null;
            }
            var files = ModOptimizer.Candidates(Project.Folder);
            if (optimizePicked == null)
                optimizePicked = new HashSet<string>(focus != null && files.Contains(focus) ? new[] { focus }
                    : files.Where(f => Size(f) >= OptimizeTickedFrom));

            modal.Clear();
            var box = Add(modal, new VisualElement(), "mbv-modal-box", "mbv-modal-box--list", "mbv-panel");
            Heading(box, T("mbOptimize", "Optimize models and textures"));
            Note(box, T("mbOptimizeNote", "Makes the mod smaller and quicker to load: the textures are scaled down and stored as JPEG unless they use transparency, and a model can lose triangles. Undo puts the files back."));
            Add(box, new Label(string.Format(T("mbOptModSize", "Whole mod: {0}"), Megabytes(Project.ModSize()))), "mbv-mod-size");

            int size = OptPref("mb_opt_size", 0, ModOptimizer.Sizes.Length);
            int quality = OptPref("mb_opt_quality", 1, ModOptimizer.Qualities.Length);
            int format = OptPref("mb_opt_format", 0, 2);
            int tris = OptPref("mb_opt_triangles", 0, ModOptimizer.TriangleShares.Length);
            bool idle = optimizeJob == null;
            if (idle)
            {
                Segments(box, T("mbOptTextureSize", "Largest texture"), new[] { "1K", "2K", "4K" }, size,
                         n => { SetOptPref("mb_opt_size", n); ShowOptimize(focus); });
                Segments(box, T("mbOptQuality", "JPEG quality"), new[] { T("mbOptHigh", "High"), T("mbOptNormal", "Normal"), T("mbOptSmall", "Small") }, quality,
                         n => { SetOptPref("mb_opt_quality", n); ShowOptimize(focus); });
                Segments(box, T("mbOptFormat", "Model textures"), new[] { T("mbOptJpeg", "JPEG where possible"), T("mbOptPng", "Keep PNG") }, format,
                         n => { SetOptPref("mb_opt_format", n); ShowOptimize(focus); });
                Segments(box, T("mbOptTriangles", "Triangles kept"), new[] { T("mbOptAll", "All"), "75%", "50%", "25%" }, tris,
                         n => { SetOptPref("mb_opt_triangles", n); ShowOptimize(focus); });
                if (tris > 0)
                    Note(box, T("mbOptTrianglesNote", "Seams and open edges are kept, so a model with many of them keeps more triangles than asked."));
            }

            var scroll = Add(box, new ScrollView(ScrollViewMode.Vertical), "mbv-picker-scroll");
            if (files.Count == 0) Note(scroll, T("mbOptNoFiles", "The project has no models or images yet."));
            foreach (var f in files)
            {
                string file = f;
                bool on = optimizePicked.Contains(file);
                string line = $"{file}  ·  {Megabytes(Size(file))}";
                if (file.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase))
                {
                    int t = ModOptimizer.CountTriangles(Path.Combine(Project.Folder, file));
                    if (t >= 0) line += "  ·  " + string.Format(T("mbOptTriangleCount", "{0} triangles"), t.ToString("N0"));
                }
                var b = Act(scroll, (on ? "✓  " : "") + line, () =>
                {
                    if (!idle) return;
                    if (!optimizePicked.Remove(file)) optimizePicked.Add(file);
                    ShowOptimize(focus);
                }, "mbv-menu-item");
                b.EnableInClassList("mbv-toggle--on", on);
            }

            optimizeProgress = Add(box, new Label(), "mbv-note");
            optimizeProgress.style.display = DisplayStyle.None;
            var row = Row(box);
            if (idle)
            {
                var go = Act(row, T("mbOptGo", "Optimize"), () => StartOptimize(new ModOptimizer.Options
                {
                    maxSize = ModOptimizer.Sizes[size],
                    quality = ModOptimizer.Qualities[quality],
                    jpeg = format == 0,
                    triangles = ModOptimizer.TriangleShares[tris]
                }));
                go.SetEnabled(optimizePicked.Count > 0);
            }
            Act(row, T("mbClose", "Close"), CloseModal);
            modal.AddToClassList("mbv-modal--shown");
            UpdateOptimizeProgress();
        }

        long Size(string rel)
        {
            try { return new FileInfo(Path.Combine(Project.Folder, rel)).Length; } catch { return 0; }
        }

        void StartOptimize(ModOptimizer.Options options)
        {
            optimizeSummary = null;
            SaveNow();
            ModBuilder.History?.Commit();   // the files as they are now: what Undo goes back to
            var files = ModOptimizer.Candidates(Project.Folder).Where(optimizePicked.Contains).ToList();
            optimizeJob = new ModOptimizer(Project.Folder, files, options);
            ShowOptimize(optimizeFocus);
        }

        /// <summary>Every frame (UpdateCommon): the running job's next step, its progress line, and when it's done the game
        /// reads the project again.</summary>
        void UpdateOptimize()
        {
            if (optimizeJob == null) return;
            optimizeJob.Step();
            UpdateOptimizeProgress();
            if (!optimizeJob.Finished) return;
            var job = optimizeJob;
            optimizeJob = null;
            optimizePicked = null;
            bool any = job.Results.Any(r => r.error == null && r.after < r.before);
            var errors = job.Results.Where(r => r.error != null).ToList();
            string summary = string.Format(T("mbOptDone", "Optimized: {0} → {1}."), Megabytes(job.Before), Megabytes(job.After));
            int trisBefore = job.Results.Sum(r => r.trianglesBefore), trisAfter = job.Results.Sum(r => r.trianglesAfter);
            if (trisAfter < trisBefore) summary += " " + string.Format(T("mbOptTrianglesDone", "Triangles: {0} → {1}."), trisBefore.ToString("N0"), trisAfter.ToString("N0"));
            if (errors.Count > 0) summary += "\n× " + string.Join("\n× ", errors.Take(3).Select(r => $"{r.path}: {r.error}"));
            optimizeSummary = any ? summary : T("mbOptNothing", "Nothing got smaller: the files are as they were.");
            Status(optimizeSummary, errors.Count > 0);
            if (any)
            {
                Visuals.ModLodCache.Forget(Project.Id);   // the levels of detail are made again from the optimized models
                RequestReload(false);
            }
            if (ModalOpen && optimizeProgress != null) ShowOptimize(optimizeFocus);
        }

        void UpdateOptimizeProgress()
        {
            if (optimizeProgress == null || optimizeProgress.panel == null) return;
            bool running = optimizeJob != null;
            optimizeProgress.style.display = running || optimizeSummary != null ? DisplayStyle.Flex : DisplayStyle.None;
            optimizeProgress.text = running
                ? string.Format(T("mbOptRunning", "Optimizing {0} ({1} / {2})…"), optimizeJob.Current ?? "", optimizeJob.Results.Count + 1, optimizeJob.Total)
                : optimizeSummary ?? "";
        }
    }
}
