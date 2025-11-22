using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public static class UrlConverters
{
    /// <summary>
    /// Represents a value converter that extracts the host part from a given URL.
    /// This property is of type IValueConverter and is used to convert an input, which is expected to be a valid URL,
    /// into its host component. If the input is not a valid URL or does not contain a host, it returns null.
    /// </summary>
    public static IValueConverter ToHost { get; } = new FuncValueConverter<object?, string?>(x =>
        x is not Uri uri && !Uri.TryCreate(x?.ToString(), UriKind.Absolute, out uri!) ? null : uri.Host);

    /// <summary>
    /// Represents a value converter that generates the URL for a favicon based on a given URL.
    /// This property is of type IValueConverter and is used to convert an input, which is expected to be a valid URL,
    /// into a URL pointing to the favicon of the website. The favicon URL is constructed by appending "/favicon.ico" to the host part of the input URL.
    /// If the input is not a valid URL or does not contain a host, it returns null.
    /// </summary>
    public static IValueConverter ToFaviconUrl { get; } = new FuncValueConverter<object?, string?>(x => 
        x is not Uri uri && !Uri.TryCreate(x?.ToString(), UriKind.Absolute, out uri!) ? null : $"{uri.Scheme}://{uri.Host}/favicon.ico");
}