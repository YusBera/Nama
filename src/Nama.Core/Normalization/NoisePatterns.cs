using System.Text.RegularExpressions;

namespace Nama.Core.Normalization;

/// <summary>
/// The vocabulary of junk that shows up in local game folder and executable names.
/// Kept separate from <see cref="NameNormalizer"/> so it can be extended without
/// touching the normalization algorithm.
/// </summary>
public static partial class NoisePatterns
{
    /// <summary>
    /// Repackers and scene groups. Matched as whole tokens only, so a group name that
    /// happens to be a real word cannot eat part of a legitimate title.
    /// </summary>
    public static readonly IReadOnlySet<string> ReleaseGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "fitgirl", "dodi", "elamigos", "codex", "cpy", "plaza", "skidrow", "reloaded",
        "hoodlum", "razor1911", "razor", "prophet", "tinyiso", "darksiders", "goldberg",
        "empress", "rune", "steampunks", "hi2u", "tenoke", "rvtfix", "flt", "fairlight",
        "3dm", "ali213", "creamapi", "onlinefix", "online-fix", "masquerade", "kaos",
        "chronos", "gog", "gnarly", "unleashed", "simplex", "mkdev", "seyter", "johncena141",
        "xatab", "r.g", "rg", "mechanics", "catalyst", "freegogpcgames", "igg", "igggames",
        "steamrip", "gamesforyou", "pcgamestorrents", "ocean", "oceanofgames", "repack",
        "repacks", "nosteam", "nosurvey", "anadius", "sksapp", "0xdeadc0de", "i_kaos",
        "vrex", "gollum", "vace", "tenoke", "rukia", "hanabi", "sunshine", "insaneramzes",
        "qoob", "decepticon", "blackbox", "corepack", "audioslave", "generation",
    };

    /// <summary>
    /// Words that describe the package rather than the game. Removed only when they are
    /// standalone tokens and never when they are the entire remaining title.
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "repack", "crack", "cracked", "cracks", "patch", "patched", "keygen", "trainer",
        "setup", "installer", "install", "uninstall", "unins000", "launcher", "start",
        "play", "run", "game", "games", "portable", "standalone", "multi", "multilang",
        "multilanguage", "english", "eng", "en", "jpn", "jap", "japanese", "jp", "ja",
        "kr", "zh", "chs", "cht", "chinese",
        "russian", "rus", "korean", "kor", "sub", "subbed", "subs", "translated", "translation",
        "uncensored", "censored", "decensored", "patch18", "r18", "18", "adult",
        "full", "complete", "goty", "definitive", "remastered", "remaster", "hd", "sd",
        "proper", "readnfo", "nfo", "iso", "rip", "torrent", "dlc", "dlcs", "alldlc",
        "incl", "including", "included", "bonus", "soundtrack", "ost", "artbook",
        "preinstalled", "pre", "installed", "cracked", "fix", "fixed", "update", "updates",
        "final", "release", "retail", "steam", "gog", "epic", "drmfree", "drm", "free",
        "win", "windows", "pc", "x64", "x86", "win32", "win64", "64bit", "32bit", "amd64",
        "beta", "alpha", "demo", "trial", "test", "debug", "shipping",
    };

    /// <summary>
    /// Noise words that are also perfectly ordinary title words. These are kept in the
    /// canonical name and only removed to build an alternate search candidate, because
    /// stripping them outright destroys real titles:
    /// <c>There Is No Game</c>, <c>The Game</c>, <c>Devil May Cry HD Collection</c>,
    /// <c>Horizon Forbidden West Complete Edition</c>.
    ///
    /// Words that only ever describe the package — <c>repack</c>, <c>final</c>,
    /// <c>x64</c>, language tags — deliberately stay out of this set.
    /// </summary>
    public static readonly IReadOnlySet<string> TitlePlausibleWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "game", "games", "complete", "definitive", "remastered", "remaster", "hd", "full",
    };

    /// <summary>
    /// Executable base names that carry no title information. When the exe matches one of
    /// these the folder name is used instead.
    /// </summary>
    public static readonly IReadOnlySet<string> GenericExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "game", "start", "launch", "launcher", "play", "run", "main", "app", "client",
        "setup", "install", "installer", "unins000", "uninstall", "config", "settings",
        "bootstrap", "loader", "steam", "player", "engine", "binary", "bin", "win64",
        "win32", "x64", "x86", "release", "shipping", "editor", "server", "crashhandler",
        "crashreporter", "unitycrashhandler64", "unitycrashhandler32", "vcredist",
        "dxsetup", "directx", "dotnetfx", "readme", "manual", "credits", "nw", "electron",
    };

    /// <summary>
    /// Suffixes Unreal/Unity builds append to the real title, e.g. <c>EldenRing-Win64-Shipping</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> EngineSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "win64", "win32", "winnoeditor", "shipping", "development", "debug", "test",
        "server", "client", "editor", "eac", "be", "battleye", "launcher", "dx11", "dx12",
        "vulkan", "opengl", "steam", "epic", "egs",
    };

    /// <summary>
    /// Abbreviations and shorthand that appear in executable names. Expanded before
    /// searching so <c>SubaHibiEN.exe</c> can find its game.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Abbreviations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["subahibi"] = "Subarashiki Hibi",
            ["sakuutsu"] = "Subarashiki Hibi",
            ["muvluv"] = "Muv-Luv",
            ["mlalt"] = "Muv-Luv Alternative",
            ["fsn"] = "Fate/stay night",
            ["fhn"] = "Fate/hollow ataraxia",
            ["fgo"] = "Fate/Grand Order",
            ["ddlc"] = "Doki Doki Literature Club",
            ["wa2"] = "White Album 2",
            ["g-senjou"] = "G-senjou no Maou",
            ["gsen"] = "G-senjou no Maou",
            ["chaoshead"] = "Chaos;Head",
            ["chaoschild"] = "Chaos;Child",
            ["steinsgate"] = "Steins;Gate",
            ["sg0"] = "Steins;Gate 0",
            ["robotics"] = "Robotics;Notes",
            ["higurashi"] = "Higurashi When They Cry",
            ["umineko"] = "Umineko When They Cry",
            ["clannad"] = "Clannad",
            ["kanon"] = "Kanon",
            ["tsukihime"] = "Tsukihime",
            ["katawa"] = "Katawa Shoujo",
            ["ks"] = "Katawa Shoujo",
            ["eldenring"] = "Elden Ring",
            ["ds3"] = "Dark Souls III",
            ["ds2"] = "Dark Souls II",
            ["ds1"] = "Dark Souls",
            ["botw"] = "The Legend of Zelda Breath of the Wild",
            ["totk"] = "The Legend of Zelda Tears of the Kingdom",
            ["gta"] = "Grand Theft Auto",
            ["gtav"] = "Grand Theft Auto V",
            ["rdr2"] = "Red Dead Redemption 2",
            ["cs2"] = "Counter-Strike 2",
            ["csgo"] = "Counter-Strike Global Offensive",
            ["tf2"] = "Team Fortress 2",
            ["l4d2"] = "Left 4 Dead 2",
            ["hl2"] = "Half-Life 2",
            ["nier"] = "NieR",
            ["ffxiv"] = "Final Fantasy XIV",
            ["ffvii"] = "Final Fantasy VII",
            ["re4"] = "Resident Evil 4",
            ["re2"] = "Resident Evil 2",
            ["mgs"] = "Metal Gear Solid",
            ["mgsv"] = "Metal Gear Solid V",
            ["ac6"] = "Armored Core VI",
            ["p5r"] = "Persona 5 Royal",
            ["p4g"] = "Persona 4 Golden",
            ["smt"] = "Shin Megami Tensei",
            ["botc"] = "Blood on the Clocktower",
            ["kcd"] = "Kingdom Come Deliverance",
            ["hkdemonsouls"] = "Demon's Souls",
            ["sekiro"] = "Sekiro Shadows Die Twice",
            ["hzd"] = "Horizon Zero Dawn",
            ["tw3"] = "The Witcher 3 Wild Hunt",
            ["cp2077"] = "Cyberpunk 2077",
            ["botf"] = "Battle of the Fleet",
        };

    /// <summary>
    /// Version strings: <c>v1.12.2</c>, <c>1.0.4.7</c>, <c>build 12345</c>, <c>r1234</c>.
    ///
    /// A bare <c>v&lt;number&gt;</c> only counts as a version when it carries dot-separated
    /// parts or sits at the very end. Without that restriction it swallows real title
    /// words: <c>Danganronpa V3 Killing Harmony</c> would lose its V3.
    /// </summary>
    [GeneratedRegex(@"\b(?:v(?:er(?:sion)?)?[\s._-]?\d+(?:[._]\d+)+[a-z]?|v(?:er(?:sion)?)?[\s._-]?\d+[a-z]?(?=\s*$)|\d+\.\d+(?:\.\d+){0,3}[a-z]?|build[\s._-]?\d+|r\d{3,}|rev[\s._-]?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex Version { get; }

    /// <summary>Bracketed segments: <c>[FitGirl]</c>, <c>(2022)</c>, <c>{MULTi9}</c>.</summary>
    [GeneratedRegex(@"[\[\({][^\]\)}]*[\]\)}]", RegexOptions.CultureInvariant)]
    public static partial Regex Bracketed { get; }

    /// <summary>Trailing four-digit release year, only when it is clearly a suffix.</summary>
    [GeneratedRegex(@"\s\(?(?:19[7-9]\d|20[0-4]\d)\)?$", RegexOptions.CultureInvariant)]
    public static partial Regex TrailingYear { get; }

    /// <summary>Update/DLC counters like <c>Update 5</c>, <c>+12 DLC</c>, <c>Hotfix 3</c>.</summary>
    [GeneratedRegex(@"\b(?:\+\s*\d+\s*dlcs?|update[\s._-]?\d*|hotfix[\s._-]?\d*|patch[\s._-]?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex UpdateCounter { get; }

    /// <summary>Separators that stand in for spaces in file names.</summary>
    [GeneratedRegex(@"[_\.\-\+~]+", RegexOptions.CultureInvariant)]
    public static partial Regex Separators { get; }

    /// <summary>
    /// Separators excluding the dot. These are collapsed before version stripping, because
    /// <c>_</c> is a word character and would otherwise hide the word boundary in
    /// <c>Hades_v1.38290</c>. Dots are kept until after versions have been removed.
    /// </summary>
    [GeneratedRegex(@"[_\-\+~]+", RegexOptions.CultureInvariant)]
    public static partial Regex WordSeparators { get; }

    /// <summary>Runs of whitespace.</summary>
    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    public static partial Regex ExcessWhitespace { get; }

    /// <summary>
    /// camelCase / PascalCase boundaries, used to split <c>EldenRing</c> into <c>Elden Ring</c>.
    ///
    /// The first alternative requires the uppercase letter to be followed by another
    /// non-space character, which protects stylized names that end in a capital:
    /// <c>NieR</c> stays intact while <c>EldenRing</c> and <c>HibiEN</c> still split.
    /// The second handles acronym runs, so <c>FFXIVLauncher</c> becomes <c>FFXIV Launcher</c>.
    /// The third separates a trailing sequel number, as in <c>DarkSouls3</c>.
    /// </summary>
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z](?=[^\s]))|(?<=[A-Z]{2})(?=[A-Z][a-z])|(?<=[a-z])(?=\d)",
        RegexOptions.CultureInvariant)]
    public static partial Regex CamelBoundary { get; }

    /// <summary>Any CJK codepoint, used to decide whether a title should keep its original form.</summary>
    [GeneratedRegex("[぀-ヿ㐀-䶿一-鿿豈-﫿ｦ-ﾟ]", RegexOptions.CultureInvariant)]
    public static partial Regex Cjk { get; }

    /// <summary>Punctuation that carries no meaning once separators are normalized.</summary>
    [GeneratedRegex(@"[""'`´’“”«»|/\\*?<>:#!,]", RegexOptions.CultureInvariant)]
    public static partial Regex StrippablePunctuation { get; }
}
