using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AutoTabOrganiser.Tree;

namespace AutoTabOrganiser.UI
{
    /// <summary>
    /// Attached behaviour that renders a TextBlock's text with the parts matching a set of
    /// search terms in bold — substring hits as one run, fuzzy (subsequence) hits per-char.
    /// Only the weight changes, so the text keeps whatever foreground the row state
    /// (selected/unselected) gives it.
    /// </summary>
    internal static class HighlightedText
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
            "Terms", typeof(IList<string>), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

        public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

        public static void SetTerms(DependencyObject d, IList<string> value) => d.SetValue(TermsProperty, value);
        public static IList<string> GetTerms(DependencyObject d) => (IList<string>)d.GetValue(TermsProperty);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock tb)) return;
            var text = GetText(tb) ?? "";
            var runs = QuickSwitchRanker.MatchRuns(text, GetTerms(tb));

            tb.Inlines.Clear();
            if (runs.Count == 0)
            {
                tb.Text = text;
                return;
            }

            int pos = 0;
            foreach (var run in runs)
            {
                if (run.Key > pos)
                    tb.Inlines.Add(new Run(text.Substring(pos, run.Key - pos)));
                tb.Inlines.Add(new Run(text.Substring(run.Key, run.Value)) { FontWeight = FontWeights.Bold });
                pos = run.Key + run.Value;
            }
            if (pos < text.Length)
                tb.Inlines.Add(new Run(text.Substring(pos)));
        }
    }
}
