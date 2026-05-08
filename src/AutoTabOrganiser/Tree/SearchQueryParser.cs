using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AutoTabOrganiser.Tree
{
    internal sealed class SearchTerm
    {
        public string Field { get; set; } // null | "name" | "tag" | "folder" | "desc" | "since" | "content"
        public string Value { get; set; }
        public bool Negate { get; set; }
    }

    internal sealed class SearchQuery
    {
        public List<SearchTerm> Terms { get; } = new List<SearchTerm>();

        public bool IsEmpty => Terms.Count == 0;
    }

    internal static class SearchQueryParser
    {
        public static SearchQuery Parse(string input)
        {
            var q = new SearchQuery();
            if (string.IsNullOrWhiteSpace(input)) return q;

            foreach (var raw in Tokenise(input))
            {
                var token = raw;
                bool negate = false;
                if (token.StartsWith("-") && token.Length > 1) { negate = true; token = token.Substring(1); }

                if (token.StartsWith("#") && token.Length > 1)
                {
                    q.Terms.Add(new SearchTerm { Field = "tag", Value = token.Substring(1).ToLowerInvariant(), Negate = negate });
                    continue;
                }

                var colon = token.IndexOf(':');
                if (colon > 0)
                {
                    var field = token.Substring(0, colon).ToLowerInvariant();
                    var value = token.Substring(colon + 1);
                    if (field == "tag") value = value.ToLowerInvariant();
                    q.Terms.Add(new SearchTerm { Field = field, Value = value, Negate = negate });
                    continue;
                }

                q.Terms.Add(new SearchTerm { Field = null, Value = token, Negate = negate });
            }
            return q;
        }

        public static (string whereSql, List<KeyValuePair<string, object>> parameters) ToSql(SearchQuery q, long nowMs)
        {
            var clauses = new List<string>();
            var pars = new List<KeyValuePair<string, object>>();
            int idx = 0;

            foreach (var t in q.Terms)
            {
                idx++;
                var pname = "$p" + idx;
                string clause = null;

                switch (t.Field)
                {
                    case "tag":
                        clause = "EXISTS (SELECT 1 FROM snapshot_tags st WHERE st.snapshot_id = tabs_latest.latest_snapshot_id AND st.tag = " + pname + ")";
                        pars.Add(new KeyValuePair<string, object>(pname, t.Value));
                        break;
                    case "name":
                        clause = "(name LIKE " + pname + " COLLATE NOCASE)";
                        pars.Add(new KeyValuePair<string, object>(pname, "%" + Like(t.Value) + "%"));
                        break;
                    case "folder":
                        clause = "(folder LIKE " + pname + " COLLATE NOCASE)";
                        pars.Add(new KeyValuePair<string, object>(pname, "%" + Like(t.Value) + "%"));
                        break;
                    case "desc":
                        clause = "(IFNULL(desc,'') LIKE " + pname + " COLLATE NOCASE)";
                        pars.Add(new KeyValuePair<string, object>(pname, "%" + Like(t.Value) + "%"));
                        break;
                    case "since":
                        var minMs = nowMs - ParseDurationMs(t.Value);
                        clause = "ts >= " + pname;
                        pars.Add(new KeyValuePair<string, object>(pname, minMs));
                        break;
                    case "content":
                        // User opts in explicitly; passes the value straight to FTS5 MATCH
                        // so FTS5 query syntax (phrases, AND/OR, prefix*) works as expected.
                        clause = "tabs_latest.tab_id IN (SELECT tab_id FROM tab_content_fts WHERE content MATCH " + pname + ")";
                        pars.Add(new KeyValuePair<string, object>(pname, t.Value));
                        break;
                    default:
                        var pn2 = "$p" + (++idx);
                        var pn3 = "$p" + (++idx);
                        clause =
                            "(name LIKE " + pname + " COLLATE NOCASE OR folder LIKE " + pn2 + " COLLATE NOCASE OR EXISTS (SELECT 1 FROM snapshot_tags st WHERE st.snapshot_id = tabs_latest.latest_snapshot_id AND st.tag LIKE " + pn3 + " COLLATE NOCASE))";
                        var pat = "%" + Like(t.Value) + "%";
                        pars.Add(new KeyValuePair<string, object>(pname, pat));
                        pars.Add(new KeyValuePair<string, object>(pn2, pat));
                        pars.Add(new KeyValuePair<string, object>(pn3, pat));
                        break;
                }

                if (t.Negate) clause = "NOT (" + clause + ")";
                clauses.Add(clause);
            }

            return (string.Join(" AND ", clauses), pars);
        }

        private static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private static long ParseDurationMs(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            char unit = v[v.Length - 1];
            string nstr = char.IsLetter(unit) ? v.Substring(0, v.Length - 1) : v;
            if (!double.TryParse(nstr, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
            switch (char.IsLetter(unit) ? char.ToLowerInvariant(unit) : 'd')
            {
                case 'm': return (long)(n * 60_000);
                case 'h': return (long)(n * 3_600_000);
                case 'w': return (long)(n * 7 * 86_400_000);
                default:  return (long)(n * 86_400_000);
            }
        }

        private static IEnumerable<string> Tokenise(string input)
        {
            var sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }
}
