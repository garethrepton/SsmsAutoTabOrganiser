using System;

namespace AutoTabOrganiser
{
    internal static class PackageGuids
    {
        public const string AutoTabOrganiserPackageString = "b4f9e3d1-7c2a-4f8e-9b1a-6e3d5c8f7a2b";
        public const string AutoTabOrganiserCmdSetString  = "c5a8d4e2-6b1f-4d9e-8c2a-7d4e6f9b3c1a";

        public static readonly Guid AutoTabOrganiserPackage = new Guid(AutoTabOrganiserPackageString);
        public static readonly Guid AutoTabOrganiserCmdSet  = new Guid(AutoTabOrganiserCmdSetString);
    }

    internal static class PackageIds
    {
        public const int TabHistoryToolsGroup           = 0x1020;
        public const int TabHistoryViewGroup            = 0x1021;
        public const int HelloCommandId                 = 0x0100;
        public const int ShowToolWindowCommandId        = 0x0101;
        public const int SnapshotNowCommandId           = 0x0102;
        public const int ShowThisTabHistoryCommandId    = 0x0103;
        public const int OpenSettingsCommandId          = 0x0104;
        public const int QuickSwitcherCommandId         = 0x0105;
        public const int TagColoursCommandId            = 0x0107;
    }
}
