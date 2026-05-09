using System.Collections.Generic;

namespace AutoTabOrganiser.Metadata
{
    internal sealed class ParsedMetadata
    {
        public string Folder { get; set; }                         // null => Unfiled
        public string Name   { get; set; }                         // may be null
        public string Description { get; set; }                    // raw markdown, may be null
        public string Id     { get; set; }                         // null if not present
        public string Server { get; set; }                         // remembered SSMS connection — server name
        public string Database { get; set; }                       // remembered SSMS connection — database name
        public bool   NoSnapshot { get; set; }
        public List<string> Tags { get; set; } = new List<string>(); // lowercased, no leading '#'
        public int CommentBlockEndExclusive { get; set; }          // char offset (in original text) where the leading comment block ends; 0 if none
    }
}
