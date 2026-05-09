using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.UI.Detail
{
    internal partial class DetailPane : UserControl
    {
        public DetailPane() { InitializeComponent(); }

        public void Show(TabSummary t)
        {
            ErrorText.Text = "";
            TitleText.Text = string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name;
            BreadcrumbText.Text = string.IsNullOrEmpty(t.Folder) ? "Unfiled" : t.Folder;

            var tags = string.IsNullOrEmpty(t.TagsCsv)
                ? Array.Empty<string>()
                : t.TagsCsv.Split(',').Select(s => "#" + s).ToArray();
            TagChips.ItemsSource = tags;

            ConnectionText.Text = FormatConnection(t.Server, t.Database);
            LastSnapshotText.Text = "Last snapshot: " +
                DateTimeOffset.FromUnixTimeMilliseconds(t.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

            if (string.IsNullOrEmpty(t.Desc))
            {
                MarkdownHost.Document = new FlowDocument();
            }
            else
            {
                try
                {
                    MarkdownHost.Document = MarkdownFlowDocumentRenderer.Render(t.Desc);
                }
                catch (Exception ex)
                {
                    MarkdownHost.Document = new FlowDocument();
                    ErrorText.Text = "Markdown render failed: " + ex.Message;
                }
            }
        }

        private static string FormatConnection(string server, string database)
        {
            var s = (server ?? "").Trim();
            var d = (database ?? "").Trim();
            if (s.Length == 0 && d.Length == 0) return "";
            if (s.Length == 0) return "Connection: · " + d;
            if (d.Length == 0) return "Connection: " + s;
            return "Connection: " + s + " · " + d;
        }

        public void Clear()
        {
            TitleText.Text = "";
            BreadcrumbText.Text = "";
            TagChips.ItemsSource = null;
            ConnectionText.Text = "";
            LastSnapshotText.Text = "";
            MarkdownHost.Document = new FlowDocument();
            ErrorText.Text = "";
        }

        public void ShowError(string message)
        {
            Clear();
            ErrorText.Text = message;
        }
    }
}
