using System;
using System.Reflection;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace RhinoAIStudioDock
{
    public sealed class RhinoAIStudioDockPlugIn : PlugIn
    {
        public static RhinoAIStudioDockPlugIn Instance { get; private set; }

        public RhinoAIStudioDockPlugIn()
        {
            Instance = this;
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                Panels.RegisterPanel(
                    this,
                    typeof(RhinoAIStudioPanel),
                    "Rhino AI Studio",
                    (Assembly)null,
                    null,
                    PanelType.System
                );

                RhinoApp.WriteLine("Rhino AI Studio dock registered.");
                return LoadReturnCode.Success;
            }
            catch (Exception ex)
            {
                errorMessage = ex.ToString();
                return LoadReturnCode.ErrorShowDialog;
            }
        }
    }
}
