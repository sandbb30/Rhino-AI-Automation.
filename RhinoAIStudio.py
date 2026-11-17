#! python 3

# ==============================================================
# RHINO AI STUDIO
# Version 5.1
# Safe image attachment pipeline
# ==============================================================

import os
import json
import uuid
import shutil
import subprocess
import threading
import traceback
import tempfile
import html
import urllib.parse
import base64

import Rhino
import rhinoscriptsyntax as rs
import Eto.Forms as forms
import Eto.Drawing as drawing


APP_NAME = "Rhino AI Studio"
CREATE_NO_WINDOW = 0x08000000
MAX_AI_IMAGE_DIM = 1400
PREVIEW_IMAGE_DIM = 256

SYSTEM_PROMPT = """
You are Rhino AI Studio, an architectural and 3D modelling assistant
connected directly to Rhino 8.

Rhino is already running and an active Rhino document is open.
You have access to an MCP server named "rhino".

When the user asks you to create, modify, delete, move, transform,
inspect, select, organize, model, create layers, use Grasshopper,
capture views, analyse geometry, or otherwise work with Rhino:

USE THE RHINO MCP TOOLS DIRECTLY.
Do not launch another Rhino application.
Work in the CURRENT ACTIVE RHINO DOCUMENT.
Do not tell the user to manually perform modelling operations when
Rhino MCP can perform them.
Do not merely generate Rhino Python code unless explicitly requested.
If images are attached, inspect them carefully and use them as visual references.
If Rhino selection context is provided, use those exact objects where appropriate.
After completing the task, briefly explain what you created or changed.
"""


def home_folder():
    return os.path.expanduser("~")


def copilot_mcp_config_path():
    return os.path.join(home_folder(), ".copilot", "mcp-config.json")


def find_existing(paths):
    for path in paths:
        if path and os.path.isfile(path):
            return path
    return None


def find_copilot():
    result = shutil.which("copilot")
    if result:
        return result
    return find_existing([
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Microsoft VS Code", "bin", "copilot-shim", "copilot.exe"),
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Links", "copilot.exe"),
        os.path.join(os.environ.get("APPDATA", ""), "npm", "copilot.cmd"),
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "GitHub Copilot", "copilot.exe"),
    ])


def find_claude():
    result = shutil.which("claude")
    if result:
        return result
    return find_existing([
        os.path.join(home_folder(), ".local", "bin", "claude.exe"),
        os.path.join(os.environ.get("APPDATA", ""), "npm", "claude.cmd"),
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Claude", "claude.exe"),
    ])


def find_codex():
    result = shutil.which("codex")
    if result:
        return result
    return find_existing([
        os.path.join(os.environ.get("APPDATA", ""), "npm", "codex.cmd"),
        os.path.join(home_folder(), ".local", "bin", "codex.exe"),
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Codex", "codex.exe"),
    ])


def find_rhino_mcp():
    path = copilot_mcp_config_path()
    try:
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8") as file:
                data = json.load(file)
            command = data.get("mcpServers", {}).get("rhino", {}).get("command")
            if command:
                command = os.path.expandvars(command)
                if os.path.isfile(command):
                    return command
    except Exception:
        pass

    fallback = os.path.join(
        os.environ.get("APPDATA", ""),
        "McNeel", "Rhinoceros", "ai", "bin", "rhino-mcp-router.exe"
    )
    return fallback if os.path.isfile(fallback) else None


def ensure_copilot_mcp_config(router_path):
    path = copilot_mcp_config_path()
    if os.path.isfile(path):
        return True
    try:
        folder = os.path.dirname(path)
        if not os.path.isdir(folder):
            os.makedirs(folder)
        data = {
            "mcpServers": {
                "rhino": {
                    "type": "local",
                    "command": router_path,
                    "args": [],
                    "tools": ["*"],
                    "timeout": 60000,
                }
            }
        }
        with open(path, "w", encoding="utf-8") as file:
            json.dump(data, file, indent=2)
        return True
    except Exception:
        return False


def create_claude_mcp_config(router_path):
    path = os.path.join(tempfile.gettempdir(), "rhino_ai_studio_claude_mcp.json")
    data = {"mcpServers": {"rhino": {"command": router_path, "args": []}}}
    with open(path, "w", encoding="utf-8") as file:
        json.dump(data, file, indent=2)
    return path


def build_command(executable, arguments):
    lower = executable.lower()
    if lower.endswith(".cmd") or lower.endswith(".bat"):
        cmd_exe = os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe")
        return [cmd_exe, "/d", "/c", executable] + arguments
    return [executable] + arguments


def hidden_startup():
    try:
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = 0
        return info
    except Exception:
        return None


def execute_hidden(executable, arguments):
    command = build_command(executable, arguments)
    creation_flags = CREATE_NO_WINDOW if os.name == "nt" else 0
    process = subprocess.run(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace",
        cwd=home_folder(),
        startupinfo=hidden_startup(),
        creationflags=creation_flags,
        shell=False,
    )
    return process.returncode, process.stdout, process.stderr


# ==============================================================
# SAFE IMAGE PROCESSING
# ============================================================== 

def _sanitize_with_pillow(source_path, output_path, max_dim):
    from PIL import Image as PILImage
    from PIL import ImageOps

    with PILImage.open(source_path) as source:
        try:
            source.seek(0)
        except Exception:
            pass

        try:
            source = ImageOps.exif_transpose(source)
        except Exception:
            pass

        has_alpha = "A" in source.getbands()
        source = source.convert("RGBA" if has_alpha else "RGB")

        try:
            resample = PILImage.Resampling.LANCZOS
        except Exception:
            resample = PILImage.LANCZOS

        source.thumbnail((max_dim, max_dim), resample)

        # New image object intentionally strips EXIF / ICC metadata.
        clean = PILImage.new(source.mode, source.size)
        clean.paste(source)
        clean.save(output_path, "PNG", optimize=True)
        clean.close()

    return output_path


def _sanitize_with_system_drawing(source_path, output_path, max_dim):
    import clr

    try:
        clr.AddReference("System.Drawing.Common")
    except Exception:
        clr.AddReference("System.Drawing")

    from System.Drawing import Image as SDImage
    from System.Drawing import Bitmap, Graphics
    from System.Drawing.Drawing2D import InterpolationMode, SmoothingMode, PixelOffsetMode, CompositingQuality
    from System.Drawing.Imaging import ImageFormat

    source = None
    bitmap = None
    graphics = None

    try:
        source = SDImage.FromFile(source_path)
        width = int(source.Width)
        height = int(source.Height)

        if width <= 0 or height <= 0:
            raise Exception("Invalid image dimensions")

        scale = min(1.0, float(max_dim) / float(max(width, height)))
        new_width = max(1, int(round(width * scale)))
        new_height = max(1, int(round(height * scale)))

        # New bitmap intentionally removes source metadata.
        bitmap = Bitmap(new_width, new_height)
        graphics = Graphics.FromImage(bitmap)
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic
        graphics.SmoothingMode = SmoothingMode.HighQuality
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
        graphics.CompositingQuality = CompositingQuality.HighQuality
        graphics.DrawImage(source, 0, 0, new_width, new_height)
        bitmap.Save(output_path, ImageFormat.Png)
        return output_path

    finally:
        if graphics is not None:
            try:
                graphics.Dispose()
            except Exception:
                pass
        if bitmap is not None:
            try:
                bitmap.Dispose()
            except Exception:
                pass
        if source is not None:
            try:
                source.Dispose()
            except Exception:
                pass


def sanitize_image(source_path, output_path, max_dim):
    if not source_path or not os.path.isfile(source_path):
        raise Exception("Image file was not found")

    pillow_error = None
    try:
        return _sanitize_with_pillow(source_path, output_path, max_dim)
    except Exception as error:
        pillow_error = error

    drawing_error = None
    try:
        return _sanitize_with_system_drawing(source_path, output_path, max_dim)
    except Exception as error:
        drawing_error = error

    raise Exception(
        "Could not safely process image. "
        "Use JPG or PNG. Pillow error: %s | System.Drawing error: %s"
        % (pillow_error, drawing_error)
    )


def image_to_data_uri(path):
    with open(path, "rb") as file:
        encoded = base64.b64encode(file.read()).decode("ascii")
    return "data:image/png;base64," + encoded


# ==============================================================
# MAIN WINDOW
# ============================================================== 

class RhinoAIStudio(forms.Form):

    def __init__(self):
        super().__init__()

        self.Title = APP_NAME
        self.ClientSize = drawing.Size(430, 820)
        self.MinimumSize = drawing.Size(360, 560)
        self.Resizable = True

        self.busy = False
        self.mcp_started = False
        self.provider = "copilot"
        self.draft = ""
        self.attachments = []
        self.selection_context = []
        self.turns = []
        self.attachment_cache_dir = tempfile.mkdtemp(prefix="rhino_ai_studio_images_")

        self.messages = [
            (
                "assistant",
                "Rhino AI Studio",
                "Ready.\n\nAsk me to create, modify, organise or analyse something in the active Rhino model.",
            )
        ]

        self.copilot_path = None
        self.claude_path = None
        self.codex_path = None
        self.rhino_mcp_path = None

        self.web = forms.WebView()
        self.web.DocumentLoading += self.on_document_loading
        self.Content = self.web
        self.Closed += self.on_closed

        self.refresh_environment()
        self.render_ui()

    def on_closed(self, sender, event):
        try:
            shutil.rmtree(self.attachment_cache_dir, ignore_errors=True)
        except Exception:
            pass

    def place_on_right(self):
        try:
            work = forms.Screen.PrimaryScreen.WorkingArea
            width = 430
            height = min(850, int(work.Height - 70))
            height = max(height, 560)
            self.ClientSize = drawing.Size(width, height)
            self.Location = drawing.Point(
                int(work.X + work.Width - width - 16),
                int(work.Y + 36),
            )
        except Exception:
            pass

    def provider_ready(self, provider):
        if provider == "copilot":
            return bool(self.copilot_path)
        if provider == "claude":
            return bool(self.claude_path)
        if provider == "codex":
            return bool(self.codex_path)
        return False

    def provider_name(self, provider=None):
        provider = provider or self.provider
        return {
            "copilot": "GitHub Copilot",
            "claude": "Claude Code",
            "codex": "Codex",
        }.get(provider, "AI")

    def refresh_environment(self):
        self.copilot_path = find_copilot()
        self.claude_path = find_claude()
        self.codex_path = find_codex()
        self.rhino_mcp_path = find_rhino_mcp()

    def h(self, value):
        return html.escape(str(value or ""))

    def message_html(self):
        result = []
        for role, speaker, message in self.messages:
            safe_speaker = self.h(speaker)
            safe_message = self.h(message).replace("\n", "<br>")
            if role == "user":
                result.append(
                    '<div class="message-row user-row">'
                    '<div class="message user-message">'
                    '<div class="speaker">%s</div>'
                    '<div class="message-text">%s</div>'
                    '</div></div>' % (safe_speaker, safe_message)
                )
            else:
                result.append(
                    '<div class="message-row assistant-row">'
                    '<div class="assistant-icon">R</div>'
                    '<div class="message assistant-message">'
                    '<div class="speaker">%s</div>'
                    '<div class="message-text">%s</div>'
                    '</div></div>' % (safe_speaker, safe_message)
                )
        return "\n".join(result)

    def attachment_html(self):
        if not self.attachments and not self.selection_context:
            return ""

        items = []
        for record in self.attachments:
            name = self.h(os.path.basename(record.get("original", "image")))
            preview = record.get("preview")
            ready = record.get("ready", False)
            error = record.get("error")

            if preview:
                visual = '<img class="attach-thumb" src="%s">' % preview
            else:
                visual = '<div class="attach-placeholder">...</div>'

            status = "Ready" if ready else "Preparing..."
            if error:
                status = "Image error"

            items.append(
                '<div class="attach-card">%s'
                '<div class="attach-name">%s</div>'
                '<div class="attach-status">%s</div>'
                '</div>' % (visual, name, self.h(status))
            )

        if self.selection_context:
            count = len(self.selection_context)
            items.append(
                '<div class="selection-card">◉ %d selected Rhino object%s</div>'
                % (count, "" if count == 1 else "s")
            )

        return "\n".join(items)

    def build_html(self):
        copilot_status = "Ready" if self.copilot_path else "Not installed"
        claude_status = "Ready" if self.claude_path else "Not installed"
        codex_status = "Ready" if self.codex_path else "Not installed"

        if not self.rhino_mcp_path:
            mcp_status, mcp_class = "MCP not found", "bad"
        elif self.mcp_started:
            mcp_status, mcp_class = "MCP connected", "good"
        else:
            mcp_status, mcp_class = "MCP available", "neutral"

        if self.provider_ready(self.provider):
            agent_status, agent_class = self.provider_name() + " ready", "good"
        else:
            agent_status, agent_class = self.provider_name() + " unavailable", "neutral"

        page = r"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
*{box-sizing:border-box}html,body{width:100%;height:100%;margin:0}body{font-family:"Segoe UI",Arial,sans-serif;background:#fff;color:#202126;overflow:hidden}.app{width:100%;height:100%;display:flex;flex-direction:column;background:#fff}.topbar{flex-shrink:0;padding:13px 14px 11px;border-bottom:1px solid #e6e8ed}.brand-row{display:flex;align-items:center}.logo{width:34px;height:34px;flex:0 0 34px;border-radius:9px;background:#26282e;color:#fff;display:flex;align-items:center;justify-content:center;font-size:15px;font-weight:700;margin-right:10px}.brand-text{min-width:0;flex:1}.title{font-size:15px;font-weight:650}.subtitle{margin-top:2px;color:#878a95;font-size:10px}.provider-row{margin-top:11px;display:flex;gap:7px}select{flex:1;min-width:0;height:35px;padding:0 9px;border-radius:8px;border:1px solid #dfe1e7;background:#fff;color:#272930;font-family:inherit;font-size:11px;outline:none}.icon-button{height:35px;min-width:38px;padding:0 10px;border:1px solid #dfe1e7;border-radius:8px;background:#fff;color:#4f525c;font-family:inherit;font-size:11px;cursor:pointer}.status-row{flex-shrink:0;min-height:38px;padding:7px 14px;border-bottom:1px solid #eceef2;background:#fafbfc;display:flex;align-items:center;gap:6px}.status-pill{max-width:50%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding:5px 8px;border-radius:20px;font-size:9px;font-weight:600}.good{background:#e5f7e9;color:#16823a}.neutral{background:#eff0f3;color:#666a75}.bad{background:#fae8e8;color:#a13c3c}.status-spacer{flex:1}.status-action{border:0;background:transparent;color:#656975;font-family:inherit;font-size:10px;cursor:pointer}.quick-actions{flex-shrink:0;padding:8px 12px;display:flex;gap:6px;overflow-x:auto;border-bottom:1px solid #eceef2}.quick{flex:0 0 auto;height:28px;padding:0 10px;border:1px solid #e0e2e8;border-radius:14px;background:#fff;color:#555965;font-family:inherit;font-size:9px;cursor:pointer}.chat{flex:1;min-height:0;overflow-y:auto;padding:14px 12px 18px;background:#fff}.message-row{width:100%;display:flex;margin-bottom:15px;align-items:flex-start}.user-row{justify-content:flex-end}.assistant-row{justify-content:flex-start}.assistant-icon{width:29px;height:29px;flex:0 0 29px;margin-right:8px;border-radius:8px;background:#292b31;color:#fff;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700}.message{max-width:84%;border-radius:12px;padding:10px 12px}.assistant-message{background:#f3f4f6}.user-message{background:#eeeaff}.speaker{margin-bottom:5px;color:#777b86;font-size:9px;font-weight:600}.message-text{color:#24262c;font-size:11px;line-height:1.48}.context-area{flex-shrink:0;padding:0 12px;background:#fff}.context-chips{display:flex;gap:6px;overflow-x:auto;padding-bottom:5px}.attach-card{width:78px;flex:0 0 78px;border:1px solid #e0e2e8;border-radius:8px;padding:4px;background:#fafafa}.attach-thumb,.attach-placeholder{width:68px;height:54px;border-radius:5px;object-fit:cover;background:#eef0f3;display:flex;align-items:center;justify-content:center;color:#888;font-size:10px}.attach-name{margin-top:4px;font-size:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.attach-status{margin-top:2px;font-size:7px;color:#8a8d96}.selection-card{flex:0 0 auto;align-self:center;padding:7px 9px;border-radius:7px;background:#f0f1f4;color:#626671;font-size:9px}.composer-wrapper{flex-shrink:0;padding:8px 11px 12px}.composer{border:1px solid #7055e8;border-radius:11px;background:#fff;padding:8px}textarea{width:100%;height:67px;resize:none;border:0;outline:none;background:transparent;color:#24262c;font-family:inherit;font-size:11px;line-height:1.4}textarea::placeholder{color:#9a9ca5}.composer-bottom{margin-top:5px;display:flex;align-items:center;gap:5px}.tool-button{min-width:30px;height:30px;border:0;border-radius:7px;background:#f2f3f5;color:#595d68;font-family:inherit;font-size:10px;cursor:pointer}.send{min-width:69px;height:31px;margin-left:auto;padding:0 11px;border:0;border-radius:8px;background:#6246df;color:#fff;font-family:inherit;font-size:10px;font-weight:650;cursor:pointer}.send:disabled{opacity:.55;cursor:default}.bottom-info{margin-top:5px;text-align:right;color:#a0a2aa;font-size:8px}
</style>
</head>
<body>
<div class="app">
<div class="topbar"><div class="brand-row"><div class="logo">R</div><div class="brand-text"><div class="title">Rhino AI Studio</div><div class="subtitle">AI assistant for the active Rhino model</div></div></div><div class="provider-row"><select onchange="nav('provider',{value:this.value})"><option value="copilot" __COPILOT_SELECTED__>GitHub Copilot — __COPILOT_STATUS__</option><option value="claude" __CLAUDE_SELECTED__>Claude Code — __CLAUDE_STATUS__</option><option value="codex" __CODEX_SELECTED__>Codex — __CODEX_STATUS__</option></select><button class="icon-button" onclick="nav('newchat')">+ Chat</button></div></div>
<div class="status-row"><div class="status-pill __MCP_CLASS__">__MCP_STATUS__</div><div class="status-pill __AGENT_CLASS__">__AGENT_STATUS__</div><div class="status-spacer"></div><button class="status-action" onclick="nav('mcp')">Start MCP</button><button class="status-action" onclick="nav('refresh')">Refresh</button></div>
<div class="quick-actions"><button class="quick" onclick="setPrompt('Create a clean architectural massing model using simple editable Rhino geometry.')">+ Massing</button><button class="quick" onclick="setPrompt('Analyse the selected Rhino objects and tell me what they are before making any changes.')">Analyse selection</button><button class="quick" onclick="setPrompt('Organize the current Rhino model into clear architectural layers without deleting geometry.')">Organize layers</button></div>
<div class="chat" id="chat">__MESSAGES__</div>
<div class="context-area"><div class="context-chips">__CONTEXT_CHIPS__</div></div>
<div class="composer-wrapper"><div class="composer"><textarea id="prompt" placeholder="Ask AI to create, modify or analyse something in Rhino...">__DRAFT__</textarea><div class="composer-bottom"><button class="tool-button" onclick="nav('attach')">📎 Image</button><button class="tool-button" onclick="nav('selection')">◉ Selection</button><button class="tool-button" onclick="nav('clearcontext')">Clear</button><button class="send" onclick="sendPrompt()" __SEND_DISABLED__>__SEND_LABEL__</button></div></div><div class="bottom-info">Ctrl + Enter to send</div></div>
</div>
<script>
function getDraft(){var b=document.getElementById('prompt');return b?(b.value||''):''}
function setPrompt(t){var b=document.getElementById('prompt');if(b){b.value=t;b.focus()}}
function nav(action,params){params=params||{};if(action!=='send'){params.draft=getDraft()}var parts=[];for(var k in params){if(Object.prototype.hasOwnProperty.call(params,k)){parts.push(encodeURIComponent(k)+'='+encodeURIComponent(params[k]))}}var target='rhinoai://'+action;if(parts.length){target+='?'+parts.join('&')}window.location.href=target}
function sendPrompt(){var t=getDraft().trim();if(t){nav('send',{message:t})}}
document.addEventListener('keydown',function(e){if(e.ctrlKey&&e.key==='Enter'){e.preventDefault();sendPrompt()}})
window.addEventListener('load',function(){var c=document.getElementById('chat');if(c){c.scrollTop=c.scrollHeight}})
</script>
</body>
</html>
"""

        replacements = {
            "__COPILOT_STATUS__": copilot_status,
            "__CLAUDE_STATUS__": claude_status,
            "__CODEX_STATUS__": codex_status,
            "__COPILOT_SELECTED__": "selected" if self.provider == "copilot" else "",
            "__CLAUDE_SELECTED__": "selected" if self.provider == "claude" else "",
            "__CODEX_SELECTED__": "selected" if self.provider == "codex" else "",
            "__MCP_STATUS__": mcp_status,
            "__MCP_CLASS__": mcp_class,
            "__AGENT_STATUS__": agent_status,
            "__AGENT_CLASS__": agent_class,
            "__MESSAGES__": self.message_html(),
            "__CONTEXT_CHIPS__": self.attachment_html(),
            "__DRAFT__": self.h(self.draft),
            "__SEND_LABEL__": "Working..." if self.busy else "Send",
            "__SEND_DISABLED__": "disabled" if self.busy else "",
        }

        for old, new in replacements.items():
            page = page.replace(old, str(new))
        return page

    def render_ui(self):
        try:
            self.web.LoadHtml(self.build_html())
        except Exception as error:
            Rhino.RhinoApp.WriteLine("Rhino AI Studio UI error: " + str(error))

    def on_document_loading(self, sender, event):
        try:
            uri = event.Uri
            if uri is None or str(uri.Scheme).lower() != "rhinoai":
                return
            event.Cancel = True
            action = str(uri.Host).lower()
            query_text = str(uri.Query)
            if query_text.startswith("?"):
                query_text = query_text[1:]
            parsed = urllib.parse.parse_qs(query_text, keep_blank_values=True)
            values = {key: (items[0] if items else "") for key, items in parsed.items()}
            forms.Application.Instance.AsyncInvoke(lambda: self.handle_action(action, values))
        except Exception as error:
            Rhino.RhinoApp.WriteLine("Rhino AI Studio event error: " + str(error))

    def handle_action(self, action, values):
        if "draft" in values:
            self.draft = values.get("draft", "")

        if action == "provider":
            selected = values.get("value", "copilot")
            if selected in ("copilot", "claude", "codex"):
                self.provider = selected
            self.render_ui()
            return

        if action == "mcp":
            self.start_rhino_mcp(True)
            self.render_ui()
            return

        if action == "refresh":
            self.refresh_environment()
            self.render_ui()
            return

        if action == "newchat":
            self.new_chat()
            self.render_ui()
            return

        if action == "attach":
            self.attach_images()
            self.render_ui()
            return

        if action == "selection":
            self.capture_selection()
            self.render_ui()
            return

        if action == "clearcontext":
            self.attachments = []
            self.selection_context = []
            self.render_ui()
            return

        if action == "send":
            message = values.get("message", "").strip()
            if message:
                self.send_message(message)

    def run_on_ui(self, callback):
        try:
            forms.Application.Instance.AsyncInvoke(callback)
        except Exception:
            try:
                callback()
            except Exception:
                pass

    def start_rhino_mcp(self, show_message=False):
        try:
            result = Rhino.RhinoApp.RunScript("MCPStart", False)
            self.mcp_started = bool(result)
            if show_message:
                self.messages.append((
                    "assistant",
                    "Rhino AI Studio",
                    "Rhino MCP connected." if result else "Rhino MCP could not be started.",
                ))
            return bool(result)
        except Exception as error:
            self.mcp_started = False
            if show_message:
                self.messages.append(("assistant", "Rhino AI Studio", "MCP error: " + str(error)))
            return False

    def auto_start_mcp(self):
        self.start_rhino_mcp(False)
        self.refresh_environment()
        self.render_ui()

    # ----------------------------------------------------------
    # IMAGE ATTACHMENT: file dialog only on UI thread.
    # Actual image decoding/resizing happens on a worker thread.
    # ----------------------------------------------------------
    def attach_images(self):
        try:
            file_filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp||"
            files = rs.OpenFileNames("Attach image reference", file_filter)
            if not files:
                return

            existing = {r.get("original") for r in self.attachments}

            for path in files:
                if not path or not os.path.isfile(path) or path in existing:
                    continue

                record = {
                    "original": path,
                    "safe": None,
                    "preview": None,
                    "ready": False,
                    "error": None,
                }
                self.attachments.append(record)
                existing.add(path)

                worker = threading.Thread(target=self.prepare_attachment_record, args=(record,))
                worker.daemon = True
                worker.start()

        except Exception as error:
            self.messages.append(("assistant", "Rhino AI Studio", "Image attachment error: " + str(error)))

    def prepare_attachment_record(self, record):
        try:
            token = uuid.uuid4().hex
            safe_path = os.path.join(self.attachment_cache_dir, token + "_safe.png")
            preview_path = os.path.join(self.attachment_cache_dir, token + "_preview.png")

            # Full AI copy: resized and metadata stripped.
            sanitize_image(record["original"], safe_path, MAX_AI_IMAGE_DIM)

            # UI preview: tiny separate copy only.
            sanitize_image(safe_path, preview_path, PREVIEW_IMAGE_DIM)

            record["safe"] = safe_path
            record["preview"] = image_to_data_uri(preview_path)
            record["ready"] = True
            record["error"] = None

        except Exception as error:
            record["ready"] = False
            record["error"] = str(error)

        self.run_on_ui(self.render_ui)

    def prepare_turn_attachments(self, records):
        safe_paths = []
        source_names = []

        for record in records:
            original = record.get("original")
            if not original or not os.path.isfile(original):
                raise Exception("Attached image no longer exists")

            safe_path = record.get("safe")
            if not safe_path or not os.path.isfile(safe_path):
                token = uuid.uuid4().hex
                safe_path = os.path.join(self.attachment_cache_dir, token + "_send.png")
                sanitize_image(original, safe_path, MAX_AI_IMAGE_DIM)

            safe_paths.append(safe_path)
            source_names.append(os.path.basename(original))

        return safe_paths, source_names

    def capture_selection(self):
        try:
            doc = Rhino.RhinoDoc.ActiveDoc
            if doc is None:
                self.messages.append(("assistant", "Rhino AI Studio", "No active Rhino document."))
                return

            selected = list(doc.Objects.GetSelectedObjects(False, False))
            if not selected:
                self.selection_context = []
                self.messages.append((
                    "assistant",
                    "Rhino AI Studio",
                    "No Rhino objects are selected. Select objects first, then click Selection.",
                ))
                return

            context = []
            for obj in selected:
                try:
                    name = obj.Attributes.Name or ""
                except Exception:
                    name = ""
                context.append({"id": str(obj.Id), "type": str(obj.ObjectType), "name": name})
            self.selection_context = context

        except Exception as error:
            self.messages.append(("assistant", "Rhino AI Studio", "Selection error: " + str(error)))

    def new_chat(self):
        if self.busy:
            return
        self.turns = []
        self.attachments = []
        self.selection_context = []
        self.draft = ""
        self.messages = [("assistant", "Rhino AI Studio", "New conversation started.\n\nWhat would you like to do in Rhino?")]

    def build_context_prompt(self, request, source_names, selection):
        text = SYSTEM_PROMPT

        if self.turns:
            text += "\n\nRECENT CONVERSATION:\n"
            for role, content in self.turns[-10:]:
                text += "\n%s:\n%s\n" % (role, content)

        if selection:
            text += "\n\nCURRENT RHINO SELECTION:\n"
            for item in selection:
                text += "- Object ID: %s | Type: %s" % (item.get("id", ""), item.get("type", ""))
                if item.get("name"):
                    text += " | Name: " + item.get("name")
                text += "\n"
            text += "\nThe user explicitly selected these objects as context."

        if source_names:
            text += "\n\nIMAGE REFERENCES ATTACHED:\n"
            for name in source_names:
                text += "- %s\n" % name
            text += "\nInspect the attached images carefully before carrying out the request."

        text += "\n\nCURRENT USER REQUEST:\n" + request
        return text

    def send_message(self, message):
        if self.busy:
            return

        if not self.mcp_started:
            self.start_rhino_mcp(False)
        self.refresh_environment()

        if not self.provider_ready(self.provider):
            self.messages.append(("assistant", "Rhino AI Studio", self.provider_name() + " is not installed or detected."))
            self.render_ui()
            return

        if not self.rhino_mcp_path:
            self.messages.append(("assistant", "Rhino AI Studio", "Rhino MCP router could not be found."))
            self.render_ui()
            return

        attachments_for_turn = [dict(record) for record in self.attachments]
        selection_for_turn = list(self.selection_context)

        self.attachments = []
        self.selection_context = []
        self.draft = ""

        displayed = message
        extras = []
        if attachments_for_turn:
            extras.append("%d image%s" % (len(attachments_for_turn), "" if len(attachments_for_turn) == 1 else "s"))
        if selection_for_turn:
            extras.append("%d selected Rhino object%s" % (len(selection_for_turn), "" if len(selection_for_turn) == 1 else "s"))
        if extras:
            displayed += "\n\nContext: " + ", ".join(extras)

        self.messages.append(("user", "You", displayed))
        self.busy = True
        self.render_ui()

        worker = threading.Thread(
            target=self.ai_worker,
            args=(self.provider, message, attachments_for_turn, selection_for_turn),
        )
        worker.daemon = True
        worker.start()

    def ai_worker(self, provider, user_message, attachment_records, selection):
        try:
            safe_attachments, source_names = self.prepare_turn_attachments(attachment_records)

            if provider == "copilot":
                code, output, error = self.run_copilot(user_message, safe_attachments, source_names, selection)
            elif provider == "claude":
                code, output, error = self.run_claude(user_message, safe_attachments, source_names, selection)
            elif provider == "codex":
                code, output, error = self.run_codex(user_message, safe_attachments, source_names, selection)
            else:
                code, output, error = 1, "", "Unknown AI provider."

            if code == 0:
                response = output.strip() or "Task completed."
                self.turns.append(("USER", user_message))
                self.turns.append(("ASSISTANT", response))

                def success():
                    self.messages.append(("assistant", self.provider_name(provider), response))
                    self.busy = False
                    self.render_ui()

                self.run_on_ui(success)
            else:
                result = error.strip() or output.strip() or ("AI returned error code " + str(code))

                def failure():
                    self.messages.append(("assistant", "Rhino AI Studio", "AI Error:\n\n" + result))
                    self.busy = False
                    self.render_ui()

                self.run_on_ui(failure)

        except Exception as error:
            details = str(error) + "\n\n" + traceback.format_exc()

            def exception():
                self.messages.append(("assistant", "Rhino AI Studio", details))
                self.busy = False
                self.render_ui()

            self.run_on_ui(exception)

    def run_copilot(self, user_message, safe_attachments, source_names, selection):
        ensure_copilot_mcp_config(self.rhino_mcp_path)
        prompt = self.build_context_prompt(user_message, source_names, selection)

        def execute_with_session(session_id):
            arguments = [
                "--session-id", session_id,
                "--enable-mcp-server=rhino",
                "--allow-tool=rhino",
                "--no-ask-user",
                "--no-auto-update",
                "--stream=off",
                "--no-color",
            ]
            for image_path in safe_attachments:
                arguments.append("--attachment=" + image_path)
            arguments.extend(["-s", "-p", prompt])
            return execute_hidden(self.copilot_path, arguments)

        code, output, error = execute_with_session(str(uuid.uuid4()))
        combined = str(output or "") + "\n" + str(error or "")

        if "SessionInUseError" in combined or "is in use by another Copilot process" in combined:
            code, output, error = execute_with_session(str(uuid.uuid4()))

        return code, output, error

    def run_claude(self, user_message, safe_attachments, source_names, selection):
        mcp_config = create_claude_mcp_config(self.rhino_mcp_path)
        prompt = self.build_context_prompt(user_message, source_names, selection)
        arguments = [
            "-p", prompt,
            "--mcp-config", mcp_config,
            "--allowedTools", "mcp__rhino", "Read",
            "--output-format", "text",
        ]

        if safe_attachments:
            folders = []
            for path in safe_attachments:
                folder = os.path.dirname(path)
                if folder not in folders:
                    folders.append(folder)
            if folders:
                arguments.append("--add-dir")
                arguments.extend(folders)

        return execute_hidden(self.claude_path, arguments)

    def ensure_codex_mcp(self):
        code, output, error = execute_hidden(self.codex_path, ["mcp", "get", "rhino", "--json"])
        if code == 0:
            return True
        code, output, error = execute_hidden(self.codex_path, ["mcp", "add", "rhino", "--", self.rhino_mcp_path])
        return code == 0

    def run_codex(self, user_message, safe_attachments, source_names, selection):
        if not self.ensure_codex_mcp():
            return 1, "", "Codex could not configure Rhino MCP."

        prompt = self.build_context_prompt(user_message, source_names, selection)
        output_path = os.path.join(tempfile.gettempdir(), "rhino_ai_studio_codex_output.txt")
        try:
            if os.path.isfile(output_path):
                os.remove(output_path)
        except Exception:
            pass

        arguments = [
            "-c", 'mcp_servers.rhino.default_tools_approval_mode="approve"',
            "exec",
            "--skip-git-repo-check",
            "--output-last-message", output_path,
            prompt,
        ]

        if safe_attachments:
            arguments.extend(["--image", ",".join(safe_attachments)])

        code, stdout, stderr = execute_hidden(self.codex_path, arguments)

        if code == 0 and os.path.isfile(output_path):
            try:
                with open(output_path, "r", encoding="utf-8") as file:
                    final_message = file.read()
                if final_message.strip():
                    stdout = final_message
            except Exception:
                pass

        return code, stdout, stderr


_rhino_ai_studio = None


def show_rhino_ai_studio():
    global _rhino_ai_studio

    try:
        if _rhino_ai_studio is not None:
            try:
                _rhino_ai_studio.Close()
            except Exception:
                pass

        _rhino_ai_studio = RhinoAIStudio()
        _rhino_ai_studio.place_on_right()

        try:
            owner = Rhino.UI.RhinoEtoApp.MainWindow
            if owner is not None:
                _rhino_ai_studio.Show(owner)
            else:
                _rhino_ai_studio.Show()
        except Exception:
            _rhino_ai_studio.Show()

        try:
            forms.Application.Instance.AsyncInvoke(_rhino_ai_studio.auto_start_mcp)
        except Exception:
            _rhino_ai_studio.auto_start_mcp()

    except Exception as error:
        print("Rhino AI Studio failed to open.")
        print(str(error))
        print(traceback.format_exc())


show_rhino_ai_studio()