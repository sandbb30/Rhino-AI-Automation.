using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
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

        private readonly WebView _web = new WebView();
        private readonly List<(string role, string name, string text)> _messages = new();
        private readonly List<string> _attachments = new();

        private bool _busy;
        private bool _mcpStarted;
        private string _provider = "copilot";

        public RhinoAIStudioPanel()
        {
            _messages.Add(("assistant", "Rhino AI Studio",
                "Ready. Ask me to create, modify or analyse something in Rhino."));

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
                else if (action == "attach")
                {
                    AttachImages();
                    Render();
                }
                else if (action == "clearattachments")
                {
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
                bool ok = RhinoApp.RunScript("MCPStart", false);
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
                var dialog = new OpenFileDialog
                {
                    MultiSelect = true,
                    Title = "Attach image"
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

                foreach (string file in dialog.Filenames)
                {
                    if (File.Exists(file) && !_attachments.Contains(file))
                        _attachments.Add(file);
                }
            }
            catch (Exception ex)
            {
                _messages.Add(("assistant", "Rhino AI Studio", "Attachment error: " + ex.Message));
            }
        }

        private async void SendMessage(string message)
        {
            _busy = true;

            var attachmentsForTurn = _attachments.ToList();
            _attachments.Clear();

            string contextText = attachmentsForTurn.Count > 0
                ? $"\n\nAttached image(s): {attachmentsForTurn.Count}"
                : "";

            _messages.Add(("user", "You", message + contextText));
            Render();

            if (!_mcpStarted)
                StartMcp(false);

            string providerForTurn = _provider;

            try
            {
                string response = await Task.Run(() =>
                    RunProvider(providerForTurn, message, attachmentsForTurn));

                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add((
                        "assistant",
                        ProviderDisplayName(providerForTurn),
                        string.IsNullOrWhiteSpace(response) ? "Task completed." : response
                    ));

                    _busy = false;
                    Render();
                });
            }
            catch (Exception ex)
            {
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add(("assistant", "Rhino AI Studio", "AI error: " + ex.Message));
                    _busy = false;
                    Render();
                });
            }
        }

        private string RunProvider(string provider, string userMessage, List<string> attachments)
        {
            if (provider == "copilot")
                return RunCopilot(userMessage, attachments);

            if (provider == "codex")
                return RunCodex(userMessage, attachments);

            if (provider == "claude")
                return RunClaude(userMessage, attachments);

            throw new Exception("Unknown provider.");
        }

        private string BuildPrompt(string userMessage, List<string> attachments)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are Rhino AI Studio, connected directly to Rhino 8.");
            sb.AppendLine();
            sb.AppendLine("Rhino is already running and an active document is open.");
            sb.AppendLine("You have access to an MCP server named rhino.");
            sb.AppendLine();
            sb.AppendLine("When the user asks you to create, modify, delete, move, transform,");
            sb.AppendLine("inspect, select, organize, model, create layers, use Grasshopper,");
            sb.AppendLine("capture views, analyse geometry, or otherwise work with Rhino:");
            sb.AppendLine();
            sb.AppendLine("USE THE RHINO MCP TOOLS DIRECTLY.");
            sb.AppendLine();
            sb.AppendLine("Do not launch another Rhino application.");
            sb.AppendLine("Work in the CURRENT ACTIVE RHINO DOCUMENT.");
            sb.AppendLine("Do not tell the user to manually perform modelling operations");
            sb.AppendLine("when Rhino MCP can perform them.");
            sb.AppendLine("After finishing, briefly explain what was created or modified.");

            if (attachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("IMAGE REFERENCES:");

                foreach (string image in attachments)
                    sb.AppendLine("- " + image);

                sb.AppendLine("Inspect these image references and use them for the request.");
            }

            sb.AppendLine();
            sb.AppendLine("USER REQUEST:");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        private string RunCopilot(string userMessage, List<string> attachments)
        {
            string copilot = FindExecutable("copilot");

            if (string.IsNullOrWhiteSpace(copilot))
                throw new Exception("GitHub Copilot CLI not found.");

            string prompt = BuildPrompt(userMessage, attachments);

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
                throw new Exception(string.IsNullOrWhiteSpace(result.error) ? result.output : result.error);

            return (result.output ?? "").Trim();
        }

        private string RunCodex(string userMessage, List<string> attachments)
        {
            string codex = FindExecutable("codex");

            if (string.IsNullOrWhiteSpace(codex))
                throw new Exception(
                    "ChatGPT/Codex CLI not found. Install Codex CLI and sign in with your ChatGPT account first.");

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
                throw new Exception(string.IsNullOrWhiteSpace(result.error) ? result.output : result.error);

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
                throw new Exception("Could not configure Rhino MCP in Codex.");
        }

        private string RunClaude(string userMessage, List<string> attachments)
        {
            string claude = FindExecutable("claude");

            if (string.IsNullOrWhiteSpace(claude))
                throw new Exception(
                    "Claude Code CLI not found. Install Claude Code CLI and sign in first.");

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
                    throw new Exception(string.IsNullOrWhiteSpace(result.error) ? result.output : result.error);

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
                Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "copilot-shim", "copilot.exe")
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
                return "ChatGPT / Codex";

            if (provider == "claude")
                return "Claude";

            return "AI";
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
            if (_attachments.Count == 0)
                return "";

            string chips = string.Join("", _attachments.Select(path =>
                "<span class='chip'>📎 " + Escape(Path.GetFileName(path)) + "</span>"));

            return
                "<div class='attachmentbar'>" +
                chips +
                "<button class='clearattach' onclick='clearAttachments()'>Clear</button>" +
                "</div>";
        }

        private void Render()
        {
            string html = """
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<style>
*{box-sizing:border-box}
html,body{height:100%;margin:0;font-family:Segoe UI,Arial;background:#fff;color:#202124}
.app{height:100%;display:flex;flex-direction:column}
.top{padding:12px;border-bottom:1px solid #e5e5e5}
.brand{font-size:15px;font-weight:650}
.sub{font-size:10px;color:#777;margin-top:2px}
.providerrow{display:flex;gap:6px;margin-top:10px}
select{flex:1;height:32px;border:1px solid #ddd;border-radius:7px;background:#fff;padding:0 7px;font-size:10px}
.status{margin-top:8px;display:flex;gap:6px;align-items:center}
.pill{font-size:9px;padding:5px 8px;border-radius:12px}
.on{background:#e5f7e9;color:#16823a}
.off{background:#eee;color:#666}
.small{border:0;background:#f3f3f4;border-radius:7px;padding:6px 8px;font-size:9px;cursor:pointer}
.chat{flex:1;overflow:auto;padding:12px}
.row{display:flex;margin-bottom:12px}
.row.user{justify-content:flex-end}
.bubble{max-width:88%;padding:9px 10px;border-radius:10px;background:#f2f3f5;font-size:11px;line-height:1.45}
.user .bubble{background:#eeeaff}
.name{font-size:9px;color:#777;font-weight:600;margin-bottom:4px}
.attachmentbar{padding:5px 10px;display:flex;gap:5px;align-items:center;overflow-x:auto;border-top:1px solid #eee}
.chip{flex:0 0 auto;background:#f1f2f4;border-radius:6px;padding:5px 7px;font-size:9px;max-width:170px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.clearattach{border:0;background:transparent;color:#777;font-size:9px;cursor:pointer}
.composer{padding:10px;border-top:1px solid #e7e7e7}
.box{border:1px solid #7055e8;border-radius:10px;padding:7px}
textarea{width:100%;height:72px;resize:none;border:0;outline:none;font:11px Segoe UI;background:transparent}
.bottom{display:flex;align-items:center;gap:6px;margin-top:5px}
.attach{border:0;background:#f2f3f5;border-radius:7px;padding:7px 9px;font-size:10px;cursor:pointer}
.send{margin-left:auto;border:0;background:#6246df;color:white;border-radius:7px;padding:7px 14px;font-size:10px;font-weight:600;cursor:pointer}
.send:disabled{opacity:.55}
</style>
</head>
<body>
<div class='app'>
  <div class='top'>
    <div class='brand'>Rhino AI Studio</div>
    <div class='sub'>AI assistant for the active Rhino model</div>

    <div class='providerrow'>
      <select onchange='setProvider(this.value)'>
        <option value='copilot' __COPILOT_SELECTED__>GitHub Copilot</option>
        <option value='codex' __CODEX_SELECTED__>ChatGPT / Codex</option>
        <option value='claude' __CLAUDE_SELECTED__>Claude</option>
      </select>

      <button class='small' onclick='newChat()'>New chat</button>
    </div>

    <div class='status'>
      <span class='pill __STATUS_CLASS__'>__STATUS_TEXT__</span>
      <button class='small' onclick='startMcp()'>Start MCP</button>
    </div>
  </div>

  <div id='chat' class='chat'>__MESSAGES__</div>

  __ATTACHMENTS__

  <div class='composer'>
    <div class='box'>
      <textarea id='p' placeholder='Ask AI to create or modify something in Rhino...'></textarea>
      <div class='bottom'>
        <button class='attach' onclick='attachImage()'>📎 Image</button>
        <button class='send' __SEND_DISABLED__ onclick='sendMessage()'>__SEND_TEXT__</button>
      </div>
    </div>
  </div>
</div>

<script>
function nav(url){window.location.href=url;}
function setProvider(v){nav('rhinoai://provider?value='+encodeURIComponent(v));}
function attachImage(){nav('rhinoai://attach');}
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
  if(e.ctrlKey&&e.key==='Enter'){
    e.preventDefault();
    sendMessage();
  }
});
window.addEventListener('load',function(){
  var c=document.getElementById('chat');
  if(c)c.scrollTop=c.scrollHeight;
});
</script>
</body>
</html>
""";

            html = html
                .Replace("__COPILOT_SELECTED__", _provider == "copilot" ? "selected" : "")
                .Replace("__CODEX_SELECTED__", _provider == "codex" ? "selected" : "")
                .Replace("__CLAUDE_SELECTED__", _provider == "claude" ? "selected" : "")
                .Replace("__STATUS_CLASS__", _mcpStarted ? "on" : "off")
                .Replace("__STATUS_TEXT__", _mcpStarted ? "MCP connected" : "MCP available")
                .Replace("__MESSAGES__", BuildMessagesHtml())
                .Replace("__ATTACHMENTS__", BuildAttachmentHtml())
                .Replace("__SEND_DISABLED__", _busy ? "disabled" : "")
                .Replace("__SEND_TEXT__", _busy ? "Working..." : "Send");

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
