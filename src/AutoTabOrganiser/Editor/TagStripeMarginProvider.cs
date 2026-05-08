using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using AutoTabOrganiser.Settings;

namespace AutoTabOrganiser.Editor
{
    /// <summary>
    /// MEF provider for <see cref="TagStripeMargin"/>. Attached to the leftmost selection
    /// margin so it sits before the line-number gutter on every text-content editor (which
    /// includes the SSMS SQL editor).
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(TagStripeMargin.MarginName)]
    [Order(Before = PredefinedMarginNames.LeftSelection)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class TagStripeMarginProvider : IWpfTextViewMarginProvider
    {
        // One SettingsStore per provider — Load() caches in-memory after first read so this
        // is cheap. The provider itself is a MEF singleton.
        private readonly SettingsStore _settings =
            new SettingsStore(SettingsStore.DefaultSettingsFilePath());

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            if (wpfTextViewHost == null) return null;
            return new TagStripeMargin(wpfTextViewHost.TextView, _settings);
        }
    }
}
