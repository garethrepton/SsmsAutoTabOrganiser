using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows.Input;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Util;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace AutoTabOrganiser.Editor
{
    /// <summary>
    /// MEF key processor that makes Ctrl+Shift+T open the Quick Switcher from inside the SSMS
    /// SQL editor.
    ///
    /// Why this exists: the editor consumes keystrokes through its own command chain (the
    /// Win32 message pump pre-translates them via IVsFilterKeys) before WPF's
    /// InputManager.PreProcessInput or the VSCT keybinding tables get a look-in, so neither of
    /// those reliably fires when focus is in a query window — which is exactly where the user
    /// wants the shortcut. A KeyProcessor is attached directly to the text view and sees every
    /// keystroke first, so it is the only dependable interception point in the editor scope.
    /// On a match we dispatch the registered Quick Switcher command
    /// (guidAutoTabOrganiserCmdSet : QuickSwitcherCommandId) through the shell command
    /// dispatcher, which routes to the very same handler the toolbar button uses.
    ///
    /// Diagnostics: this type logs to the same %APPDATA%\AutoTabOrganiser\logs file the
    /// package uses, tagged "[keyproc]", so attach/keystroke/dispatch can be traced without a
    /// debugger.
    /// </summary>
    [Export(typeof(IKeyProcessorProvider))]
    [Name("AutoTabOrganiserQuickSwitchKeyProcessor")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class QuickSwitchKeyProcessorProvider : IKeyProcessorProvider
    {
        private readonly SVsServiceProvider _serviceProvider;

        [ImportingConstructor]
        public QuickSwitchKeyProcessorProvider([Import] SVsServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            KeyProcDiagnostics.Log?.Debug("[keyproc] provider constructed (MEF export discovered).");
        }

        public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
        {
            // Read the configured chord per view, so opening a new query window picks up an
            // edited settings.json without restarting SSMS.
            ModifierKeys modifiers = ModifierKeys.None;
            Key key = Key.None;
            string chord = null;
            try
            {
                chord = new SettingsStore(SettingsStore.DefaultSettingsFilePath()).Load()?.Ui?.QuickSwitchHotkey;
                HotkeyChord.TryParse(chord, out modifiers, out key);
            }
            catch { }

            try
            {
                var ct = wpfTextView?.TextBuffer?.ContentType;
                var bases = ct == null ? "" : string.Join(",", ct.BaseTypes.Select(b => b.TypeName));
                KeyProcDiagnostics.Log?.Debug($"[keyproc] attached; hotkey='{chord}' parsedKey={key} contentType={ct?.TypeName} bases=[{bases}]");
            }
            catch { }

            return new QuickSwitchKeyProcessor(_serviceProvider, modifiers, key);
        }
    }

    internal sealed class QuickSwitchKeyProcessor : KeyProcessor
    {
        private const ModifierKeys ModMask =
            ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows;
        private static readonly Guid CmdSet = PackageGuids.AutoTabOrganiserCmdSet;
        private const uint QuickSwitcherCommandId = (uint)PackageIds.QuickSwitcherCommandId;

        private readonly SVsServiceProvider _serviceProvider;
        private readonly ModifierKeys _modifiers;
        private readonly Key _key;

        public QuickSwitchKeyProcessor(SVsServiceProvider serviceProvider, ModifierKeys modifiers, Key key)
        {
            _serviceProvider = serviceProvider;
            _modifiers = modifiers;
            _key = key;
        }

        public override void PreviewKeyDown(KeyEventArgs args)
        {
            if (_key != Key.None && args.Key == _key && (Keyboard.Modifiers & ModMask) == _modifiers)
            {
                bool ok = TryDispatchQuickSwitcher();
                KeyProcDiagnostics.Log?.Debug($"[keyproc] hotkey matched in editor; dispatch result={ok}");
                if (ok) args.Handled = true; // swallow so the editor's own binding (if any) doesn't also run
            }

            base.PreviewKeyDown(args);
        }

        private bool TryDispatchQuickSwitcher()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!(_serviceProvider.GetService(typeof(SUIHostCommandDispatcher)) is IOleCommandTarget dispatcher))
                {
                    KeyProcDiagnostics.Log?.Debug("[keyproc] SUIHostCommandDispatcher unavailable.");
                    return false;
                }

                var guid = CmdSet;
                int hr = dispatcher.Exec(ref guid, QuickSwitcherCommandId,
                    (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
                if (hr != VSConstants.S_OK)
                    KeyProcDiagnostics.Log?.Debug($"[keyproc] Exec returned hr=0x{hr:X8}");
                return hr == VSConstants.S_OK;
            }
            catch (Exception ex)
            {
                KeyProcDiagnostics.Log?.Debug("[keyproc] dispatch threw: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Lazily-created logger pointing at the package's own log directory, so the MEF key
    /// processor (which the editor instantiates, not the package) can trace itself without a
    /// reference to the package's Logger instance.
    /// </summary>
    internal static class KeyProcDiagnostics
    {
        public static readonly Logger Log = Create();

        private static Logger Create()
        {
            try
            {
                var logsDir = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultSettingsFilePath()), "logs");
                return new Logger(logsDir, null);
            }
            catch { return null; }
        }
    }
}
