using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoTabOrganiser.Settings;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.UI.ViewModels;
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
                                Func<string, string, Task> openTabAtText, Window owner,
                                string currentTabId = null)
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

            var vm = new QuickSwitcherViewModel(store, settings, log, capture, currentTabId);
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
                case Key.Tab:
                    // Accept the top tag suggestion. Swallow Tab either way — the search box
                    // is the only focusable control, so focus-cycling is meaningless here.
                    if (_vm.AcceptFirstTagSuggestion()) FocusSearchBoxAtEnd();
                    e.Handled = true;
                    break;
                default:
                    // Ctrl+1..9 jumps straight to the Nth visible row (top row and NumPad both).
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        int n = DigitFromKey(e.Key);
                        if (n >= 1) { _vm.ActivateIndex(n - 1); e.Handled = true; }
                    }
                    break;
            }
        }

        private static int DigitFromKey(Key key)
        {
            if (key >= Key.D1 && key <= Key.D9) return key - Key.D0;
            if (key >= Key.NumPad1 && key <= Key.NumPad9) return key - Key.NumPad0;
            return 0;
        }

        private void ScrollSelectedIntoView()
        {
            if (_vm.Selected != null) ResultList.ScrollIntoView(_vm.Selected);
        }

        /// <summary>Programmatic SearchText changes reset the TextBox caret to 0; put it back
        /// at the end so the user can keep typing.</summary>
        private void FocusSearchBoxAtEnd()
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        }

        // ---- mouse ----

        private void OnResult_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem != null) _vm.ActivateSelected();
        }

        private void OnTagSuggestion_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TagSuggestion s)
            {
                _vm.AcceptTagSuggestion(s);
                FocusSearchBoxAtEnd();
                e.Handled = true;
            }
        }

        private void OnTagChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TagChip chip)
            {
                _vm.AddTagFilter(chip.Text);
                FocusSearchBoxAtEnd();
                e.Handled = true;
            }
        }

        // No Deactivated handler. Modal dialogs don't auto-close on click-away — Esc is the
        // dismiss path. The previous click-away behaviour fought with the focus-race that
        // ShowDialog avoids in the first place.
    }
}
