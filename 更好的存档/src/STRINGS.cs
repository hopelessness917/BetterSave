namespace SaveOpt
{
    public static class STRINGS
    {
        public static class BETTERSAVE
        {
            public static class OPTIONS
            {
                public static class GCMODE
                {
                    public static LocString NAME = "1. GC Mode";
                    public static LocString TOOLTIP = "Manual: this mod holds off managed collection and releases it on the timer set below (recommended).\nAuto: no intervention at all - the game manages GC by itself. Lower heap peak, but collections happen more often and stutter more; suited to low-memory machines.\nOptions 2 and 3 have no effect when Auto is selected. Changing this requires a game restart.";
                    public static LocString MANUAL = "Manual (mod-managed, recommended)";
                    public static LocString AUTO = "Auto (game-managed)";
                }

                public static class GCRELEASEMINUTES
                {
                    public static LocString NAME = "2. GC Release Interval (minutes)";
                    public static LocString TOOLTIP = "1-20 minutes. Shorter: lower heap and smaller pauses, but more of them.\nLonger: fewer pauses, but each one is larger. No effect when Auto is selected.";
                }

                public static class COLLECTAFTERSAVE
                {
                    public static LocString NAME = "3. Collect GC After Saving";
                    public static LocString TOOLTIP = "Off: skip the game's own GC.Collect() at the end of every save, which keeps the save window short (recommended).\nOn: allow it - a cleaner heap, but one extra pause after each save. No effect when Auto is selected.";
                }

                public static class REALSCREENSHOT
                {
                    public static LocString NAME = "4. Real Screenshot";
                    public static LocString TOOLTIP = "On: re-render the preview image on every save, so it is always up to date (about 0.45 s each).\nOff: render once on the first save of the session and copy it for the rest (recommended default).";
                }

                public static class VERBOSE
                {
                    public static LocString NAME = "5. Diagnostic Log";
                    public static LocString TOOLTIP = "For developer use.";
                }
            }
        }
    }
}
