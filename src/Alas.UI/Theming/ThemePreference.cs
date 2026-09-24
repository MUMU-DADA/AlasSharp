using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Alas.UI.Theming;

/// <summary>
/// 界面主题偏好：对应上游同时写入 localStorage 的四个键
/// （azurpilot.theme / azurpilot.palette / azurpilot.color-mode / azurpilot.custom-palettes）。
/// JSON 读写刻意手写（Utf8JsonWriter / JsonDocument，两者都是裁剪安全的），
/// 不使用反射序列化 —— 那会在 WASM 裁剪时触发 IL2026 警告并可能丢类型。
/// </summary>
public sealed record ThemePreference(
    UiTheme Theme,
    string Palette,
    UiColorMode ColorMode,
    IReadOnlyList<UiPalette> CustomPalettes)
{
    public static ThemePreference Default { get; } =
        new(UiTheme.Light, "ocean", UiColorMode.Auto, Array.Empty<UiPalette>());

    /// <summary>把越界输入夹回上游允许的范围：配色 id 与颜色格式非法、超过 32 个都按丢弃处理。</summary>
    public ThemePreference Normalize()
    {
        var custom = new List<UiPalette>();
        foreach (var item in CustomPalettes ?? Array.Empty<UiPalette>())
        {
            if (custom.Count >= UiPalettes.MaxCustom) break;
            if (!UiPalettes.IsValidCustomId(item.Id)) continue;
            if (!IsUsable(item.Light) || !IsUsable(item.Dark)) continue;
            custom.Add(item);
        }
        var palette = Palette ?? "ocean";
        if (!UiPalettes.IsValidCustomId(palette) && UiPalettes.Find(palette, custom).Id != palette) palette = "ocean";
        var mode = Enum.IsDefined(ColorMode) ? ColorMode : UiColorMode.Auto;
        return this with { Palette = palette, ColorMode = mode, CustomPalettes = custom };
    }

    private static bool IsUsable(PaletteColors colors) =>
        UiPalettes.IsValidColor(colors.Accent) && UiPalettes.IsValidColor(colors.Secondary);

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("theme", UiThemes.ToId(Theme));
            writer.WriteString("palette", Palette);
            writer.WriteString("colorMode", ColorMode.ToString().ToLowerInvariant());
            writer.WriteStartArray("customPalettes");
            foreach (var item in CustomPalettes ?? Array.Empty<UiPalette>())
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                WriteColors(writer, "light", item.Light);
                WriteColors(writer, "dark", item.Dark);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteColors(Utf8JsonWriter writer, string name, PaletteColors colors)
    {
        writer.WriteStartObject(name);
        writer.WriteString("accent", colors.Accent);
        writer.WriteString("secondary", colors.Secondary);
        writer.WriteString("accentHover", colors.AccentHover);
        writer.WriteString("accentSoft", colors.AccentSoft);
        writer.WriteString("secondarySoft", colors.SecondarySoft);
        writer.WriteString("onAccent", colors.OnAccent);
        writer.WriteEndObject();
    }

    public static ThemePreference FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default;
            if (!UiThemes.TryParse(ReadString(root, "theme"), out var theme)) theme = UiTheme.Light;
            var mode = ReadString(root, "colorMode") switch
            {
                "light" => UiColorMode.Light,
                "dark" => UiColorMode.Dark,
                _ => UiColorMode.Auto,
            };
            var custom = new List<UiPalette>();
            if (TryReadProperty(root, "customPalettes", out var palettes) && palettes.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in palettes.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = ReadString(item, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    custom.Add(new UiPalette(id, id,
                        ReadColors(item, "light"),
                        ReadColors(item, "dark")));
                }
            }
            return new ThemePreference(theme, ReadString(root, "palette") ?? "ocean", mode, custom).Normalize();
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryReadProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        // Earlier releases wrote PascalCase record properties. Prefer the new
        // canonical key if both forms occur, without reflection-based metadata.
        return element.TryGetProperty(name, out value)
            || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value);
    }

    /// <summary>保留旧 C# 模式颜色及上游 primary/secondary 配色格式。</summary>
    private static PaletteColors ReadColors(JsonElement parent, string name)
    {
        TryReadProperty(parent, name, out var colors);
        TryReadProperty(parent, "light", out var legacyLight);
        // Upstream readCustomPalettes also accepts { light: { primary, secondary } }
        // as one brand-color pair for both modes. Explicit per-mode colors win.
        var primary = ReadString(colors, "primary") ?? ReadString(parent, "primary")
            ?? ReadString(legacyLight, "primary");
        var secondary = ReadString(parent, "secondary") ?? ReadString(legacyLight, "secondary");
        var accent = ReadString(colors, "accent") ?? primary ?? "#245DBE";
        var second = ReadString(colors, "secondary") ?? secondary ?? "#13777C";
        return new PaletteColors(
            accent,
            ReadString(colors, "accentHover") ?? accent,
            ReadString(colors, "accentSoft") ?? accent,
            second,
            ReadString(colors, "secondarySoft") ?? second,
            ReadString(colors, "onAccent") ?? "#FFFFFF");
    }
}
