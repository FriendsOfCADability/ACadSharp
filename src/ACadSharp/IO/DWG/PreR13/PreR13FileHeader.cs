namespace ACadSharp.IO.DWG.PreR13;

/// <summary>
/// Location of a table in a pre-R13 drawing, the entries are records of fixed size stored one after the other.
/// </summary>
internal class PreR13TableDescriptor
{
	public int Count { get; set; }

	public int ItemSize { get; set; }

	public string Name { get; set; }

	public int Start { get; set; }

	public bool IsEmpty { get { return this.Count <= 0 || this.ItemSize <= 0 || this.Start <= 0; } }

	public override string ToString()
	{
		return $"{this.Name}: {this.Count} x {this.ItemSize} at {this.Start}";
	}
}

/// <summary>
/// File header of a pre-R13 drawing together with the header variables that reference table entries by index.
/// </summary>
internal class PreR13FileHeader
{
	public int BlocksSize { get; set; }

	public int BlocksStart { get; set; }

	public short CurrentColor { get; set; } = 256;

	public short CurrentLayerIndex { get; set; }

	public short CurrentLineTypeIndex { get; set; } = short.MaxValue;

	public short CurrentPaperUcsIndex { get; set; } = -1;

	public short CurrentTextStyleIndex { get; set; }

	public short CurrentUcsIndex { get; set; } = -1;

	public int EntitiesEnd { get; set; }

	public int EntitiesStart { get; set; }

	public short EntitySectionCount { get; set; }

	public int ExtrasSize { get; set; }

	public int ExtrasStart { get; set; }

	public short HeaderVariableCount { get; set; }

	public int HeaderVariablesStart { get; set; }

	public short SectionCount { get; set; }

	public PreR13TableDescriptor AppIds { get; set; } = new();

	public PreR13TableDescriptor Blocks { get; set; } = new();

	public PreR13TableDescriptor DimensionStyles { get; set; } = new();

	public PreR13TableDescriptor Layers { get; set; } = new();

	public PreR13TableDescriptor LineTypes { get; set; } = new();

	public PreR13TableDescriptor TextStyles { get; set; } = new();

	public PreR13TableDescriptor Ucss { get; set; } = new();

	public PreR13TableDescriptor ViewportEntityHeaders { get; set; } = new();

	public PreR13TableDescriptor Views { get; set; } = new();

	public PreR13TableDescriptor VPorts { get; set; } = new();
}
