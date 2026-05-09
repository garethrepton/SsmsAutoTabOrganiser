using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.UI.QuickSwitcher
{
    /// <summary>
    /// Ctrl+P quick-switcher popup. Shown via <see cref="Window.ShowDialog"/> (modal) so the
    /// dispatcher frame routes keyboard input directly to us — modeless <c>Show()</c> can't
    /// hold focus against SSMS's editor reclaim even with persistent focus-claim retries.
    /// </summary>
    /// <remarks>
    /// We close exclusively via <see cref="Window.DialogResult"/>, never via a synchronous
    /// <c>Close()</c> from inside an input event. Calling <c>Close()</c> mid-keystroke is the
    /// pattern that crashed SSMS earlier; setting DialogResult posts a deferred close to
    /// the dispatcher and exits ShowDialog cleanly on the next tick.
    /// </remarks>
    internal partial class QuickSwitcherWindow : Window
    {
        private static QuickSwitcherWindow _activePopup;

        private readonly QuickSwitcherViewModel _vm;

        private QuickSwitcherWindow(QuickSwitcherViewModel vm)
        {
            _vm = vm;
            DataContext = _vm;
            InitializeComponent();
            Loaded += (s, e) => SearchBox.Focus();
        }

        public static void Show(SnapshotStore store, SettingsStore settings, Logger log,
                                Func<string, string, Task> openTabAtText, Window owner)
        {
            // Re-pressing the shortcut while the popup is open just brings it back to the
            // front rather than stacking a second popup. Under modal ShowDialog this branch
            // is rarely hit (the dispatcher queues the second keystroke), but keep it as a
            // safety net.
            if (_activePopup != null)
            {
                try { _activePopup.Activate(); _activePopup.SearchBox.Focus(); } catch { }
                return;
            }

            QuickSwitcherWindow win = null;
            string capturedTabId = null;
            string capturedFindText = null;

            // The capture lambda is what the VM invokes on Enter / double-click. It records
            // the user's pick and asks the dialog to close via DialogResult — never Close().
            Func<string, string, Task> capture = (tabId, findText) =>
            {
                capturedTabId = tabId;
                capturedFindText = findText;
                if (win != null) try { win.DialogResult = true; } catch { }
                return Task.CompletedTask;
            };

            var vm = new QuickSwitcherViewModel(store, settings, log, capture);
            win = new QuickSwitcherWindow(vm) { Owner = owner ?? Application.Current?.MainWindow };
            _activePopup = win;
            try
            {
                win.ShowDialog();
            }
            finally
            {
                if (ReferenceEquals(_activePopup, win)) _activePopup = null;
            }

            if (!string.IsNullOrEmpty(capturedTabId) && openTabAtText != null)
            {
                _ = openTabAtText(capturedTabId, capturedFindText);
            }
        }

        // ---- keyboard ----
        // Window-level PreviewKeyDown: fires regardless of which child has logical focus,
        // and runs before children's KeyDown so the TextBox doesn't eat arrows for caret nav.

        private void OnWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _vm.MoveSelection(+1);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.Up:
                    _vm.MoveSelection(-1);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    _vm.MoveSelection(+10);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    _vm.MoveSelection(-10);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.Home when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    _vm.MoveSelection(-int.MaxValue);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.End when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    _vm.MoveSelection(+int.MaxValue);
                    ScrollSelectedIntoView();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    _vm.ActivateSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    try { DialogResult = false; } catch { }
                    e.Handled = true;
                    break;
            }
        }

        private void ScrollSelectedIntoView()
        {
            if (_vm.Selected != null) ResultList.ScrollIntoView(_vm.Selected);
        }

        // ---- mouse ----

        private void OnResult_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem != null) _vm.ActivateSelected();
        }

        // No Deactivated handler. Modal dialogs don't auto-close on click-away — Esc is the
        // dismiss path. The previous click-away behaviour fought with the focus-race that
        // ShowDialog avoids in the first place.
    }
}
