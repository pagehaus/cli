namespace Pagehaus.Cli.Tests;

public sealed class OptionsTests
{
    private static Options Parse(params string[] args) => new(args, flags: ["--open"], values: ["--port"]);

    [Fact]
    public void A_folder_flags_and_values_in_any_order()
    {
        var o = Parse("--port", "9000", "site", "--open");
        Assert.Equal("site", o.Folder);
        Assert.Equal(9000, o.Int("--port"));
        Assert.True(o.Has("--open"));
        Assert.Equal(9001, Parse("--port=9001").Int("--port"));
        Assert.Equal(".", Parse().Folder);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("a", "b")]
    [InlineData("--port")]
    public void Mistakes_are_explained(params string[] args) => Assert.Throws<CliException>(() => Parse(args));

    [Fact]
    public void Numbers_must_be_numbers() => Assert.Throws<CliException>(() => Parse("--port", "eighty").Int("--port"));
}
