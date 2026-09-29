using System.Collections.Generic;
using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace SaveOpt
{
    internal enum GcModeType
    {
        [Option("STRINGS.BETTERSAVE.OPTIONS.GCMODE.MANUAL")]
        Manual = 0,
        [Option("STRINGS.BETTERSAVE.OPTIONS.GCMODE.AUTO")]
        Auto = 1
    }

    [ModInfo(null, null, false)]
    [ConfigFile("config.json", true, false)]
    [JsonObject(MemberSerialization.OptOut)]
    internal sealed class BetterSaveOptions : IOptions
    {
        [RestartRequired]
        [Option("STRINGS.BETTERSAVE.OPTIONS.GCMODE.NAME", "STRINGS.BETTERSAVE.OPTIONS.GCMODE.TOOLTIP")]
        [JsonProperty]
        public GcModeType GcMode { get; set; } = GcModeType.Manual;

        [Option("STRINGS.BETTERSAVE.OPTIONS.GCRELEASEMINUTES.NAME", "STRINGS.BETTERSAVE.OPTIONS.GCRELEASEMINUTES.TOOLTIP")]
        [JsonProperty]
        public int GcReleaseMinutes { get; set; } = 10;

        [Option("STRINGS.BETTERSAVE.OPTIONS.COLLECTAFTERSAVE.NAME", "STRINGS.BETTERSAVE.OPTIONS.COLLECTAFTERSAVE.TOOLTIP")]
        [JsonProperty]
        public bool CollectAfterSave { get; set; }

        [Option("STRINGS.BETTERSAVE.OPTIONS.REALSCREENSHOT.NAME", "STRINGS.BETTERSAVE.OPTIONS.REALSCREENSHOT.TOOLTIP")]
        [JsonProperty]
        public bool RealScreenshot { get; set; }

        [Option("STRINGS.BETTERSAVE.OPTIONS.VERBOSE.NAME", "STRINGS.BETTERSAVE.OPTIONS.VERBOSE.TOOLTIP")]
        [JsonProperty]
        public bool Verbose { get; set; }

        public void OnOptionsChanged()
        {
            ModOptions.ApplyFromOptions(this);
        }

        public IEnumerable<IOptionsEntry> CreateOptions()
        {
            return null;
        }
    }
}
