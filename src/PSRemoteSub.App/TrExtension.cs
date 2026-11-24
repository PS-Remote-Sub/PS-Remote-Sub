namespace PSRemoteSub.App;

/// <summary>XAML markup extension for UI copy: <c>{app:Tr KeyName}</c> → <see cref="Loc.Get"/>.</summary>
public sealed class TrExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public string ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
