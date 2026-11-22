using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.App.ViewModels;

/// <summary>User-facing text for search filters: chip labels, menu options and the results title.</summary>
public static class SearchLabels
{
    public static readonly FileCategory[] Categories = Enum.GetValues<FileCategory>();

    public static readonly DateFilter[] DatePresets =
        [DateFilter.Any, DateFilter.Today, DateFilter.Last7Days, DateFilter.Last30Days, DateFilter.ThisYear, DateFilter.LastYear];

    public static readonly SizeFilter[] SizePresets =
        [SizeFilter.Any, SizeFilter.Under1MB, SizeFilter.From1To100MB, SizeFilter.From100MBTo1GB, SizeFilter.Over1GB];

    public static string Category(FileCategory category) => category switch
    {
        FileCategory.Any => "Any type",
        FileCategory.Folder => "Folders",
        FileCategory.Document => "Documents",
        FileCategory.Spreadsheet => "Spreadsheets",
        FileCategory.Presentation => "Presentations",
        FileCategory.Pdf => "PDFs",
        FileCategory.Image => "Photos & images",
        FileCategory.Video => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Archive => "Archives (zip)",
        FileCategory.Media => "Photos & videos",
        _ => "Code",
    };

    public static string CategoryGlyph(FileCategory category) => category switch
    {
        FileCategory.Folder => "",
        FileCategory.Document => "",
        FileCategory.Spreadsheet => "",
        FileCategory.Presentation => "",
        FileCategory.Pdf => "",
        FileCategory.Image => "",
        FileCategory.Video => "",
        FileCategory.Audio => "",
        FileCategory.Archive => "",
        FileCategory.Code => "",
        _ => "",
    };

    public static string Date(DateFilter filter, SearchQuery query, DateTime today) => filter switch
    {
        DateFilter.Any => "Any time",
        DateFilter.Today => "Today",
        DateFilter.Last7Days => "Last 7 days",
        DateFilter.Last30Days => "Last 30 days",
        DateFilter.ThisYear => $"This year ({today.Year})",
        DateFilter.LastYear => $"Last year ({today.Year - 1})",
        _ => (query.ModifiedFrom, query.ModifiedTo) switch
        {
            ({ } from, { } to) => $"{from:d} – {to:d}",
            ({ } from, null) => $"Since {from:d}",
            (null, { } to) => $"Until {to:d}",
            _ => "Custom range",
        },
    };

    public static string Size(SizeFilter filter, SearchQuery query) => filter switch
    {
        SizeFilter.Any => "Any size",
        SizeFilter.Under1MB => "Under 1 MB",
        SizeFilter.From1To100MB => "1 – 100 MB",
        SizeFilter.From100MBTo1GB => "100 MB – 1 GB",
        SizeFilter.Over1GB => "Over 1 GB",
        _ => (query.MinSize, query.MaxSize) switch
        {
            ({ } min, { } max) => $"{Formatting.FormatSize(min)} – {Formatting.FormatSize(max)}",
            ({ } min, null) => $"Over {Formatting.FormatSize(min)}",
            (null, { } max) => $"Under {Formatting.FormatSize(max)}",
            _ => "Custom size",
        },
    };

    public static readonly OwnerFilter[] Owners = Enum.GetValues<OwnerFilter>();

    public static string Owner(OwnerFilter owner, string email = "") => owner switch
    {
        OwnerFilter.Anyone => "Anyone",
        OwnerFilter.Me => "Owned by me",
        OwnerFilter.NotMe => "Not owned by me",
        _ => email.Trim().Length > 0 ? $"Owned by {email.Trim()}" : "Specific person",
    };

    /// <summary>People chip: owner, or "Shared with …" when only that is set.</summary>
    public static string PeopleChip(SearchQuery q) =>
        q.EffectiveOwner != OwnerFilter.Anyone ? Owner(q.EffectiveOwner, q.OwnerEmail)
        : q.SharedWithText.Length > 0 ? $"Shared with {q.SharedWithText}"
        : "People";

    /// <summary>Chip text: the filter's name while unset ("Type"), its value once set ("Videos").</summary>
    public static string TypeChip(SearchQuery q) => q.Category == FileCategory.Any ? "Type" : Category(q.Category);

    public static string ModifiedChip(SearchQuery q, DateTime today) => q.Modified == DateFilter.Any ? "Modified" : Date(q.Modified, q, today);

    public static string SizeChip(SearchQuery q) => q.Size == SizeFilter.Any ? "Size" : Size(q.Size, q);

    /// <summary>"Search results for "lab" · Videos · Last 7 days".</summary>
    public static string Describe(SearchQuery q, DateTime today)
    {
        var parts = new List<string>();
        parts.Add(q.NameText.Length > 0 ? $"Search results for \"{q.NameText}\"" : "Search results");
        if (q.ContentText.Length > 0) parts.Add($"containing \"{q.ContentText}\"");
        if (q.Category != FileCategory.Any) parts.Add(Category(q.Category));
        if (q.Modified != DateFilter.Any) parts.Add(Date(q.Modified, q, today));
        if (q.Size != SizeFilter.Any) parts.Add(Size(q.Size, q));
        if (q.EffectiveOwner != OwnerFilter.Anyone) parts.Add(Owner(q.EffectiveOwner, q.OwnerEmail));
        if (q.SharedWithText.Length > 0) parts.Add($"shared with {q.SharedWithText}");
        if (q.Scope == SearchScope.SharedWithMe) parts.Add("Shared with me");
        if (q.Starred) parts.Add("Starred");
        if (q.InTrash) parts.Add("In trash");
        return string.Join(" · ", parts);
    }
}
