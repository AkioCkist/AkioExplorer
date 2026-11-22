using System.IO.Enumeration;

namespace AkioExplorer.Core.Models;

/// <summary>Kind of item, the "Type" filter (Google Drive style).</summary>
public enum FileCategory
{
    Any,
    Folder,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Image,
    Video,
    Audio,
    Archive,
    Code,

    /// <summary>Photos and videos together (MEGA's "Media" view).</summary>
    Media,
}

public enum DateFilter
{
    Any,
    Today,
    Last7Days,
    Last30Days,
    ThisYear,
    LastYear,

    /// <summary>Between <see cref="SearchQuery.ModifiedFrom"/> and <see cref="SearchQuery.ModifiedTo"/> (whole days, both inclusive).</summary>
    Custom,
}

public enum SizeFilter
{
    Any,
    Under1MB,
    From1To100MB,
    From100MBTo1GB,
    Over1GB,

    /// <summary>Between <see cref="SearchQuery.MinSize"/> and <see cref="SearchQuery.MaxSize"/> bytes (either may be null).</summary>
    Custom,
}

public enum SearchScope
{
    /// <summary>The current folder and everything below it.</summary>
    CurrentFolder,

    /// <summary>The whole provider (all fixed drives for This PC, the whole account for a cloud).</summary>
    Everywhere,

    /// <summary>Items other people shared with the user (<see cref="StorageCapabilities.SearchSharing"/>).</summary>
    SharedWithMe,
}

/// <summary>"Owner" filter (<see cref="StorageCapabilities.SearchSharing"/>).</summary>
public enum OwnerFilter
{
    Anyone,
    Me,
    NotMe,

    /// <summary>Owned by <see cref="SearchQuery.OwnerEmail"/>.</summary>
    Person,
}

/// <summary>
/// What a search looks for. <see cref="Name"/> and the filters are checked by <see cref="SearchMatcher"/>;
/// <see cref="Content"/> is only understood by providers with <see cref="StorageCapabilities.ContentSearch"/>.
/// Providers push what they can to their server and check the rest with the matcher before yielding.
/// </summary>
public sealed record SearchQuery
{
    public static SearchQuery Empty { get; } = new();

    /// <summary>Part of the item name (case-insensitive), or a wildcard pattern when it contains * or ?.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Words inside the file ("Has the words").</summary>
    public string Content { get; init; } = string.Empty;

    public FileCategory Category { get; init; }

    public DateFilter Modified { get; init; }

    public DateTime? ModifiedFrom { get; init; }

    public DateTime? ModifiedTo { get; init; }

    public SizeFilter Size { get; init; }

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public SearchScope Scope { get; init; }

    public OwnerFilter Owner { get; init; }

    public string OwnerEmail { get; init; } = string.Empty;

    /// <summary>Email of someone the item is shared with ("Shared with").</summary>
    public string SharedWith { get; init; } = string.Empty;

    /// <summary>Look in the trash instead of the live files (<see cref="StorageCapabilities.SearchTrash"/>).</summary>
    public bool InTrash { get; init; }

    /// <summary>Only starred items (<see cref="StorageCapabilities.SearchStarred"/>).</summary>
    public bool Starred { get; init; }

    public string NameText => Name.Trim();

    public string OwnerEmailText => OwnerEmail.Trim();

    public string SharedWithText => SharedWith.Trim();

    /// <summary>Owner filter that can actually be applied (a "person" without an email means anyone).</summary>
    public OwnerFilter EffectiveOwner => Owner == OwnerFilter.Person && OwnerEmailText.Length == 0 ? OwnerFilter.Anyone : Owner;

    /// <summary>Filters only a server can answer (owner, sharing, trash, star).</summary>
    public bool HasServerFilters =>
        EffectiveOwner != OwnerFilter.Anyone || SharedWithText.Length > 0 || InTrash || Starred || Scope == SearchScope.SharedWithMe;

    public string ContentText => Content.Trim();

    public bool HasWildcards => NameText.IndexOfAny(['*', '?']) >= 0;

    /// <summary>True when any filter besides the name is set (drives the "Clear filters" button and chip highlights).</summary>
    public bool HasFilters =>
        Category != FileCategory.Any || Modified != DateFilter.Any || Size != SizeFilter.Any ||
        ContentText.Length > 0 || Scope != SearchScope.CurrentFolder || HasServerFilters;

    /// <summary>True when there is something to search for (a scope alone is not enough).</summary>
    public bool HasCriteria =>
        NameText.Length > 0 || ContentText.Length > 0 ||
        Category != FileCategory.Any || Modified != DateFilter.Any || Size != SizeFilter.Any || HasServerFilters;

    /// <summary>Same filters, no name / words (what chips keep while the user types a new name).</summary>
    public SearchQuery WithoutText() => this with { Name = string.Empty, Content = string.Empty };

    /// <summary>Drops the parts a provider can't answer, so nothing silently widens or empties a search.</summary>
    public SearchQuery RestrictTo(StorageCapabilities capabilities)
    {
        var q = this;
        if ((capabilities & StorageCapabilities.ContentSearch) == 0) q = q with { Content = string.Empty };
        if ((capabilities & StorageCapabilities.SearchSharing) == 0)
        {
            q = q with
            {
                Owner = OwnerFilter.Anyone,
                OwnerEmail = string.Empty,
                SharedWith = string.Empty,
                Scope = q.Scope == SearchScope.SharedWithMe ? SearchScope.CurrentFolder : q.Scope,
            };
        }
        if ((capabilities & StorageCapabilities.SearchTrash) == 0) q = q with { InTrash = false };
        if ((capabilities & StorageCapabilities.SearchStarred) == 0) q = q with { Starred = false };
        return q;
    }

    /// <summary>Modified range in UTC: [After, Before). Null bounds are open.</summary>
    public (DateTimeOffset? After, DateTimeOffset? Before) ModifiedRange(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.LocalDateTime.Date, now.Offset);
        return Modified switch
        {
            DateFilter.Today => (today, null),
            DateFilter.Last7Days => (today.AddDays(-6), null),
            DateFilter.Last30Days => (today.AddDays(-29), null),
            DateFilter.ThisYear => (new DateTimeOffset(today.Year, 1, 1, 0, 0, 0, now.Offset), null),
            DateFilter.LastYear => (new DateTimeOffset(today.Year - 1, 1, 1, 0, 0, 0, now.Offset), new DateTimeOffset(today.Year, 1, 1, 0, 0, 0, now.Offset)),
            DateFilter.Custom => (
                ModifiedFrom is { } from ? new DateTimeOffset(from.Date, now.Offset) : null,
                ModifiedTo is { } to ? new DateTimeOffset(to.Date.AddDays(1), now.Offset) : null),
            _ => (null, null),
        };
    }

    /// <summary>Size range in bytes: [Min, Max). Null bounds are open.</summary>
    public (long? Min, long? Max) SizeRange() => Size switch
    {
        SizeFilter.Under1MB => (null, Mb),
        SizeFilter.From1To100MB => (Mb, 100 * Mb),
        SizeFilter.From100MBTo1GB => (100 * Mb, Gb),
        SizeFilter.Over1GB => (Gb, null),
        SizeFilter.Custom => (MinSize, MaxSize is { } max ? max + 1 : null),
        _ => (null, null),
    };

    public SearchMatcher CreateMatcher(DateTimeOffset now) => new(this, now);

    private const long Mb = 1024 * 1024;
    private const long Gb = 1024 * Mb;
}

/// <summary>Checks items against a <see cref="SearchQuery"/>. Build once per search: bounds are resolved up front.</summary>
public sealed class SearchMatcher
{
    private readonly string _name;
    private readonly bool _wildcard;
    private readonly FileCategory _category;
    private readonly DateTimeOffset? _after;
    private readonly DateTimeOffset? _before;
    private readonly long? _minSize;
    private readonly long? _maxSize;
    private readonly bool _sizeFilter;

    public SearchMatcher(SearchQuery query, DateTimeOffset now)
    {
        _name = query.NameText;
        _wildcard = query.HasWildcards;
        _category = query.Category;
        (_after, _before) = query.ModifiedRange(now);
        (_minSize, _maxSize) = query.SizeRange();
        _sizeFilter = query.Size != SizeFilter.Any;
    }

    public DateTimeOffset? ModifiedAfter => _after;

    public DateTimeOffset? ModifiedBefore => _before;

    /// <summary>True when every item passes (no name and no filters): callers can skip the check.</summary>
    public bool MatchesEverything =>
        _name.Length == 0 && _category == FileCategory.Any && _after is null && _before is null && !_sizeFilter;

    public bool Matches(StorageItem item) =>
        Matches(item.Name, item.IsContainer, item.Size, item.Modified, item.TypeDescription);

    public bool Matches(string name, bool isFolder, long? size, DateTimeOffset? modified, string? typeDescription = null)
    {
        if (_name.Length > 0 && !(_wildcard
                ? FileSystemName.MatchesSimpleExpression(_name, name, ignoreCase: true)
                : name.Contains(_name, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (_category != FileCategory.Any)
        {
            var kind = FileCategories.Of(name, isFolder, typeDescription);
            if (_category == FileCategory.Media ? kind is not (FileCategory.Image or FileCategory.Video) : kind != _category) return false;
        }

        if (_after is not null || _before is not null)
        {
            if (modified is not { } m) return false;
            if (_after is { } after && m < after) return false;
            if (_before is { } before && m >= before) return false;
        }

        if (_sizeFilter)
        {
            // Folders have no size of their own, so a size filter means "files of that size".
            if (isFolder || size is not { } s) return false;
            if (_minSize is { } min && s < min) return false;
            if (_maxSize is { } max && s >= max) return false;
        }

        return true;
    }
}

/// <summary>Maps names (by extension) and cloud-native document types to a <see cref="FileCategory"/>.</summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    public static FileCategory Of(StorageItem item) => Of(item.Name, item.IsContainer, item.TypeDescription);

    public static FileCategory Of(string name, bool isFolder, string? typeDescription = null)
    {
        if (isFolder) return FileCategory.Folder;

        // Google's own formats have no extension; the provider names them in TypeDescription.
        switch (typeDescription)
        {
            case "Google Docs": return FileCategory.Document;
            case "Google Sheets": return FileCategory.Spreadsheet;
            case "Google Slides": return FileCategory.Presentation;
            case "Google Drawings": return FileCategory.Image;
        }

        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return FileCategory.Any;
        return ByExtension.GetValueOrDefault(name[dot..], FileCategory.Any);
    }

    /// <summary>Extensions in a category (used by providers that filter by extension on the server).</summary>
    public static IEnumerable<string> ExtensionsOf(FileCategory category) =>
        ByExtension.Where(p => p.Value == category).Select(p => p.Key);

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, string extensions)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[ext] = category;
        }

        Add(FileCategory.Document, ".doc .docx .docm .dot .dotx .odt .rtf .txt .md .markdown .pages .wpd .tex .epub");
        Add(FileCategory.Spreadsheet, ".xls .xlsx .xlsm .xlsb .xlt .xltx .ods .csv .tsv .numbers");
        Add(FileCategory.Presentation, ".ppt .pptx .pptm .pps .ppsx .pot .potx .odp .key");
        Add(FileCategory.Pdf, ".pdf");
        Add(FileCategory.Image, ".png .jpg .jpeg .jfif .gif .bmp .webp .ico .tif .tiff .heic .heif .avif .svg .psd .ai .raw .cr2 .nef .arw .dng .eps");
        Add(FileCategory.Video, ".mp4 .m4v .mov .mkv .webm .avi .wmv .flv .3gp .mpg .mpeg .ts .m2ts .vob");
        Add(FileCategory.Audio, ".mp3 .wav .flac .m4a .aac .ogg .opus .wma .aiff .aif .mid .midi .amr");
        Add(FileCategory.Archive, ".zip .rar .7z .tar .gz .tgz .bz2 .xz .zst .cab .iso .lz .lzma");
        Add(FileCategory.Code, ".cs .csproj .sln .xaml .vb .fs .c .h .cpp .hpp .cc .java .kt .go .rs .py .rb .php .lua .swift .dart " +
            ".scala .sql .js .mjs .cjs .jsx .ts .tsx .vue .svelte .html .htm .css .scss .less .sh .ps1 .bat .cmd .json .xml .yaml .yml " +
            ".toml .gd .tscn .shader .glsl .hlsl .ipynb .r");
        return map;
    }
}
