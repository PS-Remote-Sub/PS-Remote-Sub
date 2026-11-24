using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class SpeakerBookTests
{
    private static SpeakerBook Book(FakeTimeProvider time, params (string Name, string? Thai, string? Color)[] known) =>
        new(known.ToDictionary(k => k.Name, k => new SpeakerInfo { Thai = k.Thai, Color = k.Color }), time);

    [Fact]
    public void NamesToTranslate_SkipsKnown_IgnoresCase()
    {
        var book = Book(new FakeTimeProvider(), ("Lucia", "ลูเซีย", null));
        book.Sight(["Jason"]);
        book.Sight(["Jason"]);
        Assert.Equal(["Jason"], book.NamesToTranslate(["LUCIA", "Jason", "jason"]));
    }

    [Fact]
    public void MarkAttempt_HidesForAMinute()
    {
        var time = new FakeTimeProvider();
        var book = Book(time);
        book.Sight(["Jason"]);
        book.Sight(["Jason"]);
        book.MarkAttempt("Jason");
        Assert.Empty(book.NamesToTranslate(["Jason"]));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(["Jason"], book.NamesToTranslate(["Jason"]));
    }

    [Fact]
    public void SetThai_ChangedOnlyWhenDifferent()
    {
        var book = Book(new FakeTimeProvider());
        Assert.True(book.SetThai("Jason", "เจสัน"));
        Assert.False(book.SetThai("JASON", "เจสัน"));
        Assert.Equal("เจสัน", book.Get("jason")!.Thai);
    }

    [Fact]
    public void SetColorIfMissing_FirstOnly()
    {
        var book = Book(new FakeTimeProvider(), ("Lucia", "ลูเซีย", null));
        Assert.True(book.SetColorIfMissing("Lucia", "#E070C0"));
        Assert.False(book.SetColorIfMissing("Lucia", "#FFFFFF"));
        Assert.Equal(("ลูเซีย", "#E070C0"), (book.Get("Lucia")!.Thai, book.Get("Lucia")!.Color));
    }

    [Fact]
    public void Get_ReturnsCopy()
    {
        var book = Book(new FakeTimeProvider(), ("Lucia", "ลูเซีย", null));
        book.Get("Lucia")!.Thai = "x";
        Assert.Equal("ลูเซีย", book.Get("Lucia")!.Thai);
    }

    [Fact]
    public void NewName_TrustedOnlyAfterSeenInTwoSubtitles()
    {
        var book = Book(new FakeTimeProvider());
        book.Sight(["Jason"]);
        Assert.False(book.IsTrusted("Jason"));
        Assert.Empty(book.NamesToTranslate(["Jason"]));
        book.Sight(["JASON"]);
        Assert.True(book.IsTrusted("jason"));
        Assert.Equal(["Jason"], book.NamesToTranslate(["Jason"]));
    }

    [Fact]
    public void KnownName_TrustedAtOnce() =>
        Assert.True(Book(new FakeTimeProvider(), ("Lucia", null, "#E070C0")).IsTrusted("LUCIA"));

    [Fact]
    public void SetColorIfMissing_ReplacesWhitishOnly()
    {
        var book = Book(new FakeTimeProvider(), ("Mattia", null, "#F9FBFF"));
        Assert.True(book.SetColorIfMissing("Mattia", "#E070C0"));
        Assert.False(book.SetColorIfMissing("Mattia", "#00C000"));
        Assert.Equal("#E070C0", book.Get("Mattia")!.Color);
    }

    [Fact]
    public void SetColorIfMissing_SameWhiteAgain_NotAChange()
    {
        var book = Book(new FakeTimeProvider(), ("Kat", null, "#FFFFFF"));
        Assert.False(book.SetColorIfMissing("Kat", "#FFFFFF"));
    }
}
