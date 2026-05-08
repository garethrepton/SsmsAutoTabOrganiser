using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AutoTabOrganiser.UI
{
    [Guid("d3c8a2b1-4f5e-4d6c-9c2a-1a3b5d7e9f04")]
    public sealed class TabOrganiserToolWindow : ToolWindowPane
    {
        public TabOrganiserToolWindow() : base(null)
        {
            Caption = "Tab Organiser";
            Content = new ToolWindowControl();
        }

        internal ToolWindowControl Control => Content as ToolWindowControl;

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            // Tool windows can be restored at startup without our command running, leaving the
            // control un-wired (gear button etc. would no-op). Wire from here so it always works.
            (Package as AutoTabOrganiserPackage)?.WireToolWindowSafe(this);
        }
    }
}
