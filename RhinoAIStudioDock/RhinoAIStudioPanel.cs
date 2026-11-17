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
        private readonly List<(string role, string text)> _messages = new();
        private bool _busy;
        private bool _mcpStarted;

        public RhinoAIStudioPanel()
        {
            _messages.Add(("assistant", "Ready. Ask me to create or modify something in Rhino."));
            _web.DocumentLoading += OnDocumentLoading;
            Content = _web;
            Render();
            Eto.Forms.Application.Instance.AsyncInvoke(() => StartMcp(false));
        }

        private void OnDocumentLoading(object sender, WebViewLoadingEventArgs e)
        {
            if (e.Uri == null || !string.Equals(e.Uri.Scheme, "rhinoai", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;

            string action = e.Uri.Host?.ToLowerInvariant() ?? "";
            var query = ParseQuery(e.Uri.Query);

            if (action == "send")
            {
                string message = query.TryGetValue("message", out var m) ? m.Trim() : "";
                if (!string.IsNullOrWhiteSpace(message) && !_busy)
                    SendMessage(message);
            }
            else if (action == "mcp")
            {
                StartMcp(true);
                Render();
            }
            else if (action == "clear")
            {
                _messages.Clear();
                _messages.Add(("assistant", "New conversation started."));
                Render();
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
                return result;

            string q = query.TrimStart('?');
            foreach (string part in q.Split('&'))
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
                    _messages.Add(("assistant", ok ? "Rhino MCP connected." : "Could not start Rhino MCP."));
            }
            catch (Exception ex)
            {
                _mcpStarted = false;
                if (addMessage)
                    _messages.Add(("assistant", "MCP error: " + ex.Message));
            }
        }

        private async void SendMessage(string message)
        {
            _busy = true;
            _messages.Add(("user", message));
            Render();

            if (!_mcpStarted)
                StartMcp(false);

            try
            {
                string response = await Task.Run(() => RunCopilot(message));

                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add(("assistant", string.IsNullOrWhiteSpace(response) ? "Task completed." : response));
                    _busy = false;
                    Render();
                });
            }
            catch (Exception ex)
            {
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    _messages.Add(("assistant", "AI error: " + ex.Message));
                    _busy = false;
                    Render();
                });
            }
        }

        private string RunCopilot(string userMessage)
        {
            string copilot = FindCopilot();
            if (string.IsNullOrWhiteSpace(copilot))
                throw new Exception("GitHub Copilot CLI not found.");

            string prompt =
@"You are Rhino AI Studio, connected directly to Rhino 8.

Rhino is already running and an active document is open.
You have access to an MCP server named rhino.

When the user asks you to create, modify, delete, move, transform,
inspect, select, organize, model, create layers, use Grasshopper,
capture views, analyse geometry, or otherwise work with Rhino:

USE THE RHINO MCP TOOLS DIRECTLY.

Do not launch another Rhino application.
Work in the CURRENT ACTIVE RHINO DOCUMENT.
Do not tell the user to manually perform modelling operations when
Rhino MCP can perform them.
After finishing, briefly explain what was created or modified.

USER REQUEST:
" + userMessage;

            string sessionId = Guid.NewGuid().ToString();

            var args = new List<string>
            {
                "--session-id", sessionId,
                "--enable-mcp-server=rhino",
                "--allow-tool=rhino",
                "--no-ask-user",
                "--no-auto-update",
                "--stream=off",
                "--no-color",
                "-s",
                "-p",
                prompt
            };

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

        private static string FindCopilot()
        {
            string[] candidates =
            {
                FindOnPath("copilot.exe"),
                FindOnPath("copilot.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links", "copilot.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "copilot.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Microsoft VS Code", "bin", "copilot-shim", "copilot.exe")
            };

            return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        }

        private static string FindOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim(), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
            return null;
        }

        private static (int exitCode, string output, string error) RunProcess(string executable, List<string> arguments)
        {
            bool isCmd = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                         executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isCmd ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") : executable,
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
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output, error);
        }

        private void Render()
        {
            string messages = string.Join("\n", _messages.Select(m =>
            {
                string cls = m.role == "user" ? "user" : "assistant";
                string name = m.role == "user" ? "You" : "Rhino AI Studio";
                return $"<div class='row {cls}'><div class='bubble'><div class='name'>{Escape(name)}</div>{Escape(m.text).Replace("\n", "<br>")}</div></div>";
            }));

            string statusClass = _mcpStarted ? "on" : "off";
            string statusText = _mcpStarted ? "MCP connected" : "MCP available";
            string sendDisabled = _busy ? "disabled" : "";
            string sendText = _busy ? "Working..." : "Send";

            _web.LoadHtml($@"
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<style>
*{{box-sizing:border-box}}html,body{{height:100%;margin:0;font-family:Segoe UI,Arial;background:#fff;color:#202124}}
.app{{height:100%;display:flex;flex-direction:column}}
.top{{padding:12px;border-bottom:1px solid #e5e5e5}}
.brand{{font-size:15px;font-weight:650}}
.sub{{font-size:10px;color:#777;margin-top:2px}}
.status{{margin-top:9px;display:flex;gap:6px;align-items:center}}
.pill{{font-size:9px;padding:5px 8px;border-radius:12px}}.on{{background:#e5f7e9;color:#16823a}}.off{{background:#eee;color:#666}}
.small{{border:0;background:#f3f3f4;border-radius:7px;padding:6px 8px;font-size:9px;cursor:pointer}}
.chat{{flex:1;overflow:auto;padding:12px}}
.row{{display:flex;margin-bottom:12px}}.row.user{{justify-content:flex-end}}
.bubble{{max-width:88%;padding:9px 10px;border-radius:10px;background:#f2f3f5;font-size:11px;line-height:1.45}}
.user .bubble{{background:#eeeaff}}.name{{font-size:9px;color:#777;font-weight:600;margin-bottom:4px}}
.composer{{padding:10px;border-top:1px solid #e7e7e7}}
.box{{border:1px solid #7055e8;border-radius:10px;padding:7px}}
textarea{{width:100%;height:72px;resize:none;border:0;outline:none;font:11px Segoe UI;background:transparent}}
.bottom{{display:flex;align-items:center;margin-top:5px}}
.send{{margin-left:auto;border:0;background:#6246df;color:white;border-radius:7px;padding:7px 14px;font-size:10px;font-weight:600;cursor:pointer}}
.send:disabled{{opacity:.55}}
</style>
</head>
<body>
<div class='app'>
  <div class='top'>
    <div class='brand'>Rhino AI Studio</div>
    <div class='sub'>AI assistant for the active Rhino model</div>
    <div class='status'>
      <span class='pill {statusClass}'>{statusText}</span>
      <button class='small' onclick=""go('mcp')"">Start MCP</button>
      <button class='small' onclick=""go('clear')"">New chat</button>
    </div>
  </div>

  <div id='chat' class='chat'>{messages}</div>

  <div class='composer'>
    <div class='box'>
      <textarea id='p' placeholder='Ask AI to create or modify something in Rhino...'></textarea>
      <div class='bottom'>
        <button class='send' {sendDisabled} onclick='send()'>{sendText}</button>
      </div>
    </div>
  </div>
</div>

<script>
function go(a){{window.location.href='rhinoai://'+a;}}
function send(){{
  var p=document.getElementById('p');
  var t=(p.value||'').trim();
  if(!t)return;
  window.location.href='rhinoai://send?message='+encodeURIComponent(t);
}}
document.addEventListener('keydown',function(e){{if(e.ctrlKey&&e.key==='Enter'){{e.preventDefault();send();}}}});
window.addEventListener('load',function(){{var c=document.getElementById('chat');if(c)c.scrollTop=c.scrollHeight;}});
</script>
</body>
</html>");
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
