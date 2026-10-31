using TFM.Nucleus.Data;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// The version gate. Nucleus has no optimistic concurrency and cannot report how many rows an
/// update touched, so a version check written as <c>UPDATE ... WHERE Version = @expected</c> has
/// nothing to observe. Instead every save inserts a claim for the version it moves the record to,
/// inside the same transaction as the record write. Two writers racing from the same version both
/// try to insert the same claim; the unique index lets exactly one through and the other fails
/// with a unique violation the store reports as <c>S3AssetConcurrencyException</c>. The previous
/// claim is deleted in the same transaction, so the table holds one row per asset.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class S3AssetVersionClaim : CoreObject<S3AssetVersionClaim>
{
    [DatabaseColumn(Indexed = true)]
    public Guid AssetId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary><c>{AssetId:N}:{version}</c>. ASCII, so the default varchar is enough.</summary>
    [DatabaseColumn(Unique = true)]
    public string? Claim { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (AssetId == Guid.Empty)
            throw new InvalidOperationException("A version claim needs an asset id.");
        if (string.IsNullOrEmpty(Claim))
            throw new InvalidOperationException("A version claim needs a claim value.");
    }
}
