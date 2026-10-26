namespace LabelScope.Core.Samples;

/// <summary>The groups the sample labels are listed under, in the order the menu shows them.</summary>
public enum SampleGroup
{
    /// <summary>Parcel, carton, pallet and freight labels.</summary>
    Shipping,

    /// <summary>Picking, bin, rack, receiving and counting labels.</summary>
    Warehouse,

    /// <summary>Shelf, product and clothing labels.</summary>
    Retail,

    /// <summary>Serial-number, part, asset and lot labels.</summary>
    Manufacturing,

    /// <summary>Pharmacy and laboratory labels.</summary>
    Healthcare,

    /// <summary>Paper documents.</summary>
    Documents,
}

/// <summary>
/// One built-in sample label. The ZPL itself is an embedded file (see <see cref="SampleLabels"/>); the title and the group
/// name are looked up in the current language every time they are asked for, so a language switch is followed.
/// </summary>
/// <param name="Number">Position in the catalog, from 1. It names the file and the title resource (<c>Sample_01</c>).</param>
/// <param name="Id">The slug, for example <c>parcel-shipping-label</c>. It is the sample's persistent identity: it is what
/// is kept with a saved job, so it never changes once released, whatever the title or the number.</param>
/// <param name="Group">The group the sample is listed under.</param>
public sealed record SampleLabel(int Number, string Id, SampleGroup Group)
{
    /// <summary>The file name in the <c>samples</c> folder and in the embedded resources, for example <c>01-parcel-shipping-label.zpl</c>.</summary>
    public string FileName => $"{Number:00}-{Id}.zpl";

    /// <summary>The title in the current language.</summary>
    public string Title => Text.Get($"Sample_{Number:00}");

    /// <summary>The name of the group in the current language.</summary>
    public string GroupTitle => Text.Get($"SampleGroup_{Group}");
}
