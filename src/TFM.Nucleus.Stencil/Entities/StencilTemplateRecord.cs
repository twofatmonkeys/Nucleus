using TFM.Nucleus.Data;

namespace TFM.Nucleus.Stencil;

/// <summary>
/// One TFM.Stencil template's metadata: every scalar on <c>Template</c> has a column, and the file
/// manifest travels as JSON, because listings read it whole and never query into it. The bytes of
/// each file live in <see cref="StencilTemplateFile"/> rows, so a listing never touches content.
/// <para>
/// <see cref="Collection"/> partitions the table between consumers, so one database can hold
/// Mailroom and Paperwork bundles side by side. <see cref="RecordKey"/> is
/// <c>{collection}:{key}</c>, lowercased, and carries the unique index: lowercasing it makes the
/// uniqueness rule the same under any database collation. <see cref="Key"/> keeps the caller's own
/// casing so a template reads back exactly as it was saved.
/// </para>
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class StencilTemplateRecord : CoreObject<StencilTemplateRecord>
{
    /// <summary>The consumer's partition, lowercased. Every listing filters on it.</summary>
    [DatabaseColumn(DataType.Unicode, NonClusteredIndex = true)]
    public string? Collection { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The template key as the caller gave it.</summary>
    [DatabaseColumn(DataType.Unicode)]
    public string? Key { get => GetValue<string>(); set => SetValue(value); }

    /// <summary><c>{collection}:{key}</c>, lowercased. The unique index is the upsert key and the per-render lookup.</summary>
    [DatabaseColumn(DataType.Unicode, Unique = true)]
    public string? RecordKey { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, -1)]
    public string? Description { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? EntryPath { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The library's opaque hash over the manifest. ASCII, so the default varchar is enough.</summary>
    [DatabaseColumn]
    public string? ContentStamp { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset CreatedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset UpdatedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    /// <summary>The <c>TemplateFileInfo</c> manifest as JSON; null when the template has no files.</summary>
    [DatabaseColumn(DataType.Unicode, -1)]
    public string? FilesJson { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(Collection))
            throw new InvalidOperationException("A template record needs a collection.");
        if (string.IsNullOrEmpty(Key))
            throw new InvalidOperationException("A template record needs a key.");
        if (string.IsNullOrEmpty(RecordKey))
            throw new InvalidOperationException("A template record needs a record key.");
    }
}
