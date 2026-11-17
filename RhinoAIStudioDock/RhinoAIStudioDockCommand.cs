using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace RhinoAIStudioDock
{
    public sealed class RhinoAIStudioDockCommand : Command
    {
        public override string EnglishName => "RhinoAIStudioDock";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(RhinoAIStudioPanel.PanelId);
            return Result.Success;
        }
    }
}
