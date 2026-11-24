using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class CharacterIndexTests
{
    internal static GameCharacter Ch(string en, params string[] aliases) =>
        new(en, aliases, null, null, null, null, null, null);

    internal static CharacterIndex Sample() => new(new GameInfo(null, null,
        [Ch("Lucia", "Lu"), Ch("Jason Duval", "Jason", "Jay"), Ch("Cal Hampton"), Ch("Boobie Ike"), Ch("Raul Hernandez"), Ch("Raul Silva")],
        [new GamePlace("Vice City", "ไวซ์ซิตี้", null), new GamePlace("Leonida Keys", null, null)]));

    [Fact]
    public void Resolve_ByAliasAndWord()
    {
        var idx = Sample();
        Assert.Equal("Lucia", idx.Resolve("Lu")!.En);
        Assert.Equal("Jason Duval", idx.Resolve("Jason Duval")!.En);
        Assert.Equal("Jason Duval", idx.Resolve("Duval")!.En);
        Assert.Equal("Lucia", idx.Resolve("LUCIA (on phone)")!.En);
        Assert.Null(idx.Resolve("Nobody"));
        Assert.Null(idx.Resolve(null));
        Assert.Null(idx.Resolve("Raul"));      // ambiguous word
        Assert.Equal("Cal Hampton", idx.Resolve("Hampton")!.En);
    }

    [Fact]
    public void MentionedIn_WholeWordOnly()
    {
        var idx = Sample();
        Assert.Contains(idx.MentionedIn("Hey, Lu! Come here"), c => c.En == "Lucia");
        Assert.Empty(idx.MentionedIn("Lucky luminous day"));
        Assert.Contains(idx.MentionedIn("Tell Jay."), c => c.En == "Jason Duval");
    }

    [Fact]
    public void PlacesIn_FindsPlaces()
    {
        var idx = Sample();
        Assert.Single(idx.PlacesIn("Welcome to vice city, baby"));
        Assert.Empty(idx.PlacesIn("a nice city"));
    }
}
