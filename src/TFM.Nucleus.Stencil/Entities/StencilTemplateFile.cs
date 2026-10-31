using TFM.Nucleus.Data;

namespace TFM.Nucleus.Stencil;

/// <summary>
/// The bytes of one file in one template's bundle. Rows are tied to their record by
/// <see cref="RecordKey"/> rather than by a Nucleus child collection: a child can only be saved
/// through its parent, and the store writes one file at a time. Deleting by record key also sweeps
/// a row whose record is already gone, which a content save racing a delete can leave behind.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class StencilTemplateFile : CoreObject<StencilTemplateFile>
{
    /// <summary>The owning record's <see cref="StencilTemplateRecord.RecordKey"/>. Deletes sweep by it.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? RecordKey { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The bundle-relative path as the caller gave it.</summary>
    [DatabaseColumn(DataType.Unicode)]
    public string? Path { get => GetValue<string>(); set => SetValue(value); }

    /// <summary><c>{recordKey}|{path}</c>, lowercased. The unique index is the upsert key and the lookup.</summary>
    [DatabaseColumn(DataType.Unicode, Unique = true)]
    public string? FileKey { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The file's bytes. Nucleus maps <c>byte[]</c> to varbinary(max).</summary>
    [DatabaseColumn]
    public byte[]? Content { get => GetValue<byte[]>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(RecordKey))
            throw new InvalidOperationException("A template file needs a record key.");
        if (string.IsNullOrEmpty(Path))
            throw new InvalidOperationException("A template file needs a path.");
        if (string.IsNullOrEmpty(FileKey))
            throw new InvalidOperationException("A template file needs a file key.");
    }
}
