using System;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace RhinoAIStudioDock
{
    [Guid("D53A6A3D-6DB2-45F5-903F-3A0E1E9180A6")]
    public sealed class RhinoAIStudioPanel : Panel, IPanel
    {
        public static Guid PanelId => typeof(RhinoAIStudioPanel).GUID;

        public string Title { get; } = "Rhino AI Studio";

        public RhinoAIStudioPanel()
        {
            var web = new WebView();

            web.LoadHtml(@"
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<style>
html,body{height:100%;margin:0;font-family:Segoe UI,Arial;background:#f7f7f8;color:#202124}
.wrap{padding:16px}
.card{background:#fff;border:1px solid #ddd;border-radius:12px;padding:16px}
.logo{width:34px;height:34px;border-radius:8px;background:#26282e;color:white;display:flex;align-items:center;justify-content:center;font-weight:700;margin-bottom:12px}
h2{font-size:16px;margin:0 0 8px}
p{font-size:12px;line-height:1.5;color:#666;margin:0 0 12px}
.status{display:inline-block;padding:5px 8px;border-radius:12px;background:#e5f7e9;color:#16823a;font-size:10px;font-weight:600}
</style>
</head>
<body>
<div class='wrap'>
  <div class='card'>
    <div class='logo'>R</div>
    <h2>Rhino AI Studio</h2>
    <p>This is the real native Rhino dock panel.</p>
    <span class='status'>Dock ready</span>
  </div>
</div>
</body>
</html>");

            Content = web;
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
    }
}
