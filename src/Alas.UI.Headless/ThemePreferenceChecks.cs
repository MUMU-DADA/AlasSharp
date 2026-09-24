using Alas.UI.Theming;

namespace Alas.UI.Headless;

internal static class ThemePreferenceChecks
{
    public static void Verify()
    {
        // This is the shape written by the shipped reflection serializer, including
        // separate light/dark colors. Upgrading must not reset the user's preferences.
        var previous = ThemePreference.FromJson("""
            {"Theme":"extreme","Palette":"custom:saved","ColorMode":"dark","CustomPalettes":[
              {"Id":"custom:saved",
               "Light":{"Accent":"#123456","Secondary":"#654321","AccentHover":"#234567","AccentSoft":"#345678","SecondarySoft":"#456789","OnAccent":"#FFFFFF"},
               "Dark":{"Accent":"#ABCDEF","Secondary":"#FEDCBA","AccentHover":"#BCDEFA","AccentSoft":"#CDEFAB","SecondarySoft":"#DEFABC","OnAccent":"#17202B"}}
            ]}
            """);
        Check(previous.Theme == UiTheme.Extreme && previous.ColorMode == UiColorMode.Dark
              && previous.Palette == "custom:saved" && previous.CustomPalettes.Count == 1,
            "previous PascalCase preferences survive upgrade");
        Check(previous.CustomPalettes[0].Light == new PaletteColors("#123456", "#234567", "#345678", "#654321", "#456789", "#FFFFFF")
              && previous.CustomPalettes[0].Dark == new PaletteColors("#ABCDEF", "#BCDEFA", "#CDEFAB", "#FEDCBA", "#DEFABC", "#17202B"),
            "every saved color survives upgrade");
        var restored = ThemePreference.FromJson(previous.ToJson());
        Check(restored.Theme == previous.Theme && restored.ColorMode == previous.ColorMode
              && restored.Palette == previous.Palette && restored.CustomPalettes.SequenceEqual(previous.CustomPalettes),
            "trim-safe JSON round trip preserves preferences");

        foreach (var json in new[]
        {
            """{"CustomPalettes":[{"Id":"custom:legacy","Primary":"#13579B","Secondary":"#2468AC"}]}""",
            """{"customPalettes":[{"id":"custom:legacy","primary":"#13579B","secondary":"#2468AC"}]}""",
            """{"customPalettes":[{"id":"custom:legacy","light":{"primary":"#13579B","secondary":"#2468AC"}}]}"""
        })
        {
            var legacy = ThemePreference.FromJson(json).CustomPalettes.Single();
            Check(legacy.Light.Accent == "#13579B" && legacy.Light.Secondary == "#2468AC"
                  && legacy.Dark.Accent == "#13579B" && legacy.Dark.Secondary == "#2468AC",
                "legacy brand-color formats preserve both modes");
        }
        foreach (var broken in new[] { "{", "null", "[]", "123" })
            Check(ThemePreference.FromJson(broken) == ThemePreference.Default, "broken preference data uses defaults");
        Check(ThemePreference.FromJson("""{"customPalettes":[null,3,"bad"]}""").CustomPalettes.Count == 0,
            "malformed palette entries are ignored");
        Console.WriteLine("PASS: theme preference upgrade, legacy colors and trim-safe persistence");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
    }
}
