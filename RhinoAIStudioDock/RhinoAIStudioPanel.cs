using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace RhinoAIStudioDock
{
    [System.Runtime.InteropServices.Guid("D53A6A3D-6DB2-45F5-903F-3A0E1E9180A6")]
    public sealed class RhinoAIStudioPanel : Panel, IPanel
    {
        public static Guid PanelId => typeof(RhinoAIStudioPanel).GUID;
        public string Title { get; } = "Rhino AI Studio";

        private sealed class ImageAttachment
        {
            public string OriginalPath { get; init; } = "";
            public string SafePath { get; init; } = "";
            public string PreviewPath { get; init; } = "";
            public string DisplayName { get; init; } = "";
        }

        private readonly WebView _web = new WebView();
        private readonly List<(string role, string name, string text)> _messages = new();
        private readonly List<ImageAttachment> _attachments = new();

        private bool _busy;
        private bool _mcpStarted;
        private bool _processingAttachments;
        private string _provider = "copilot";
        private string _modelingMode = "pro";
        private string _workingStage = "";

        private const int MaxAttachments = 4;
        private const long MaxAttachmentBytes = 12L * 1024L * 1024L;
        private const int MaxSafeImageDimension = 1600;
        private const int PreviewMaxWidth = 240;
        private const int PreviewMaxHeight = 180;

        public RhinoAIStudioPanel()
        {
            _messages.Add((
                "assistant",
                "Rhino AI Studio",
                "Ready. Choose an AI, attach a reference image if needed, then tell me what to create or modify."
            ));

            _web.DocumentLoading += OnDocumentLoading;
            Content = _web;

            Render();
            Eto.Forms.Application.Instance.AsyncInvoke(() => StartMcp(false));
        }

        private void OnDocumentLoading(object sender, WebViewLoadingEventArgs e)
        {
            if (e.Uri == null ||
                !string.Equals(e.Uri.Scheme, "rhinoai", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;

            string action = e.Uri.Host?.ToLowerInvariant() ?? "";
            var query = ParseQuery(e.Uri.Query);

            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                if (action == "send")
                {
                    string message = query.TryGetValue("message", out var m) ? m.Trim() : "";

                    if (!string.IsNullOrWhiteSpace(message) && !_busy)
                        SendMessage(message);
                }
                else if (action == "provider")
                {
                    string value = query.TryGetValue("value", out var v) ? v : "copilot";

                    if (value == "copilot" || value == "codex" || value == "claude")
                        _provider = value;

                    Render();
                }
                else if (action == "mode")
                {
                    string value = query.TryGetValue("value", out var v) ? v : "pro";

                    if (value == "fast" || value == "pro")
                        _modelingMode = value;

                    Render();
                }
                else if (action == "attach")
                {
                    AttachImages();
                }
                else if (action == "removeattachment")
                {
                    if (query.TryGetValue("index", out var raw) &&
                        int.TryParse(raw, out int index) &&
                        index >= 0 &&
                        index < _attachments.Count)
                    {
                        var removed = _attachments[index];
                        _attachments.RemoveAt(index);
                        CleanupAttachment(removed);
                    }

                    Render();
                }
                else if (action == "clearattachments")
                {
                    foreach (var attachment in _attachments)
                        CleanupAttachment(attachment);

                    _attachments.Clear();
                    Render();
                }
                else if (action == "mcp")
                {
                    StartMcp(true);
                    Render();
                }
                else if (action == "clear")
                {
                    _messages.Clear();

                    foreach (var attachment in _attachments)
                        CleanupAttachment(attachment);

                    _attachments.Clear();
                    _messages.Add(("assistant", "Rhino AI Studio", "New conversation started."));
                    Render();
                }
            });
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(query))
                return result;

            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                string[] kv = part.Split(new[] { '=' }, 2);

                string key = WebUtility.UrlDecode(kv[0] ?? "");
                string value = kv.Length > 1 ? WebUtility.UrlDecode(kv[1] ?? "") : "";

                result[key] = value;
            }

            return result;
        }

        private void StartMcp(bool addMessage)
        {
            try
            {
                bool ok = RhinoApp.RunScript("_MCPStart _Enter", false);
                _mcpStarted = ok;

                if (addMessage)
                {
                    _messages.Add((
                        "assistant",
                        "Rhino AI Studio",
                        ok ? "Rhino MCP connected." : "Could not start Rhino MCP."
                    ));
                }
            }
            catch (Exception ex)
            {
                _mcpStarted = false;

                if (addMessage)
                    _messages.Add(("assistant", "Rhino AI Studio", "MCP error: " + ex.Message));
            }
        }

        private void AttachImages()
        {
            try
            {
                if (_processingAttachments)
                    return;

                if (_attachments.Count >= MaxAttachments)
                {
                    _messages.Add((
                        "assistant",
                        "Rhino AI Studio",
                        $"You can attach up to {MaxAttachments} images per message."
                    ));
                    Render();
                    return;
                }

                var dialog = new Eto.Forms.OpenFileDialog
                {
                    MultiSelect = true,
                    Title = "Attach reference image"
                };

                dialog.Filters.Add(new FileFilter(
                    "Images",
                    ".png",
                    ".jpg",
                    ".jpeg",
                    ".webp",
                    ".gif"
                ));

                DialogResult result = dialog.ShowDialog(RhinoEtoApp.MainWindow);

                if (result != DialogResult.Ok)
                    return;

                int availableSlots = MaxAttachments - _attachments.Count;

                var selectedFiles = dialog.Filenames
                    .Where(File.Exists)
                    .Where(file => !_attachments.Any(a =>
                        string.Equals(
                            a.OriginalPath,
                            file,
                            StringComparison.OrdinalIgnoreCase)))
                    .Take(availableSlots)
                    .ToList();

                if (selectedFiles.Count == 0)
                    return;

                _processingAttachments = true;
                Render();

                Task.Run(() =>
                {
                    var processed = new List<ImageAttachment>();
                    var errors = new List<string>();

                    foreach (string file in selectedFiles)
                    {
                        try
                        {
                            var info = new FileInfo(file);

                            if (info.Length > MaxAttachmentBytes)
                            {
                                errors.Add(
                                    $"{Path.GetFileName(file)} is too large. Keep each image under 12 MB."
                                );
                                continue;
                            }

                            processed.Add(ProcessImageAttachment(file));
                        }
                        catch (Exception ex)
                        {
                            errors.Add(
                                $"{Path.GetFileName(file)} could not be processed: {ex.Message}"
                            );
                        }
                    }

                    return Tuple.Create(processed, errors);
                })
                .ContinueWith(task =>
                {
                    Eto.Forms.Application.Instance.AsyncInvoke(() =>
                    {
                        _processingAttachments = false;

                        if (task.IsFaulted)
                        {
                            _messages.Add((
                                "assistant",
                                "Rhino AI Studio",
                                "Attachment processing failed."
                            ));
                            Render();
                            return;
                        }

                        foreach (var attachment in task.Result.Item1)
                        {
                            if (_attachments.Count >= MaxAttachments)
                            {
                                CleanupAttachment(attachment);
                                break;
                            }

                            _attachments.Add(attachment);
                        }

                        foreach (string error in task.Result.Item2)
                        {
                            _messages.Add((
                                "assistant",
                                "Rhino AI Studio",
                                error
                            ));
                        }

                        Render();
                    });
                });
            }
            catch (Exception ex)
            {
                _processingAttachments = false;
                _messages.Add(("assistant", "Rhino AI Studio", "Attachment error: " + ex.Message));
                Render();
            }
        }

        private static ImageAttachment ProcessImageAttachment(string originalPath)
        {
            string tempFolder = Path.Combine(
                Path.GetTempPath(),
                "RhinoAIStudio",
                "attachments"
            );

            Directory.CreateDirectory(tempFolder);

            string token = Guid.NewGuid().ToString("N");
            string safePath = Path.Combine(tempFolder, token + "_safe.png");
            string previewPath = Path.Combine(tempFolder, token + "_preview.png");

            using var source = new Bitmap(originalPath);

            Size safeSize = FitSize(
                source.Width,
                source.Height,
                MaxSafeImageDimension,
                MaxSafeImageDimension
            );

            using (var safeBitmap = new Bitmap(
                safeSize.Width,
                safeSize.Height,
                PixelFormat.Format32bppRgba))
            {
                using (var graphics = new Graphics(safeBitmap))
                {
                    graphics.Clear(Colors.White);
                    graphics.DrawImage(
                        source,
                        new RectangleF(0, 0, safeSize.Width, safeSize.Height)
                    );
                }

                safeBitmap.Save(safePath, ImageFormat.Png);

                Size previewSize = FitSize(
                    safeSize.Width,
                    safeSize.Height,
                    PreviewMaxWidth,
                    PreviewMaxHeight
                );

                using var previewBitmap = new Bitmap(
                    previewSize.Width,
                    previewSize.Height,
                    PixelFormat.Format32bppRgba
                );

                using (var graphics = new Graphics(previewBitmap))
                {
                    graphics.Clear(Colors.White);
                    graphics.DrawImage(
                        safeBitmap,
                        new RectangleF(0, 0, previewSize.Width, previewSize.Height)
                    );
                }

                previewBitmap.Save(previewPath, ImageFormat.Png);
            }

            return new ImageAttachment
            {
                OriginalPath = originalPath,
                SafePath = safePath,
                PreviewPath = previewPath,
                DisplayName = Path.GetFileName(originalPath)
            };
        }

        private static Size FitSize(
            int width,
            int height,
            int maxWidth,
            int maxHeight)
        {
            if (width <= 0 || height <= 0)
                return new Size(1, 1);

            double scale = Math.Min(
                1.0,
                Math.Min(
                    (double)maxWidth / width,
                    (double)maxHeight / height
                )
            );

            int newWidth = Math.Max(1, (int)Math.Round(width * scale));
            int newHeight = Math.Max(1, (int)Math.Round(height * scale));

            return new Size(newWidth, newHeight);
        }

        private static void CleanupAttachment(ImageAttachment attachment)
        {
            if (attachment == null)
                return;

            foreach (string path in new[] { attachment.SafePath, attachment.PreviewPath })
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private async void SendMessage(string message)
        {
            if (_processingAttachments)
                return;

            _busy = true;

            var attachmentsForTurn = _attachments.ToList();
            _attachments.Clear();

            var safePathsForTurn = attachmentsForTurn
                .Select(a => a.SafePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .ToList();

            string attachmentNote = attachmentsForTurn.Count > 0
                ? $"\n\n[{attachmentsForTurn.Count} reference image(s) attached]"
                : "";

            _messages.Add(("user", "You", message + attachmentNote));
            Render();

            if (!_mcpStarted)
                StartMcp(false);

            string providerForTurn = _provider;
            _workingStage = _modelingMode == "pro" && providerForTurn == "copilot"
                ? "Analysing..."
                : "Working...";
            Render();

            try
            {
                string response = await Task.Run(() =>
                    RunProvider(providerForTurn, message, safePathsForTurn));

                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add((
                        "assistant",
                        ProviderDisplayName(providerForTurn),
                        string.IsNullOrWhiteSpace(response) ? "Task completed." : response
                    ));

                    _busy = false;
                    _workingStage = "";
                    Render();
                });
            }
            catch (Exception ex)
            {
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add((
                        "assistant",
                        ProviderDisplayName(providerForTurn),
                        "AI error: " + ex.Message
                    ));

                    _busy = false;
                    _workingStage = "";
                    Render();
                });
            }
            finally
            {
                foreach (var attachment in attachmentsForTurn)
                    CleanupAttachment(attachment);
            }
        }

        private string RunProvider(string provider, string userMessage, List<string> attachments)
        {
            if (provider == "copilot")
            {
                if (_modelingMode == "pro")
                    return RunCopilotPro(userMessage, attachments);

                return RunCopilot(userMessage, attachments);
            }

            if (provider == "codex")
                return RunCodex(userMessage, attachments);

            if (provider == "claude")
                return RunClaude(userMessage, attachments);

            throw new Exception("Unknown AI provider.");
        }

        private static void AppendArchitecturalIntelligenceRules(StringBuilder sb)
        {
            sb.AppendLine("ARCHITECTURAL INTELLIGENCE RULES:");
            sb.AppendLine("- First classify visible/requested geometry as architectural objects and assemblies, not only as shapes.");
            sb.AppendLine("- Think in terms of host elements, subcomponents, interfaces, profiles, thicknesses, openings, supports, joints and repeated systems.");
            sb.AppendLine("- Do not replace a coherent architectural assembly with several unrelated primitive boxes just because that is faster.");
            sb.AppendLine("- Do not BooleanUnion components that should remain constructionally distinct, such as glass versus frames, cladding versus structure, or doors versus walls.");
            sb.AppendLine("- When one manufactured/constructed component should be continuous, model it as a coherent joined Brep/profile/surface assembly rather than disconnected fragments.");
            sb.AppendLine("- Use one master profile/guide/grid for repeated members whenever possible so spacing, thickness and alignment remain consistent.");
            sb.AppendLine("- Use realistic construction hierarchy and connections, but do not claim structural/code compliance from an image alone.");
            sb.AppendLine("- If exact dimensions are unknown, use ratios and clearly inferred dimensions. If one real dimension is provided, use it as the scale reference.");
            sb.AppendLine();
            sb.AppendLine("COMMON ASSEMBLY RECIPES:");
            sb.AppendLine("WINDOW: create one coordinated wall opening; one continuous outer frame/reveal where appropriate; mullions/transoms from a consistent profile; glazing as separate coordinated panes; sill/head/jamb depth where visible. Do not model the outer frame as four unrelated bars unless the design genuinely requires separate pieces.");
            sb.AppendLine("DOOR: create the host opening first; coordinated frame/jamb/head/threshold; leaf or leaves; glazing/panels/hardware zones when visible; correct swing/clearance only when supported by the request.");
            sb.AppendLine("CURTAIN WALL: establish the overall boundary and grid first; generate mullions/transoms from shared profiles; generate glazing/panels from the grid; keep frame and infill distinct but coordinated.");
            sb.AppendLine("WALL: create a continuous host wall body; cut openings from the host; add reveals, returns, finishes and joints as secondary geometry instead of building the wall from disconnected blocks around each opening.");
            sb.AppendLine("SLAB/FLOOR: create a continuous slab boundary and thickness; cut voids/openings; coordinate edges, drops, upstands and supports.");
            sb.AppendLine("ROOF/CANOPY: establish governing rails/sections/edge curves first; use Sweep/Loft/NetworkSrf/SubD where appropriate; then add thickness, fascia, supports, joints and drainage slope only when visible or requested.");
            sb.AppendLine("COLUMN/BEAM/FRAME: establish centerlines/axes and section profiles first; keep alignment and bearing/contact intentional; join only when the real assembly is monolithic.");
            sb.AppendLine("STAIR: model flight geometry, landings and primary support logic first; then treads/risers/stringers/railings; keep rise/run and landing relationships consistent.");
            sb.AppendLine("RAILING/LOUVRE/SCREEN: create a governing path/grid and one clean member profile, then generate repeated members consistently; avoid hand-placing many slightly different primitives.");
            sb.AppendLine("FACADE/PANEL SYSTEM: establish the base surface and panel grid first; derive panels, joints, mullions, fins or ribs from that shared system.");
            sb.AppendLine("ORGANIC/BESPOKE FORM: establish clean guide curves, rails and sections first; choose Sweep1/Sweep2/Loft/NetworkSrf/SubD/BlendSrf based on topology and continuity; add thickness/details only after the main surface is correct.");
            sb.AppendLine();
            sb.AppendLine("RHINO TECHNIQUE RULES:");
            sb.AppendLine("- Extrusion is appropriate for genuinely prismatic components and profile-based members, but it is not the default answer for complex forms.");
            sb.AppendLine("- Prefer Sweep1/Sweep2 for profile-driven curved members, Loft for controlled section transitions, NetworkSrf for two-direction surface control, Revolve for rotational forms, and SubD for genuinely organic editable forms.");
            sb.AppendLine("- Use BlendSrf/MatchSrf or equivalent continuity-aware methods where smooth transitions matter.");
            sb.AppendLine("- Build clean construction curves first when they provide a better geometric foundation than direct primitive creation.");
            sb.AppendLine();
        }

        private static void AppendArchitecturalQaRules(StringBuilder sb)
        {
            sb.AppendLine("ARCHITECTURAL QA:");
            sb.AppendLine("- Verify each recognised architectural object reads as one coherent assembly with sensible subcomponents.");
            sb.AppendLine("- Check that openings belong to their host element and are not merely gaps between unrelated solids.");
            sb.AppendLine("- Check frame members share consistent section size, alignment and connection logic.");
            sb.AppendLine("- Check glass/infill is properly seated within frames and not floating or intersecting them incorrectly.");
            sb.AppendLine("- Check walls, slabs, roofs, frames and supports meet intentionally at their interfaces.");
            sb.AppendLine("- Check repeated systems use consistent spacing, thickness and orientation.");
            sb.AppendLine("- Replace crude disconnected primitives when a cleaner profile/sweep/loft/joined Brep or system-based solution is clearly more appropriate.");
            sb.AppendLine("- Preserve separate components where constructionally distinct; coherence does not mean BooleanUnion everything.");
            sb.AppendLine();
        }

        private string BuildPrompt(string userMessage, List<string> attachments)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are Rhino AI Studio, connected directly to Rhino 8.");
            sb.AppendLine();
            sb.AppendLine("Rhino is already running and an active document is open.");
            sb.AppendLine("You have access to an MCP server named rhino.");
            sb.AppendLine();
            sb.AppendLine("For modelling, editing, inspection, layers, transforms, Grasshopper,");
            sb.AppendLine("view capture, geometry analysis, selections, or other Rhino operations:");
            sb.AppendLine();
            sb.AppendLine("USE THE RHINO MCP TOOLS DIRECTLY.");
            sb.AppendLine();
            sb.AppendLine("Do not launch another Rhino application.");
            sb.AppendLine("Work in the CURRENT ACTIVE RHINO DOCUMENT.");
            sb.AppendLine("Do not tell the user to manually model something when Rhino MCP can do it.");
            sb.AppendLine("Preserve existing geometry unless the user asks to change it.");
            sb.AppendLine("If a reference image is attached, inspect it and use it as visual guidance.");
            sb.AppendLine();

            if (_modelingMode == "pro")
            {
                sb.AppendLine("PRO MODELING MODE IS ON.");
                sb.AppendLine("Think like an experienced architect, facade modeller and Rhino specialist.");
                sb.AppendLine("Do not jump directly to primitive geometry. Understand what each object IS before deciding how to model it.");
                sb.AppendLine();
                AppendArchitecturalIntelligenceRules(sb);
                sb.AppendLine("WORKFLOW:");
                sb.AppendLine("1. IDENTIFY: classify the architectural objects/assemblies and their hierarchy.");
                sb.AppendLine("2. PLAN: establish origin, axes, levels, scale/proportions, key dimensions and construction relationships.");
                sb.AppendLine("3. PRIMARY GEOMETRY: create governing curves/grids/host geometry and major surfaces/solids first.");
                sb.AppendLine("4. ASSEMBLIES: build each architectural object using the appropriate recipe and coherent subcomponents.");
                sb.AppendLine("5. CONNECTIONS: trim/split/join/boolean only where appropriate so interfaces are intentional.");
                sb.AppendLine("6. DETAIL: add openings, frames, reveals, joints, edges, supports, parapets and visible secondary elements.");
                sb.AppendLine("7. QA: inspect geometry and architectural assembly logic.");
                sb.AppendLine("8. FIX: repair disconnected, crude, invalid or semantically incorrect geometry.");
                sb.AppendLine("9. VISUAL REVIEW: compare silhouette, proportions and important details with the reference/request.");
                sb.AppendLine();
                AppendArchitecturalQaRules(sb);
                sb.AppendLine("Do not stop at rough massing unless the user explicitly asks only for massing.");
                sb.AppendLine("Use fewer, cleaner, better-related objects rather than many crude primitives.");
            }
            else
            {
                sb.AppendLine("FAST MODELING MODE IS ON.");
                sb.AppendLine("Prioritize speed and the main architectural form, but still keep geometry valid and connected.");
            }

            sb.AppendLine();
            sb.AppendLine("After finishing, briefly explain what you created or changed.");

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE IMAGE FILES:");

                foreach (string image in attachments)
                    sb.AppendLine("- " + image);
            }

            sb.AppendLine();
            sb.AppendLine("USER REQUEST:");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        private void SetWorkingStage(string stage)
        {
            _workingStage = stage ?? "";

            try
            {
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    if (_busy)
                        Render();
                });
            }
            catch
            {
            }
        }

        private string BuildProAnalysisPrompt(string userMessage, List<string> attachments)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are the ARCHITECTURAL ANALYSIS PLANNER for Rhino AI Studio.");
            sb.AppendLine("This is PASS 1 of 4: ANALYSE AND SPECIFY ONLY.");
            sb.AppendLine();
            sb.AppendLine("Do NOT create, delete, move, transform, trim, join or modify Rhino geometry in this pass.");
            sb.AppendLine("Your job is to convert the request/reference into a precise architectural 3D specification and modelling plan.");
            sb.AppendLine();
            AppendArchitecturalIntelligenceRules(sb);
            sb.AppendLine("Analyse the user's request and every attached reference image carefully.");
            sb.AppendLine("If useful, inspect the CURRENT Rhino document using read-only Rhino MCP inspection tools, but do not modify it.");
            sb.AppendLine();
            sb.AppendLine("Return a structured specification containing:");
            sb.AppendLine("- OBJECT CLASSIFICATION: identify each major element as window, wall, door, slab, roof, canopy, curtain wall, frame, stair, railing, facade panel, bespoke form, etc.");
            sb.AppendLine("- ASSEMBLY HIERARCHY: for each object, list its host, subcomponents and what must remain separate versus continuous.");
            sb.AppendLine("- FORM/INTENT: overall silhouette, curvature, symmetry, repetition and architectural character.");
            sb.AppendLine("- SCALE: inferred coordinate system/origin, principal axes, levels and one consistent scale logic.");
            sb.AppendLine("- DIMENSIONS: key dimensions and ratios; mark all values that are inferred from images.");
            sb.AppendLine("- COMPONENT DETAILS: thickness hierarchy, frame/reveal/joint depth, openings, supports and visible secondary pieces.");
            sb.AppendLine("- MODELLING METHOD: choose the Rhino technique for each component and explain why it suits the topology.");
            sb.AppendLine("- CONNECTIONS: specify where components meet, seat, trim, join, overlap intentionally, or remain separate.");
            sb.AppendLine("- REPETITION: identify master profiles/grids/arrays instead of individually improvised repeated pieces.");
            sb.AppendLine("- RISKS: likely gaps, overlaps, slivers, floating pieces, invalid solids, weak joins or semantic assembly mistakes.");
            sb.AppendLine("- BUILD ORDER: ordered sequence from construction geometry to main assemblies to details.");
            sb.AppendLine();
            sb.AppendLine("Important: do not merely describe visible rectangles. Infer the likely architectural assembly behind them.");
            sb.AppendLine("Example: a four-sided window frame should normally be planned as one coordinated frame assembly around glazing, not as four unrelated rectangles.");
            sb.AppendLine("Be concrete and implementation-focused.");

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE IMAGE FILES:");
                foreach (string image in attachments)
                    sb.AppendLine("- " + image);
            }

            sb.AppendLine();
            sb.AppendLine("USER REQUEST:");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        private string BuildProBuildPrompt(
            string userMessage,
            List<string> attachments,
            string plan)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are the SENIOR ARCHITECTURAL RHINO MODELLER for Rhino AI Studio.");
            sb.AppendLine("This is PASS 2 of 4: BUILD.");
            sb.AppendLine();
            sb.AppendLine("USE RHINO MCP TOOLS DIRECTLY and work in the CURRENT ACTIVE RHINO DOCUMENT.");
            sb.AppendLine("Do not launch another Rhino instance.");
            sb.AppendLine();
            AppendArchitecturalIntelligenceRules(sb);
            sb.AppendLine("Execute the modelling specification below. Do not merely describe what should be modelled.");
            sb.AppendLine("Build in this order: governing axes/grids/curves -> host/main geometry -> coherent assemblies -> connections/openings -> visible secondary detail.");
            sb.AppendLine();
            sb.AppendLine("BUILD QUALITY RULES:");
            sb.AppendLine("- Treat the plan's object classification as authoritative unless Rhino inspection proves it impossible.");
            sb.AppendLine("- For each architectural object, create a coherent assembly rather than a pile of unrelated visual approximations.");
            sb.AppendLine("- Build one clean master profile or system and reuse it for repeated members.");
            sb.AppendLine("- Use consistent axes, levels, proportions and inferred dimensions from the plan.");
            sb.AppendLine("- Make connected architectural parts actually meet.");
            sb.AppendLine("- Use trim/split/join and Boolean operations only when they reflect the intended assembly.");
            sb.AppendLine("- Keep glass, cladding, structure and other materially distinct components separate when appropriate.");
            sb.AppendLine("- Avoid duplicates, accidental overlaps, tiny sliver faces, floating pieces and unnecessary coplanar fragments.");
            sb.AppendLine("- Preserve unrelated existing geometry.");
            sb.AppendLine("- Do not stop at rough massing unless the user explicitly requested only massing.");
            sb.AppendLine("- Add the visible architectural depth and secondary elements that materially define the reference.");
            sb.AppendLine();
            sb.AppendLine("MODELLING SPECIFICATION:");
            sb.AppendLine(plan);

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE IMAGE FILES:");
                foreach (string image in attachments)
                    sb.AppendLine("- " + image);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userMessage);
            sb.AppendLine();
            sb.AppendLine("When this pass is complete, give a short factual summary of what you actually created.");

            return sb.ToString();
        }

        private string BuildProInspectPrompt(
            string userMessage,
            List<string> attachments,
            string plan,
            string buildResult)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are the ARCHITECTURAL + GEOMETRY QA INSPECTOR for Rhino AI Studio.");
            sb.AppendLine("This is PASS 3 of 4: INSPECT ONLY.");
            sb.AppendLine();
            sb.AppendLine("Do NOT modify Rhino geometry in this pass.");
            sb.AppendLine("Inspect the CURRENT ACTIVE RHINO DOCUMENT using Rhino MCP.");
            sb.AppendLine("Use object inspection, geometry analysis, bounding boxes, intersections and viewport/view capture tools where useful.");
            sb.AppendLine();
            AppendArchitecturalQaRules(sb);
            sb.AppendLine("CHECK SPECIFICALLY FOR:");
            sb.AppendLine("- objects that visually resemble the reference but are architecturally assembled incorrectly");
            sb.AppendLine("- window/door/curtain-wall frames built as unrelated bars instead of coordinated assemblies");
            sb.AppendLine("- host elements built as disconnected blocks around openings instead of continuous hosts with openings");
            sb.AppendLine("- repeated components with inconsistent section, spacing or alignment");
            sb.AppendLine("- disconnected elements that should meet");
            sb.AppendLine("- gaps at roof/wall/slab/frame/support connections");
            sb.AppendLine("- unintended overlaps or collisions");
            sb.AppendLine("- duplicate objects");
            sb.AppendLine("- invalid Breps or failed solids");
            sb.AppendLine("- naked/non-manifold edges where closed solids are expected");
            sb.AppendLine("- floating or misplaced pieces");
            sb.AppendLine("- inconsistent levels, axes or proportions");
            sb.AppendLine("- missing major details visible in the reference");
            sb.AppendLine("- crude geometry that should be replaced by cleaner profile/sweep/loft/joined/system-based geometry");
            sb.AppendLine();
            sb.AppendLine("Compare the result against the original request and reference image(s).");
            sb.AppendLine("Return a concise, prioritised FIX LIST with exact objects/areas/issues to repair.");
            sb.AppendLine("If an item is already correct, do not invent a problem.");
            sb.AppendLine();
            sb.AppendLine("ORIGINAL SPECIFICATION:");
            sb.AppendLine(plan);
            sb.AppendLine();
            sb.AppendLine("BUILD PASS REPORT:");
            sb.AppendLine(buildResult);

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE IMAGE FILES:");
                foreach (string image in attachments)
                    sb.AppendLine("- " + image);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        private string BuildProRepairPrompt(
            string userMessage,
            List<string> attachments,
            string plan,
            string qaReport)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are the SENIOR ARCHITECTURAL RHINO REPAIR MODELLER for Rhino AI Studio.");
            sb.AppendLine("This is PASS 4 of 4: REPAIR AND REFINE.");
            sb.AppendLine();
            sb.AppendLine("USE RHINO MCP TOOLS DIRECTLY in the CURRENT ACTIVE RHINO DOCUMENT.");
            sb.AppendLine("Fix the QA issues below. Do not just explain them.");
            sb.AppendLine();
            AppendArchitecturalIntelligenceRules(sb);
            AppendArchitecturalQaRules(sb);
            sb.AppendLine("REPAIR PRIORITY:");
            sb.AppendLine("1. invalid or broken geometry");
            sb.AppendLine("2. architecturally incorrect assembly logic");
            sb.AppendLine("3. disconnected architectural connections");
            sb.AppendLine("4. wrong overlaps/collisions and bad booleans");
            sb.AppendLine("5. major proportion/alignment errors");
            sb.AppendLine("6. missing important architectural details");
            sb.AppendLine("7. cleanup of duplicates, slivers and unnecessary primitive fragments");
            sb.AppendLine();
            sb.AppendLine("Where a component was modelled as crude disconnected primitives, rebuild only that component using the correct architectural recipe and preserve correct surrounding geometry.");
            sb.AppendLine("After repairs, inspect the corrected areas again before finishing.");
            sb.AppendLine("Do not delete correct geometry just to rebuild it differently.");
            sb.AppendLine("Keep the result clean, editable and as faithful to the request/reference as possible.");
            sb.AppendLine();
            sb.AppendLine("QA FIX LIST:");
            sb.AppendLine(qaReport);
            sb.AppendLine();
            sb.AppendLine("ORIGINAL SPECIFICATION:");
            sb.AppendLine(plan);

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE IMAGE FILES:");
                foreach (string image in attachments)
                    sb.AppendLine("- " + image);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userMessage);
            sb.AppendLine();
            sb.AppendLine("Finish with a short summary of the final model and the repairs you made.");

            return sb.ToString();
        }

        private string RunCopilotPro(string userMessage, List<string> attachments)
        {
            string copilot = FindExecutable("copilot");

            if (string.IsNullOrWhiteSpace(copilot))
                throw new Exception("GitHub Copilot CLI not found.");

            SetWorkingStage("Pass 1/4 · Analysing...");
            string plan = ExecuteCopilot(
                copilot,
                BuildProAnalysisPrompt(userMessage, attachments),
                attachments
            );

            if (string.IsNullOrWhiteSpace(plan))
                throw new Exception("Pro Modeling analysis pass returned no plan.");

            SetWorkingStage("Pass 2/4 · Building...");
            string buildResult = ExecuteCopilot(
                copilot,
                BuildProBuildPrompt(userMessage, attachments, plan),
                attachments
            );

            SetWorkingStage("Pass 3/4 · Inspecting...");
            string qaReport = ExecuteCopilot(
                copilot,
                BuildProInspectPrompt(
                    userMessage,
                    attachments,
                    plan,
                    buildResult
                ),
                attachments
            );

            SetWorkingStage("Pass 4/4 · Repairing...");
            string finalResult = ExecuteCopilot(
                copilot,
                BuildProRepairPrompt(
                    userMessage,
                    attachments,
                    plan,
                    qaReport
                ),
                attachments
            );

            SetWorkingStage("Finishing...");

            if (string.IsNullOrWhiteSpace(finalResult))
                finalResult = "Pro Modeling completed: analysed, built, inspected and repaired the Rhino model.";

            return finalResult;
        }

        private string ExecuteCopilot(
            string copilot,
            string prompt,
            List<string> attachments)
        {
            var args = new List<string>
            {
                "--session-id", Guid.NewGuid().ToString(),
                "--enable-mcp-server=rhino",
                "--allow-tool=rhino",
                "--no-ask-user",
                "--no-auto-update",
                "--stream=off",
                "--no-color"
            };

            foreach (string image in attachments)
                args.Add("--attachment=" + image);

            args.Add("-s");
            args.Add("-p");
            args.Add(prompt);

            var result = RunProcess(copilot, args);

            string combined = (result.output ?? "") + "\n" + (result.error ?? "");

            if (combined.Contains("SessionInUseError", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("is in use by another Copilot process", StringComparison.OrdinalIgnoreCase))
            {
                args[1] = Guid.NewGuid().ToString();
                result = RunProcess(copilot, args);
            }

            if (result.exitCode != 0)
                throw new Exception(
                    string.IsNullOrWhiteSpace(result.error) ? result.output : result.error
                );

            return (result.output ?? "").Trim();
        }

        private string RunCopilot(string userMessage, List<string> attachments)
        {
            string copilot = FindExecutable("copilot");

            if (string.IsNullOrWhiteSpace(copilot))
                throw new Exception("GitHub Copilot CLI not found.");

            return ExecuteCopilot(
                copilot,
                BuildPrompt(userMessage, attachments),
                attachments
            );
        }

        private string RunCodex(string userMessage, List<string> attachments)
        {
            string codex = FindExecutable("codex");

            if (string.IsNullOrWhiteSpace(codex))
                throw new Exception(
                    "ChatGPT is not configured in the dock yet. Install Codex CLI and sign in with your ChatGPT account."
                );

            string router = FindRhinoMcpRouter();

            if (string.IsNullOrWhiteSpace(router))
                throw new Exception("Rhino MCP router not found.");

            EnsureCodexMcp(codex, router);

            string prompt = BuildPrompt(userMessage, attachments);

            string outputPath = Path.Combine(
                Path.GetTempPath(),
                "rhino_ai_studio_codex_" + Guid.NewGuid().ToString("N") + ".txt"
            );

            var args = new List<string>
            {
                "exec",
                "--skip-git-repo-check"
            };

            foreach (string image in attachments)
            {
                args.Add("--image");
                args.Add(image);
            }

            args.Add("--output-last-message");
            args.Add(outputPath);
            args.Add(prompt);

            var result = RunProcess(codex, args);

            if (result.exitCode != 0)
                throw new Exception(
                    string.IsNullOrWhiteSpace(result.error) ? result.output : result.error
                );

            try
            {
                if (File.Exists(outputPath))
                {
                    string final = File.ReadAllText(outputPath);

                    if (!string.IsNullOrWhiteSpace(final))
                        return final.Trim();
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                }
                catch { }
            }

            return (result.output ?? "").Trim();
        }

        private void EnsureCodexMcp(string codex, string router)
        {
            var get = RunProcess(codex, new List<string>
            {
                "mcp",
                "get",
                "rhino",
                "--json"
            });

            if (get.exitCode == 0)
                return;

            var add = RunProcess(codex, new List<string>
            {
                "mcp",
                "add",
                "rhino",
                "--",
                router
            });

            if (add.exitCode != 0)
                throw new Exception("Could not configure Rhino MCP for ChatGPT.");
        }

        private string RunClaude(string userMessage, List<string> attachments)
        {
            string claude = FindExecutable("claude");

            if (string.IsNullOrWhiteSpace(claude))
                throw new Exception(
                    "Claude is not configured in the dock yet. Install Claude Code and sign in first."
                );

            string router = FindRhinoMcpRouter();

            if (string.IsNullOrWhiteSpace(router))
                throw new Exception("Rhino MCP router not found.");

            string configPath = Path.Combine(
                Path.GetTempPath(),
                "rhino_ai_studio_claude_mcp_" + Guid.NewGuid().ToString("N") + ".json"
            );

            string escapedRouter = router
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");

            string config =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"rhino\": {\n" +
                "      \"command\": \"" + escapedRouter + "\",\n" +
                "      \"args\": []\n" +
                "    }\n" +
                "  }\n" +
                "}";

            File.WriteAllText(configPath, config);

            string prompt = BuildPrompt(userMessage, attachments);

            var args = new List<string>
            {
                "-p",
                prompt,
                "--mcp-config",
                configPath,
                "--allowedTools",
                "mcp__rhino",
                "Read",
                "--output-format",
                "text"
            };

            foreach (string folder in attachments
                .Select(Path.GetDirectoryName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                args.Add("--add-dir");
                args.Add(folder);
            }

            try
            {
                var result = RunProcess(claude, args);

                if (result.exitCode != 0)
                    throw new Exception(
                        string.IsNullOrWhiteSpace(result.error) ? result.output : result.error
                    );

                return (result.output ?? "").Trim();
            }
            finally
            {
                try
                {
                    if (File.Exists(configPath))
                        File.Delete(configPath);
                }
                catch { }
            }
        }

        private static string FindRhinoMcpRouter()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "McNeel",
                "Rhinoceros",
                "ai",
                "bin",
                "rhino-mcp-router.exe"
            );

            return File.Exists(path) ? path : null;
        }

        private static string FindExecutable(string baseName)
        {
            string[] names =
            {
                baseName + ".exe",
                baseName + ".cmd",
                baseName + ".bat"
            };

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string folder in path.Split(Path.PathSeparator))
            {
                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(folder.Trim(), name);

                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch { }
                }
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] candidates =
            {
                Path.Combine(appData, "npm", baseName + ".cmd"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", baseName + ".exe"),
                Path.Combine(home, ".local", "bin", baseName + ".exe"),
                Path.Combine(
                    local,
                    "Programs",
                    "Microsoft VS Code",
                    "bin",
                    "copilot-shim",
                    "copilot.exe"
                )
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static (int exitCode, string output, string error) RunProcess(
            string executable,
            List<string> arguments)
        {
            bool isCmd =
                executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isCmd
                    ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
                    : executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (isCmd)
            {
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(executable);
            }

            foreach (string arg in arguments)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);

            if (process == null)
                throw new Exception("Could not start AI process.");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            return (process.ExitCode, output, error);
        }

        private static string ProviderDisplayName(string provider)
        {
            if (provider == "copilot")
                return "GitHub Copilot";

            if (provider == "codex")
                return "ChatGPT";

            if (provider == "claude")
                return "Claude";

            return "AI";
        }

        private bool IsProviderReady(string provider)
        {
            if (provider == "copilot")
                return !string.IsNullOrWhiteSpace(FindExecutable("copilot"));

            if (provider == "codex")
                return !string.IsNullOrWhiteSpace(FindExecutable("codex"));

            if (provider == "claude")
                return !string.IsNullOrWhiteSpace(FindExecutable("claude"));

            return false;
        }

        private string BuildMessagesHtml()
        {
            return string.Join("\n", _messages.Select(m =>
            {
                string cls = m.role == "user" ? "user" : "assistant";

                return
                    "<div class='row " + cls + "'>" +
                    "<div class='bubble'>" +
                    "<div class='name'>" + Escape(m.name) + "</div>" +
                    Escape(m.text).Replace("\n", "<br>") +
                    "</div></div>";
            }));
        }

        private string BuildAttachmentHtml()
        {
            if (_attachments.Count == 0 && !_processingAttachments)
                return "";

            var cards = new StringBuilder();

            for (int i = 0; i < _attachments.Count; i++)
            {
                var attachment = _attachments[i];
                string imageSrc = BuildDataUri(attachment.PreviewPath);

                cards.Append("<div class='attachcard'>");

                if (!string.IsNullOrWhiteSpace(imageSrc))
                {
                    cards.Append(
                        "<img class='thumb' src='" + imageSrc + "' alt='reference image'>"
                    );
                }
                else
                {
                    cards.Append("<div class='thumb fallback'>IMG</div>");
                }

                cards.Append("<div class='attachname'>" + Escape(attachment.DisplayName) + "</div>");
                cards.Append(
                    "<button class='remove' onclick='removeAttachment(" + i + ")'>×</button>"
                );
                cards.Append("</div>");
            }

            if (_processingAttachments)
            {
                cards.Append(
                    "<div class='attachcard'><div class='thumb fallback'>...</div>" +
                    "<div class='attachname'>Processing image...</div></div>"
                );
            }

            return
                "<div class='attachments'>" +
                "<div class='attachhead'><span>Reference images</span>" +
                "<button class='clearattach' onclick='clearAttachments()'>Clear all</button></div>" +
                "<div class='attachlist'>" + cards + "</div>" +
                "</div>";
        }

        private static string BuildDataUri(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return "";

                var info = new FileInfo(path);

                // Preview files are tiny. Never push a large image into the WebView.
                if (info.Length > 512L * 1024L)
                    return "";

                byte[] bytes = File.ReadAllBytes(path);

                return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            catch
            {
                return "";
            }
        }

        private void Render()
        {
            string documentName = RhinoDoc.ActiveDoc?.Name;

            if (string.IsNullOrWhiteSpace(documentName))
                documentName = "Untitled";

            bool providerReady = IsProviderReady(_provider);

            string providerStateClass = providerReady ? "ready" : "setup";
            string providerStateText = providerReady
                ? ProviderDisplayName(_provider) + " ready"
                : ProviderDisplayName(_provider) + " setup needed";

            string html = """
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<style>
*{box-sizing:border-box}
html,body{height:100%;margin:0;font-family:Segoe UI,Arial,sans-serif;background:#fbfbfc;color:#202124}
button,select,textarea{font-family:inherit}
.app{height:100%;display:flex;flex-direction:column}
.top{padding:13px 12px 11px;border-bottom:1px solid #e8e8eb;background:#fff}
.brandrow{display:flex;align-items:center;gap:8px}
.logo{width:24px;height:24px;border-radius:7px;background:linear-gradient(135deg,#6c50e8,#9679ff);display:flex;align-items:center;justify-content:center;color:#fff;font-size:11px;font-weight:700}
.brand{font-size:14px;font-weight:700}
.model{font-size:9px;color:#777;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.providerrow{display:flex;gap:7px;margin-top:11px}
.moderow{display:flex;gap:7px;margin-top:7px;align-items:center}
.modelabel{font-size:9px;color:#777;min-width:48px}
select{flex:1;height:34px;border:1px solid #dddde3;border-radius:8px;background:#fff;padding:0 9px;font-size:10px;outline:none}
.small{border:1px solid #e4e4e8;background:#f6f6f8;border-radius:8px;padding:7px 9px;font-size:9px;cursor:pointer}
.status{margin-top:8px;display:flex;gap:6px;align-items:center;flex-wrap:wrap}
.pill{font-size:9px;padding:5px 8px;border-radius:14px}
.on,.ready{background:#e8f7ec;color:#19763a}
.off{background:#f1f1f3;color:#666}
.setup{background:#fff2dc;color:#8b5b00}
.chat{flex:1;overflow:auto;padding:13px 11px}
.row{display:flex;margin-bottom:12px}
.row.user{justify-content:flex-end}
.bubble{max-width:90%;padding:9px 10px;border-radius:11px;background:#f0f1f3;font-size:11px;line-height:1.5;word-break:break-word}
.user .bubble{background:#eeeaff;border:1px solid #e2dbff}
.name{font-size:9px;color:#777;font-weight:650;margin-bottom:4px}
.attachments{border-top:1px solid #ececf0;background:#fff;padding:8px 10px 7px}
.attachhead{display:flex;align-items:center;justify-content:space-between;font-size:9px;color:#666;margin-bottom:6px}
.clearattach{border:0;background:transparent;color:#6c50e8;font-size:9px;cursor:pointer}
.attachlist{display:flex;gap:7px;overflow-x:auto;padding-bottom:2px}
.attachcard{position:relative;flex:0 0 82px;border:1px solid #e3e3e8;border-radius:8px;background:#fafafa;padding:4px}
.thumb{width:72px;height:52px;object-fit:cover;border-radius:5px;display:block;background:#eee}
.fallback{display:flex;align-items:center;justify-content:center;font-size:9px;color:#777}
.attachname{font-size:8px;margin-top:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.remove{position:absolute;top:1px;right:1px;width:18px;height:18px;border:0;border-radius:9px;background:rgba(30,30,30,.72);color:#fff;font-size:13px;line-height:16px;cursor:pointer}
.composer{padding:10px;border-top:1px solid #e7e7ea;background:#fff}
.box{border:1px solid #7257e8;border-radius:11px;padding:7px;background:#fff;box-shadow:0 0 0 2px rgba(114,87,232,.06)}
textarea{width:100%;height:72px;resize:none;border:0;outline:none;font-size:11px;line-height:1.45;background:transparent;color:#222}
.bottom{display:flex;align-items:center;gap:6px;margin-top:5px}
.hint{font-size:8px;color:#999}
.attach{border:0;background:#f1effc;color:#5d43cf;border-radius:7px;padding:7px 9px;font-size:10px;font-weight:600;cursor:pointer}
.send{margin-left:auto;border:0;background:#6549df;color:#fff;border-radius:8px;padding:7px 14px;font-size:10px;font-weight:650;cursor:pointer}
.send:disabled{opacity:.5;cursor:default}
</style>
</head>
<body>
<div class='app'>
  <div class='top'>
    <div class='brandrow'>
      <div class='logo'>AI</div>
      <div style='min-width:0'>
        <div class='brand'>Rhino AI Studio</div>
        <div class='model'>Active model: __DOCUMENT__</div>
      </div>
    </div>

    <div class='providerrow'>
      <select onchange='setProvider(this.value)'>
        <option value='copilot' __COPILOT_SELECTED__>GitHub Copilot</option>
        <option value='codex' __CODEX_SELECTED__>ChatGPT</option>
        <option value='claude' __CLAUDE_SELECTED__>Claude</option>
      </select>

      <button class='small' onclick='newChat()'>New chat</button>
    </div>

    <div class='moderow'>
      <span class='modelabel'>Modeling</span>
      <select onchange='setMode(this.value)'>
        <option value='fast' __FAST_SELECTED__>Fast</option>
        <option value='pro' __PRO_SELECTED__>Pro Modeling</option>
      </select>
    </div>

    <div class='status'>
      <span class='pill __MCP_CLASS__'>__MCP_TEXT__</span>
      <span class='pill __PROVIDER_CLASS__'>__PROVIDER_TEXT__</span>
      <button class='small' onclick='startMcp()'>Connect MCP</button>
    </div>
  </div>

  <div id='chat' class='chat'>__MESSAGES__</div>

  __ATTACHMENTS__

  <div class='composer'>
    <div class='box'>
      <textarea id='p' placeholder='Describe what you want to create, modify or analyse...'></textarea>
      <div class='bottom'>
        <button class='attach' onclick='attachImage()'>＋ Image</button>
        <span class='hint'>Enter to send · Shift+Enter for new line</span>
        <button class='send' __SEND_DISABLED__ onclick='sendMessage()'>__SEND_TEXT__</button>
      </div>
    </div>
  </div>
</div>

<script>
function nav(url){window.location.href=url;}
function setProvider(v){nav('rhinoai://provider?value='+encodeURIComponent(v));}
function setMode(v){nav('rhinoai://mode?value='+encodeURIComponent(v));}
function attachImage(){nav('rhinoai://attach');}
function removeAttachment(i){nav('rhinoai://removeattachment?index='+i);}
function clearAttachments(){nav('rhinoai://clearattachments');}
function newChat(){nav('rhinoai://clear');}
function startMcp(){nav('rhinoai://mcp');}
function sendMessage(){
  var p=document.getElementById('p');
  var t=(p.value||'').trim();
  if(!t)return;
  nav('rhinoai://send?message='+encodeURIComponent(t));
}
document.addEventListener('keydown',function(e){
  if(e.target && e.target.id==='p' && e.key==='Enter' && !e.shiftKey){
    e.preventDefault();
    sendMessage();
  }
});
window.addEventListener('load',function(){
  var c=document.getElementById('chat');
  if(c)c.scrollTop=c.scrollHeight;
  var p=document.getElementById('p');
  if(p)p.focus();
});
</script>
</body>
</html>
""";

            html = html
                .Replace("__DOCUMENT__", Escape(documentName))
                .Replace("__COPILOT_SELECTED__", _provider == "copilot" ? "selected" : "")
                .Replace("__CODEX_SELECTED__", _provider == "codex" ? "selected" : "")
                .Replace("__CLAUDE_SELECTED__", _provider == "claude" ? "selected" : "")
                .Replace("__FAST_SELECTED__", _modelingMode == "fast" ? "selected" : "")
                .Replace("__PRO_SELECTED__", _modelingMode == "pro" ? "selected" : "")
                .Replace("__MCP_CLASS__", _mcpStarted ? "on" : "off")
                .Replace("__MCP_TEXT__", _mcpStarted ? "Rhino MCP connected" : "Rhino MCP available")
                .Replace("__PROVIDER_CLASS__", providerStateClass)
                .Replace("__PROVIDER_TEXT__", Escape(providerStateText))
                .Replace("__MESSAGES__", BuildMessagesHtml())
                .Replace("__ATTACHMENTS__", BuildAttachmentHtml())
                .Replace("__SEND_DISABLED__", (_busy || _processingAttachments) ? "disabled" : "")
                .Replace(
                    "__SEND_TEXT__",
                    _processingAttachments
                        ? "Processing image..."
                        : (_busy
                            ? (string.IsNullOrWhiteSpace(_workingStage) ? "Working..." : _workingStage)
                            : "Send")
                );

            _web.LoadHtml(html);
        }

        private static string Escape(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
    }
}
