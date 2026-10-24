using TrebuchetLib;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;

/// <summary>What the profile of a server save writes into the Engine.ini of the server (checks that need no Windows and no process).</summary>
static class ServerIniChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var setup = new AppSetup(new Config(), GameEdition.Enhanced, false, false);
        string Text(Yuu.Ini.IniDocument document) => document.ToString().Replace(" = ", "=").Replace(" =", "=");

        // Thrall distance: a value in the [SystemSettings] of the engine file of the server, nothing for 0, other lines of the section stay.
        var profile = new ServerProfile();
        var ini = Yuu.Ini.IniParser.Parse("[OnlineSubsystem]\nServerName=Test\n");
        YuuIniServerFiles.ApplyThrallDistance(setup, profile, ini);
        var text = Text(ini);
        Check(ServerProfile.ThrallMaxRelevantDistanceDefault == 0 && profile.ThrallMaxRelevantDistance == 0 && !text.Contains("SystemSettings")
              && !text.Contains("dw.ThrallMaxRelevantDistance") && text.Contains("ServerName=Test"), "Not set (0): nothing is written: " + text.Replace("\n", "|"));
        profile.ThrallMaxRelevantDistance = 1300;
        YuuIniServerFiles.ApplyThrallDistance(setup, profile, ini);
        text = Text(ini);
        Check(text.Contains("[SystemSettings]") && text.Contains("dw.ThrallMaxRelevantDistance=1300") && text.Contains("ServerName=Test"),
            "5 foundations (1300) are written into [SystemSettings]: " + text.Replace("\n", "|"));
        profile.ThrallMaxRelevantDistance = 2600;
        YuuIniServerFiles.ApplyThrallDistance(setup, profile, ini);
        text = Text(ini);
        Check(text.Contains("dw.ThrallMaxRelevantDistance=2600") && !text.Contains("=1300") && text.Split("dw.ThrallMaxRelevantDistance").Length == 2,
            "A new value replaces the old one (one line): " + text.Replace("\n", "|"));
        profile.ThrallMaxRelevantDistance = 5000000;
        YuuIniServerFiles.ApplyThrallDistance(setup, profile, ini);
        Check(Text(ini).Contains("dw.ThrallMaxRelevantDistance=100000"), "A value over the limit is cut to it");
        profile.ThrallMaxRelevantDistance = -5;
        YuuIniServerFiles.ApplyThrallDistance(setup, profile, ini);
        text = Text(ini);
        Check(!text.Contains("dw.ThrallMaxRelevantDistance") && !text.Contains("SystemSettings") && text.Contains("ServerName=Test"),
            "Back to not set (or nonsense): the line goes, and the empty section with it: " + text.Replace("\n", "|"));
        var own = Yuu.Ini.IniParser.Parse("[SystemSettings]\nnet.MaxRepArraySize=65535\ndw.ThrallMaxRelevantDistance=900\n");
        YuuIniServerFiles.ApplyThrallDistance(setup, new ServerProfile(), own);
        text = Text(own);
        Check(text.Contains("net.MaxRepArraySize=65535") && !text.Contains("dw.ThrallMaxRelevantDistance") && text.Contains("[SystemSettings]"),
            "Other lines of the section stay when the setting is switched off: " + text.Replace("\n", "|"));
        var other = Yuu.Ini.IniParser.Parse("[SystemSettings]\nnet.MaxRepArraySize=65535\n");
        YuuIniServerFiles.ApplyThrallDistance(setup, new ServerProfile { ThrallMaxRelevantDistance = 1300 }, other);
        text = Text(other);
        Check(text.Contains("net.MaxRepArraySize=65535") && text.Contains("dw.ThrallMaxRelevantDistance=1300") && text.Split("[SystemSettings]").Length == 2,
            "It joins the section that is already there: " + text.Replace("\n", "|"));
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<ServerProfile>(System.Text.Json.JsonSerializer.Serialize(new ServerProfile { ThrallMaxRelevantDistance = 1300 }));
        Check(reloaded?.ThrallMaxRelevantDistance == 1300 && System.Text.Json.JsonSerializer.Deserialize<ServerProfile>("{}")?.ThrallMaxRelevantDistance == 0,
            "The setting is saved with the profile, and a profile of before has it off");
        // It is one of the settings that are written on every start of the server.
        Check(typeof(YuuIniServerFiles).GetMethod(nameof(YuuIniServerFiles.ApplyThrallDistance))!
                  .GetCustomAttributes(typeof(IniSettingAttribute), true).Cast<IniSettingAttribute>().Any(x => x.Path.EndsWith("Engine.ini", StringComparison.Ordinal) && x.Path.Contains("WindowsServer", StringComparison.Ordinal)),
            "It goes into the Engine.ini of the server (WindowsServer), not the one of the client");
        return count;
    }
}
