using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class BoardNoteStyleTests
{
    [Theory]
    [InlineData("yellow", "#F5E099")]
    [InlineData("rose", "#F7C4CD")]
    [InlineData("blue", "#B7DAEB")]
    [InlineData("slate", "#C8CFD5")]
    public void LegacyStylesRemainReadable(string storage, string background)
    {
        var style = BoardNoteStyleCodec.Decode(storage);

        Assert.Equal(15, style.FontSize);
        Assert.Equal(background, style.BackgroundColor);
        Assert.True(style.BackgroundEnabled);
    }

    [Fact]
    public void VersionedStyleRoundTripsNormalizedValues()
    {
        var input = new BoardNoteStyle(
            72,
            1.7,
            BoardNoteTextAlignment.Right,
            "#abcdef",
            false,
            "#123456",
            0.42,
            88,
            37);

        var storage = BoardNoteStyleCodec.Encode(input);
        var restored = BoardNoteStyleCodec.Decode(storage);

        Assert.StartsWith(BoardNoteStyleCodec.Prefix, storage);
        Assert.Equal(input with { TextColor = "#ABCDEF" }, restored);
    }

    [Fact]
    public void DamagedAndUnknownPayloadsFallBackWithoutThrowing()
    {
        Assert.Equal(BoardNoteStyle.Default, BoardNoteStyleCodec.Decode("m10:not-base64"));
        Assert.Equal(BoardNoteStyle.FromLegacy("yellow"), BoardNoteStyleCodec.Decode("future-format"));
    }

    [Fact]
    public void ValuesAreClampedAndInvalidColorsUseDefaults()
    {
        var restored = BoardNoteStyleCodec.Decode(BoardNoteStyleCodec.Encode(new BoardNoteStyle(
            999,
            -2,
            (BoardNoteTextAlignment)999,
            "red",
            true,
            "#xyzxyz",
            4,
            -9,
            500)));

        Assert.Equal(300, restored.FontSize);
        Assert.Equal(1, restored.LineSpacing);
        Assert.Equal(BoardNoteStyle.Default.Alignment, restored.Alignment);
        Assert.Equal(BoardNoteStyle.Default.TextColor, restored.TextColor);
        Assert.Equal(BoardNoteStyle.Default.BackgroundColor, restored.BackgroundColor);
        Assert.Equal(1, restored.BackgroundOpacity);
        Assert.Equal(0, restored.CornerRadius);
        Assert.Equal(200, restored.VerticalPadding);
    }

    [Fact]
    public void PresetsAlwaysNormalizeToExactlyFiveSlots()
    {
        var presets = BoardNotePresetDefaults.Normalize(
        [
            BoardNoteStyle.Default with { FontSize = 400 },
            null!
        ]);

        Assert.Equal(BoardNotePresetDefaults.SlotCount, presets.Count);
        Assert.Equal(300, presets[0].FontSize);
        Assert.Equal(BoardNotePresetDefaults.Create()[1], presets[1]);
    }
}
