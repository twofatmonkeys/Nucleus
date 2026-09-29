using System.Text.Json;
using TFM.Stencil.Abstractions;

namespace TFM.Nucleus.Stencil;

/// <summary>
/// Copies between the library's <see cref="Template"/> and <see cref="StencilTemplateRecord"/>, and
/// builds the two composite keys. <see cref="ToTemplate"/> builds a fresh instance on every call,
/// which is how the store meets the contract's detachment rule by construction: nothing handed out
/// is ever the row itself.
/// </summary>
internal static class StencilTemplateMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes every column from the template.</summary>
    public static void Apply(StencilTemplateRecord row, Template template, string collection, string recordKey)
    {
        row.Collection = collection;
        row.Key = template.Key;
        row.RecordKey = recordKey;
        row.Name = template.Name;
        row.Description = template.Description;
        row.EntryPath = template.EntryPath;
        row.ContentStamp = template.ContentStamp;
        row.CreatedUtc = template.CreatedUtc;
        row.UpdatedUtc = template.UpdatedUtc;
        row.FilesJson = template.Files.Count == 0 ? null : JsonSerializer.Serialize(template.Files, Json);
    }

    /// <summary>A new template from the row.</summary>
    public static Template ToTemplate(StencilTemplateRecord row)
    {
        var template = new Template
        {
            Key = row.Key ?? string.Empty,
            Name = row.Name ?? string.Empty,
            Description = row.Description,
            EntryPath = row.EntryPath ?? TemplateConventions.DefaultEntryPath,
            ContentStamp = row.ContentStamp ?? string.Empty,
            CreatedUtc = row.CreatedUtc,
            UpdatedUtc = row.UpdatedUtc,
        };
        if (row.FilesJson is { Length: > 0 } files)
            template.Files = JsonSerializer.Deserialize<List<TemplateFileInfo>>(files, Json) ?? [];
        return template;
    }

    /// <summary><c>{collection}:{key}</c>, lowercased, so uniqueness does not depend on the collation.</summary>
    public static string RecordKeyFor(string collection, string key) => $"{collection}:{key}".ToLowerInvariant();

    /// <summary><c>{recordKey}|{path}</c>, lowercased.</summary>
    public static string FileKeyFor(string recordKey, string path) => $"{recordKey}|{path}".ToLowerInvariant();
}
