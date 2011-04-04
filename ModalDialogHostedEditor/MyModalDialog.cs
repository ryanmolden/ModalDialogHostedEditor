using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace ModalDialogHostedEditor
{
    internal class MyModalDialog : DialogWindow,
                                   IOleCommandTarget,
                                   IDisposable
    {
        #region Constants

        private const int WM_KEYFIRST = 0x0100;
        private const int WM_KEYLAST = 0x0109;

        #endregion

        private Microsoft.VisualStudio.OLE.Interop.IServiceProvider cachedOleServiceProvider;
        private IOleCommandTarget editorCommandTarget;
        private IVsCodeWindow codeWindow;
        private IVsTextView textView;
        private IVsTextBuffer textBuffer;
        private IWpfTextViewHost textViewHost;

        private bool disposed;

        internal MyModalDialog()
        {            
            CreateHostedEditor();
        }

        /// <summary>
        /// Preprocess input (keyboard) messages in order to translate them to editor commands if they map. Since we are in a modal dialog
        /// we need to tell the shell to allow pre-translate during a modal loop as well as instructing it to use the editor keyboard scope
        /// even though, as far as the shell knows, there is no editor active (in a document or tool window, the shell knows nothing about
        /// random modal dialogs such as this one).
        /// </summary>
        private void FilterThreadMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (msg.message >= WM_KEYFIRST && msg.message <= WM_KEYLAST)
            {
                IVsFilterKeys2 filterKeys = (IVsFilterKeys2)ServiceProvider.GlobalProvider.GetService(typeof(SVsFilterKeys));
                Microsoft.VisualStudio.OLE.Interop.MSG oleMSG = new Microsoft.VisualStudio.OLE.Interop.MSG() { hwnd = msg.hwnd, lParam = msg.lParam, wParam = msg.wParam, message = (uint)msg.message };

                //Ask the shell to do the command mapping for us and fire off the command if it succeeds with that mapping. We pass no 'custom' scopes
                //(third and fourth argument) because we pass VSTAEXF_UseTextEditorKBScope to indicate we want the shell to apply the text editor
                //command scope to this call.
                Guid cmdGuid;
                uint cmdId;
                int fTranslated;
                int fStartsMultiKeyChord;
                int res = filterKeys.TranslateAcceleratorEx(new Microsoft.VisualStudio.OLE.Interop.MSG[] { oleMSG },
                                                            (uint)(__VSTRANSACCELEXFLAGS.VSTAEXF_UseTextEditorKBScope | __VSTRANSACCELEXFLAGS.VSTAEXF_AllowModalState),
                                                            0 /*scope count*/,
                                                            new Guid[0] /*scopes*/,
                                                            out cmdGuid,
                                                            out cmdId,
                                                            out fTranslated,
                                                            out fStartsMultiKeyChord);

                if (fStartsMultiKeyChord == 0)
                {
                    //HACK: Work around a bug in TranslateAcceleratorEx that will report it DIDN'T do the command mapping 
                    //when in fact it did :( Problem has been fixed (since I found it while writing this code), but in the 
                    //mean time we need to successfully eat keystrokes that have been mapped to commands and dispatched, 
                    //we DON'T want them to continue on to Translate/Dispatch. "Luckily" asking TranslateAcceleratorEx to
                    //do the mapping WITHOUT firing the command will give us the right result code to indicate if the command
                    //mapped or not, unfortunately we can't always do this as it would break key-chords as it causes the shell 
                    //to not remember the first input match of a multi-part chord, hence the reason we ONLY hit this block if 
                    //it didn't tell us the input IS part of key-chord.
                    res = filterKeys.TranslateAcceleratorEx(new Microsoft.VisualStudio.OLE.Interop.MSG[] { oleMSG },
                                                            (uint)(__VSTRANSACCELEXFLAGS.VSTAEXF_NoFireCommand | __VSTRANSACCELEXFLAGS.VSTAEXF_UseTextEditorKBScope | __VSTRANSACCELEXFLAGS.VSTAEXF_AllowModalState),
                                                            0,
                                                            new Guid[0],
                                                            out cmdGuid,
                                                            out cmdId,
                                                            out fTranslated,
                                                            out fStartsMultiKeyChord);
                    handled = (res == VSConstants.S_OK);
                    return;
                }

                //We return true (that we handled the input message) if we managed to map it to a command OR it was the 
                //beginning of a multi-key chord, anything else should continue on with normal processing.
                handled = ((res == VSConstants.S_OK) || (fStartsMultiKeyChord != 0));
            }
        }

        public bool? ShowModalWithEditorHooked()
        {
            //Hook ourselves into the command chain
            IVsRegisterPriorityCommandTarget rpct = (IVsRegisterPriorityCommandTarget)ServiceProvider.GlobalProvider.GetService(typeof(SVsRegisterPriorityCommandTarget));
            uint pctCookie;
            ErrorHandler.ThrowOnFailure(rpct.RegisterPriorityCommandTarget(dwReserved: 0 , pCmdTrgt: this, pdwCookie: out pctCookie));

            //Hook into WPFs thread message handling so we can handle WM_KEYDOWN messages
            ComponentDispatcher.ThreadFilterMessage += FilterThreadMessage;

            try
            {
                return ShowModal();
            }
            finally 
            {
                //unhook from WPF's thread message handling.
                ComponentDispatcher.ThreadFilterMessage -= FilterThreadMessage;

                //Unhook ourselves from the command chain
                ErrorHandler.ThrowOnFailure(rpct.UnregisterPriorityCommandTarget(pctCookie));
            }
        }

        private void CreateHostedEditor()
        {
            //Get the component model so we can request the editor adapter factory which we can use to spin up an editor instance.
            IComponentModel componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));

            IContentTypeRegistryService contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();
            IContentType contentType = contentTypeRegistry.GetContentType("CSharp");

            IVsEditorAdaptersFactoryService editorAdapterFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            this.textBuffer = editorAdapterFactory.CreateVsTextBufferAdapter(OleServiceProvider);
            Guid CSharpLanguageService = new Guid("{694DD9B6-B865-4C5B-AD85-86356E9C88DC}");
            ErrorHandler.ThrowOnFailure(textBuffer.SetLanguageServiceID(ref CSharpLanguageService));

            string initialContents = String.Format("using System;{0}{0}namespace Lazers{0}{{{0}{1}public class Awesome{0}{1}{{{0}{1}}}{0}}}", Environment.NewLine, "    ");
            ErrorHandler.ThrowOnFailure(textBuffer.InitializeContent(initialContents, initialContents.Length));

            //Disable the splitter due to a crashing bug if we don't :(
            this.codeWindow = editorAdapterFactory.CreateVsCodeWindowAdapter(OleServiceProvider);
            ((IVsCodeWindowEx)this.codeWindow).Initialize((uint)_codewindowbehaviorflags.CWB_DISABLESPLITTER,
                                                          VSUSERCONTEXTATTRIBUTEUSAGE.VSUC_Usage_Filter,
                                                          "",
                                                          "",
                                                          0,
                                                          new INITVIEW[1]);

            this.codeWindow.SetBuffer((IVsTextLines)this.textBuffer);

            ErrorHandler.ThrowOnFailure(this.codeWindow.GetPrimaryView(out this.textView));
            this.textViewHost = editorAdapterFactory.GetWpfTextViewHost(this.textView);

            this.Content = textViewHost.HostControl;
            this.editorCommandTarget = (IOleCommandTarget)this.textView;
        }

        /// <summary>
        /// The shell's service provider as an OLE service provider (needed to create the editor bits).
        /// </summary>
        private Microsoft.VisualStudio.OLE.Interop.IServiceProvider OleServiceProvider
        {
            get
            {
                if (this.cachedOleServiceProvider == null)
                {
                    //ServiceProvider.GlobalProvider is a System.IServiceProvider, but the editor pieces want an OLE.IServiceProvider, luckily the
                    //global provider is also IObjectWithSite and we can use that to extract its underlying (OLE) IServiceProvider object.
                    IObjectWithSite objWithSite = (IObjectWithSite)ServiceProvider.GlobalProvider;

                    Guid interfaceIID = typeof(Microsoft.VisualStudio.OLE.Interop.IServiceProvider).GUID;
                    IntPtr rawSP;
                    objWithSite.GetSite(ref interfaceIID, out rawSP);
                    try
                    {
                        if (rawSP != IntPtr.Zero)
                        {
                            //Get an RCW over the raw OLE service provider pointer.
                            this.cachedOleServiceProvider = (Microsoft.VisualStudio.OLE.Interop.IServiceProvider)Marshal.GetObjectForIUnknown(rawSP);
                        }
                    }
                    finally
                    {
                        if (rawSP != IntPtr.Zero)
                        {
                            //Release the raw pointer we got from IObjectWithSite so we don't cause leaks.
                            Marshal.Release(rawSP);
                        }
                    }
                }

                return this.cachedOleServiceProvider;
            }
        }

        public int Exec(ref System.Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, System.IntPtr pvaIn, System.IntPtr pvaOut)
        {
            if (this.editorCommandTarget != null)
            {
                return this.editorCommandTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int QueryStatus(ref System.Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, System.IntPtr pCmdText)
        {
            if (this.editorCommandTarget != null)
            {
                return this.editorCommandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
            }

            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                this.disposed = true;
                this.editorCommandTarget = null;

                ((IVsPersistDocData)this.textBuffer).Close();
                this.textBuffer = null;

                this.textViewHost.Close();
                this.textViewHost = null;

                this.codeWindow.Close();
                this.codeWindow = null;

                this.textView.CloseView();
                this.textView = null;
            }
        }
    }
}