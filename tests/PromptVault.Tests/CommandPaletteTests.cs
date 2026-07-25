using PromptVault.App;

namespace PromptVault.Tests;

public sealed class CommandPaletteTests
{
    private static readonly CommandPaletteItem[] Commands =
    [
        new("layout.waterfall", "布局：瀑布流", "切换图片排列", "waterfall layout", "布局"),
        new("filter.favorite", "筛选：收藏", "只看已收藏图片", "favorite star", "筛选"),
        new("category.1", "分类：人物", "打开人物分类", "portrait people", "分类"),
        new("future.aesthetic", "AI 审美属性", "M4 分析后启用", "aesthetic ai", "未来入口")
    ];

    [Fact]
    public void EmptyQueryPreservesCatalogOrder()
    {
        Assert.Equal(Commands, CommandPaletteSearch.Filter(Commands, ""));
    }

    [Theory]
    [InlineData("收藏", "filter.favorite")]
    [InlineData("FAVORITE", "filter.favorite")]
    [InlineData("分类 人物", "category.1")]
    [InlineData("AI aesthetic", "future.aesthetic")]
    public void QueryMatchesChineseEnglishAndMultipleTerms(string query, string expectedId)
    {
        Assert.Equal(expectedId, Assert.Single(CommandPaletteSearch.Filter(Commands, query)).Id);
    }
}
