using ACadSharp.Tables;
using ACadSharp.Tables.Collections;
using CSMath;
using System;
using System.Collections.Generic;

namespace ACadSharp.IO.DWG.PreR13;

internal partial class DwgPreR13Reader
{
	/// <summary>
	/// Block records by their index in the block table, null for the entries that could not be read.
	/// </summary>
	private readonly List<BlockRecord> _blocks = new();

	/// <summary>
	/// Address of the first entity (the BLOCK entity) of each block, by the index in the block table.
	/// </summary>
	private readonly List<uint> _blockAddresses = new();

	private readonly List<AppId> _appIds = new();

	private readonly List<DimensionStyle> _dimensionStyles = new();

	private readonly List<Layer> _layers = new();

	private readonly List<LineType> _lineTypes = new();

	private readonly List<TextStyle> _textStyles = new();

	private readonly List<UCS> _ucss = new();

	private bool _hasEntryCrc { get { return this._version >= ACadVersion.AC1009; } }

	/// <summary>
	/// Reads the tables in the order needed to resolve the references between them.
	/// </summary>
	private void readTables()
	{
		this.readTable(this._fileHeader.LineTypes, this.readLineType, this._lineTypes);
		this.readTable(this._fileHeader.Layers, this.readLayer, this._layers);
		this.readTable(this._fileHeader.TextStyles, this.readTextStyle, this._textStyles);
		this.readTable(this._fileHeader.Ucss, this.readUcs, this._ucss);
		this.readTable<View>(this._fileHeader.Views, this.readView, null);
		this.readTable<VPort>(this._fileHeader.VPorts, this.readVPort, null);
		this.readTable(this._fileHeader.AppIds, this.readAppId, this._appIds);
		this.readTable(this._fileHeader.Blocks, this.readBlockRecord, this._blocks);
		this.readTable(this._fileHeader.DimensionStyles, this.readDimensionStyle, this._dimensionStyles);
	}

	private void readTable<T>(PreR13TableDescriptor table, Func<T> readEntry, List<T> entries)
		where T : TableEntry
	{
		if (table.IsEmpty)
		{
			return;
		}

		for (int i = 0; i < table.Count; i++)
		{
			T entry = null;

			try
			{
				this._reader.Position = table.Start + i * table.ItemSize;
				if (this._reader.CanRead(table.ItemSize))
				{
					entry = readEntry();
				}
				else
				{
					this.notify($"Entry {i} of the table {table.Name} is beyond the end of the drawing", NotificationType.Warning);
				}
			}
			catch (Exception ex)
			{
				if (!this._configuration.Failsafe)
				{
					throw;
				}

				this.notify($"Error reading the entry {i} of the table {table.Name}", NotificationType.Error, ex);
			}

			entries?.Add(entry);
		}
	}

	/// <summary>
	/// Reads the flags and the name that start every table entry, followed by the usage counter in R11 and later.
	/// </summary>
	private bool readEntryStart(out byte flags, out string name)
	{
		flags = this._reader.ReadByte();
		name = this._reader.ReadFixedString(32);

		if (this._version >= ACadVersion.AC1009)
		{
			//Usage counter
			this._reader.Skip(2);
		}

		//Deleted entries keep their place in the table, the indexes of the following entries do not change
		return (flags & 0x80) == 0 && !string.IsNullOrWhiteSpace(name);
	}

	/// <summary>
	/// Gets the entry with the name or adds a new one, the default entries of the document are reused.
	/// </summary>
	private T getOrAdd<T>(Table<T> table, string name, Func<string, T> create)
		where T : TableEntry
	{
		if (table.TryGetValue(name, out T entry))
		{
			return entry;
		}

		entry = create(name);
		table.Add(entry);
		return entry;
	}

	private bool tryGetEntry<T>(List<T> entries, int index, out T entry)
		where T : class
	{
		entry = null;
		if (index < 0 || index >= entries.Count)
		{
			return false;
		}

		entry = entries[index];
		return entry != null;
	}

	private static StandardFlags toStandardFlags(byte flags)
	{
		//The highest bit is not part of the DXF flags
		return (StandardFlags)(flags & 0x7F);
	}

	private LineType readLineType()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		string description = this._reader.ReadFixedString(48);
		byte alignment = this._reader.ReadByte();
		byte segmentCount = this._reader.ReadByte();
		//Pattern length, the sum of the segments
		this._reader.ReadDouble();

		double[] lengths = new double[12];
		for (int i = 0; i < lengths.Length; i++)
		{
			lengths[i] = this._reader.ReadDouble();
		}

		if (name.Equals(LineType.ByLayerName, StringComparison.OrdinalIgnoreCase)
			|| name.Equals(LineType.ByBlockName, StringComparison.OrdinalIgnoreCase))
		{
			return this._document.LineTypes[name];
		}

		LineType lineType = this.getOrAdd(this._document.LineTypes, name, n => new LineType(n));
		lineType.Flags = toStandardFlags(flags);
		lineType.Description = description;

		if (alignment != 0)
		{
			lineType.Alignment = (char)alignment;
		}

		if (!lineType.IsComplex)
		{
			for (int i = 0; i < Math.Min((int)segmentCount, lengths.Length); i++)
			{
				lineType.AddSegment(new LineType.Segment { Length = lengths[i] });
			}
		}

		return lineType;
	}

	private Layer readLayer()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		short color = this._reader.ReadInt16();
		short lineTypeIndex = this._reader.ReadInt16();

		Layer layer = this.getOrAdd(this._document.Layers, name, n => new Layer(n));
		layer.Flags = (LayerFlags)(flags & 0x7F);

		//A negative color turns the layer off
		layer.IsOn = color >= 0;
		short index = Math.Abs(color);
		if (index > 0 && index < 256)
		{
			layer.Color = new Color(index);
		}

		if (this.tryGetEntry(this._lineTypes, lineTypeIndex, out LineType lineType))
		{
			layer.LineType = lineType;
		}

		return layer;
	}

	private TextStyle readTextStyle()
	{
		//The styles that load a shape file have no name
		if (!this.readEntryStart(out byte flags, out string name)
			&& ((flags & 0x80) != 0 || (flags & (byte)StyleFlags.IsShape) == 0))
		{
			return null;
		}

		double height = this._reader.ReadDouble();
		double width = this._reader.ReadDouble();
		double oblique = this._reader.ReadDouble();
		byte generation = this._reader.ReadByte();
		double lastHeight = this._reader.ReadDouble();
		string font = this._reader.ReadFixedString(64);
		string bigFont = this._reader.ReadFixedString(64);

		TextStyle style = string.IsNullOrWhiteSpace(name)
			? this.addUnnamedStyle()
			: this.getOrAdd(this._document.TextStyles, name, n => new TextStyle(n));
		style.Flags = (StyleFlags)(flags & 0x7F);
		style.Height = height;
		style.Width = width;
		style.ObliqueAngle = oblique;
		style.MirrorFlag = (Entities.TextMirrorFlag)generation;
		style.LastHeight = lastHeight;
		style.Filename = font;
		style.BigFontFilename = bigFont;

		return style;
	}

	private TextStyle addUnnamedStyle()
	{
		//The table gives a name to the entries without one
		TextStyle style = new TextStyle();
		this._document.TextStyles.Add(style);
		return style;
	}

	private UCS readUcs()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		UCS ucs = this.getOrAdd(this._document.UCSs, name, n => new UCS(n));
		ucs.Flags = toStandardFlags(flags);
		ucs.Origin = this._reader.ReadXYZ();
		ucs.XAxis = this._reader.ReadXYZ();
		ucs.YAxis = this._reader.ReadXYZ();

		return ucs;
	}

	private View readView()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		View view = this.getOrAdd(this._document.Views, name, n => new View(n));
		view.Flags = toStandardFlags(flags);
		view.Height = this._reader.ReadDouble();
		view.Center = this._reader.ReadXY();
		view.Width = this._reader.ReadDouble();
		view.Direction = this._reader.ReadXYZ();

		if (this._version >= ACadVersion.AC1009)
		{
			//Flag 3d
			this._reader.Skip(2);
			view.Target = this._reader.ReadXYZ();
			view.ViewMode = (ViewModeType)this._reader.ReadInt16();
			view.LensLength = this._reader.ReadDouble();
			view.FrontClipping = this._reader.ReadDouble();
			view.BackClipping = this._reader.ReadDouble();
			view.Angle = this._reader.ReadDouble();
		}

		return view;
	}

	private VPort readVPort()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		VPort vport = this.getOrAdd(this._document.VPorts, name, n => new VPort(n));
		vport.Flags = toStandardFlags(flags);
		vport.BottomLeft = this._reader.ReadXY();
		vport.TopRight = this._reader.ReadXY();
		vport.Target = this._reader.ReadXYZ();
		vport.Direction = this._reader.ReadXYZ();
		vport.TwistAngle = this._reader.ReadDouble();
		vport.ViewHeight = this._reader.ReadDouble();
		vport.Center = this._reader.ReadXY();
		vport.AspectRatio = this._reader.ReadDouble();
		vport.LensLength = this._reader.ReadDouble();
		vport.FrontClippingPlane = this._reader.ReadDouble();
		vport.BackClippingPlane = this._reader.ReadDouble();
		vport.ViewMode = (ViewModeType)this._reader.ReadInt16();
		vport.CircleZoomPercent = this._reader.ReadInt16();
		//Fast zoom, UCS icon
		this._reader.Skip(2 + 2);
		vport.SnapOn = this._reader.ReadInt16() != 0;
		vport.ShowGrid = this._reader.ReadInt16() != 0;
		vport.IsometricSnap = this._reader.ReadInt16() != 0;
		vport.SnapIsoPair = this._reader.ReadInt16();
		vport.SnapRotation = this._reader.ReadDouble();
		vport.SnapBasePoint = this._reader.ReadXY();
		vport.SnapSpacing = this._reader.ReadXY();
		vport.GridSpacing = this._reader.ReadXY();

		return vport;
	}

	private AppId readAppId()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		AppId appId = this.getOrAdd(this._document.AppIds, name, n => new AppId(n));
		appId.Flags = toStandardFlags(flags);
		return appId;
	}

	private BlockRecord readBlockRecord()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			this._blockAddresses.Add(0);
			return null;
		}

		this._blockAddresses.Add(this._reader.ReadUInt32());

		BlockRecord record;
		if (name.Equals(BlockRecord.ModelSpaceName, StringComparison.OrdinalIgnoreCase)
			|| name.Equals(BlockRecord.PaperSpaceName, StringComparison.OrdinalIgnoreCase))
		{
			record = this._document.BlockRecords[name];
		}
		else
		{
			//The anonymous blocks of R11 share their name, the readers number them
			string unique = name;
			for (int i = 1; this._document.BlockRecords.Contains(unique); i++)
			{
				unique = $"{name}{i}";
			}

			record = new BlockRecord(unique);
			record.Flags = (Blocks.BlockTypeFlags)(flags & 0x7F);
			this._document.BlockRecords.Add(record);
		}

		return record;
	}

	private DimensionStyle readDimensionStyle()
	{
		if (!this.readEntryStart(out byte flags, out string name))
		{
			return null;
		}

		PreR13ByteReader r = this._reader;

		DimensionStyle style = this.getOrAdd(this._document.DimensionStyles, name, n => new DimensionStyle(n));
		style.Flags = toStandardFlags(flags);

		double scale = r.ReadDouble();
		if (scale >= 0)
		{
			style.ScaleFactor = scale;
		}
		style.ArrowSize = r.ReadDouble();
		style.ExtensionLineOffset = r.ReadDouble();
		style.DimensionLineIncrement = r.ReadDouble();
		style.ExtensionLineExtension = r.ReadDouble();
		style.Rounding = r.ReadDouble();
		style.DimensionLineExtension = r.ReadDouble();
		style.PlusTolerance = r.ReadDouble();
		style.MinusTolerance = r.ReadDouble();
		style.TextHeight = r.ReadDouble();
		style.CenterMarkSize = r.ReadDouble();
		style.TickSize = r.ReadDouble();
		style.AlternateUnitScaleFactor = r.ReadDouble();
		style.LinearScaleFactor = r.ReadDouble();
		style.TextVerticalPosition = r.ReadDouble();
		style.GenerateTolerances = r.ReadByte() != 0;
		style.LimitsGeneration = r.ReadByte() != 0;
		style.TextInsideHorizontal = r.ReadByte() != 0;
		style.TextOutsideHorizontal = r.ReadByte() != 0;
		style.SuppressFirstExtensionLine = r.ReadByte() != 0;
		style.SuppressSecondExtensionLine = r.ReadByte() != 0;
		style.TextVerticalAlignment = (DimensionTextVerticalAlignment)r.ReadByte();
		style.ZeroHandling = (ZeroHandling)r.ReadByte();
		style.AlternateUnitDimensioning = r.ReadByte() != 0;
		style.AlternateUnitDecimalPlaces = r.ReadByte();
		style.TextOutsideExtensions = r.ReadByte() != 0;
		style.SeparateArrowBlocks = r.ReadByte() != 0;
		style.TextInsideExtensions = r.ReadByte() != 0;
		style.SuppressOutsideExtensions = r.ReadByte() != 0;
		style.PostFix = r.ReadFixedString(16);
		style.AlternateDimensioningSuffix = r.ReadFixedString(16);
		string arrow = r.ReadFixedString(16);
		string arrow1 = r.ReadFixedString(16);
		string arrow2 = r.ReadFixedString(66);
		style.DimensionLineColor = new Color(r.ReadInt16());
		style.ExtensionLineColor = new Color(r.ReadInt16());
		style.TextColor = new Color(r.ReadInt16());
		//DIMUPT
		r.Skip(1);
		style.ToleranceScaleFactor = r.ReadDouble();
		style.DimensionLineGap = r.ReadDouble();

		if (this._document.BlockRecords.TryGetValue(arrow, out BlockRecord block))
		{
			style.ArrowBlock = block;
		}

		if (this._document.BlockRecords.TryGetValue(arrow1, out block))
		{
			style.DimArrow1 = block;
		}

		if (this._document.BlockRecords.TryGetValue(arrow2, out block))
		{
			style.DimArrow2 = block;
		}

		return style;
	}
}
