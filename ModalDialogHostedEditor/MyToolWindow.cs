using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ModalDialogHostedEditor
{
    [Guid("1d859026-38cf-4ee1-b747-21c285036119")]
    public class MyToolWindow : ToolWindowPane
    {
        public MyToolWindow() :
            base(null)
        {
            this.Caption = Resources.ToolWindowTitle;
            this.BitmapResourceID = 301;
            this.BitmapIndex = 1;
            base.Content = new MyControl();
        }
    }
}
