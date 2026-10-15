// SessionRules.cs
// The gameplay rules a multiplayer session decides for everyone (NetRules, GoF2.Net): their bits, here so the options
// (Settings) can ask for the session's value through GameHooks.SessionRule.

namespace GoF2Remake.Data
{
    public static class SessionRules
    {
        public const int CloakLosesPursuers = 1, PirateEvents = 2, CapitalShips = 4, InformerOriginalRule = 8,
                         KaamoStacking = 16, KaamoKeepsEquipment = 32, CloakBay = 64, EmpBreaksCloak = 128;

        /// <summary>Remake: the highest level of one Kaamo Club upgrade on a ship while stacking is on (Settings.KaamoMaxLevel,
        /// Session.MaxModLevel); a number, so the session carries it beside the bits (NetRules.HostKaamoLevel). 10 is the
        /// players' feedback of 2026-10 (45 equipment slots on a Cronus uncapped); 1 would be stacking off.</summary>
        public const int DefaultKaamoLevel = 10, MinKaamoLevel = 2, MaxKaamoLevel = 20;

        public static int ClampKaamoLevel(int level) => level < MinKaamoLevel ? MinKaamoLevel : level > MaxKaamoLevel ? MaxKaamoLevel : level;
    }
}
