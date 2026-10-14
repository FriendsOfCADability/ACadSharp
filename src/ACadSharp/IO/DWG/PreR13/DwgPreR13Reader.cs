using ACadSharp.Exceptions;
using ACadSharp.Header;
using ACadSharp.Tables;
using CSUtilities.Text;
using System;
using System.Collections.Generic;
using System.Text;

namespace ACadSharp.IO.DWG.PreR13;

/// <summary>
/// Reader for the drawings written before R13: R10 (AC1006) and R11/R12 (AC1009).
/// </summary>
/// <remarks>
/// These drawings share nothing with the R13+ layout. The data is byte aligned, the tables are arrays of fixed size
/// records and the entities are stored one after the other, each one preceded by its size. Entities and table
/// entries reference each other by their index in the tables, there are no handles to resolve.
/// <br/>
/// The document is built directly with the public object model, the references are resolved while reading.
/// <br/>
/// The layout follows the Kaitai Struct descriptions of the formats by Michal Josef Špaček
/// (https://github.com/michal-josef-spacek/CAD-Format-DWG-AC1006 and CAD-Format-DWG-AC1009, BSD-2-Clause),
/// checked against drawings written by AutoCAD.
/// </remarks>
internal partial class DwgPreR13Reader
{
	/// <summary>
	/// Flags in the high byte of an address: the address is relative to the start of the blocks section.
	/// </summary>
	private const uint _blocksSectionFlag = 0x40;

	/// <summary>
	/// Flags in the high byte of an address: the address is relative to the start of the extra entities section.
	/// </summary>
	private const uint _extrasSectionFlag = 0x80;

	private readonly DwgReaderConfiguration _configuration;

	private readonly PreR13ByteReader _reader;

	private readonly ACadVersion _version;

	private CadDocument _document;

	private PreR13FileHeader _fileHeader;

	private readonly List<(string Name, object[] Values)> _headerValues = new();

	private readonly NotificationEventHandler _onNotification;

	/// <summary>
	/// Initializes a new instance of the <see cref="DwgPreR13Reader"/> class.
	/// </summary>
	/// <param name="data">Complete content of the drawing.</param>
	/// <param name="version">Version of the drawing, must be supported by <see cref="IsSupported(ACadVersion)"/>.</param>
	/// <param name="configuration">Reader configuration.</param>
	/// <param name="notification">Notification handler.</param>
	public DwgPreR13Reader(byte[] data, ACadVersion version, DwgReaderConfiguration configuration, NotificationEventHandler notification)
	{
		if (!IsSupported(version))
		{
			throw new CadNotSupportedException(version);
		}

		this._version = version;
		this._configuration = configuration ?? new DwgReaderConfiguration();
		this._onNotification = notification;
		this._reader = new PreR13ByteReader(data, this.getEncoding(this.defaultCodePage()));
	}

	/// <summary>
	/// Check if the version is read by this reader.
	/// </summary>
	public static bool IsSupported(ACadVersion version)
	{
		return version == ACadVersion.AC1006 || version == ACadVersion.AC1009;
	}

	/// <summary>
	/// Read the complete drawing.
	/// </summary>
	public CadDocument Read()
	{
		this._document = new CadDocument(this._version);

		this.readFileHeader();
		this.readHeaderVariables();

		this.readTables();

		this.readBlocks();
		this.readEntities();

		this.applyHeaderVariables(this._document.Header);

		return this._document;
	}

	/// <summary>
	/// Read only the header variables of the drawing.
	/// </summary>
	public CadHeader ReadHeader()
	{
		this._document = null;

		this.readFileHeader();
		this.readHeaderVariables();

		CadHeader header = new CadHeader(this._version);
		this.applyHeaderVariables(header);
		return header;
	}

	/// <summary>
	/// Code page of the drawings that do not set one.
	/// </summary>
	private CodePage defaultCodePage()
	{
		//R10 leaves the code page variable empty, the drawings of that time come from DOS where code page 437 was the default,
		//it shares the position of the german umlauts with code page 850
		return this._version == ACadVersion.AC1006 ? CodePage.Ibm437 : CodePage.Windows1252;
	}

	private Encoding getEncoding(CodePage codePage)
	{
		try
		{
#if !NET48
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
			return Encoding.GetEncoding((int)codePage);
		}
		catch (Exception ex)
		{
			this.notify($"Encoding for code page {codePage} not found, using Windows-1252", NotificationType.Warning, ex);
		}

		return TextEncoding.Windows1252();
	}

	private void notify(string message, NotificationType notificationType = NotificationType.None, Exception exception = null)
	{
		this._onNotification?.Invoke(this, new NotificationEventArgs(message, notificationType, exception));
	}

	private void readFileHeader()
	{
		PreR13ByteReader r = this._reader;
		r.Position = 0;

		PreR13FileHeader header = new PreR13FileHeader();

		//0x00 "ACxxxx" followed by zeros up to 0x0C
		r.Position = 0x0C;
		r.Skip(1);
		header.EntitySectionCount = r.ReadInt16();
		header.SectionCount = r.ReadInt16();
		header.HeaderVariableCount = r.ReadInt16();
		r.Skip(1);

		header.EntitiesStart = r.ReadInt32();
		header.EntitiesEnd = r.ReadInt32();
		header.BlocksStart = r.ReadInt32();
		header.BlocksSize = (int)(r.ReadUInt32() & 0x00FFFFFF);
		header.ExtrasStart = r.ReadInt32();
		header.ExtrasSize = (int)(r.ReadUInt32() & 0x00FFFFFF);

		header.Blocks = this.readTableDescriptor(nameof(header.Blocks));
		header.Layers = this.readTableDescriptor(nameof(header.Layers));
		header.TextStyles = this.readTableDescriptor(nameof(header.TextStyles));
		header.LineTypes = this.readTableDescriptor(nameof(header.LineTypes));
		header.Views = this.readTableDescriptor(nameof(header.Views));

		header.HeaderVariablesStart = r.Position;

		this._fileHeader = header;
	}

	private PreR13TableDescriptor readTableDescriptor(string name)
	{
		PreR13TableDescriptor table = new PreR13TableDescriptor();
		table.Name = name;
		table.ItemSize = this._reader.ReadUInt16();
		table.Count = this._reader.ReadUInt16();
		this._reader.Skip(2);
		table.Start = this._reader.ReadInt32();
		return table;
	}

	/// <summary>
	/// Reads the header variables, they follow the file header without any separation.
	/// </summary>
	/// <remarks>
	/// R10 stores a prefix of the R11 variables. The variables are kept by their DXF name and applied to the header
	/// once the tables are known, some of them reference table entries by index.
	/// </remarks>
	private void readHeaderVariables()
	{
		PreR13ByteReader r = this._reader;
		r.Position = this._fileHeader.HeaderVariablesStart;
		this._headerValues.Clear();

		this.setXYZ("$INSBASE", r.ReadXYZ());
		this.setHeader("$PLINEGEN", r.ReadInt16());
		this.setXYZ("$EXTMIN", r.ReadXYZ());
		this.setXYZ("$EXTMAX", r.ReadXYZ());
		this.setXY("$LIMMIN", r.ReadXY());
		this.setXY("$LIMMAX", r.ReadXY());
		//VIEWCTR, VIEWSIZE
		r.Skip(24 + 8);
		//SNAPMODE, SNAPUNIT, SNAPBASE, SNAPANG, SNAPSTYLE, SNAPISOPAIR, GRIDMODE, GRIDUNIT
		r.Skip(2 + 16 + 16 + 8 + 2 + 2 + 2 + 16);
		this.setHeader("$ORTHOMODE", r.ReadInt16());
		this.setHeader("$REGENMODE", r.ReadInt16());
		this.setHeader("$FILLMODE", r.ReadInt16());
		this.setHeader("$QTEXTMODE", r.ReadInt16());
		//DRAGMODE
		r.Skip(2);
		this.setHeader("$LTSCALE", r.ReadDouble());
		this.setHeader("$TEXTSIZE", r.ReadDouble());
		this.setHeader("$TRACEWID", r.ReadDouble());
		this._fileHeader.CurrentLayerIndex = r.ReadInt16();
		//Unknown, R10 does not store PSLTSCALE and TREEDEPTH at these positions
		r.Skip(4 + 4 + 2);
		short psltscale = r.ReadInt16();
		short treeDepth = r.ReadInt16();
		if (this._version >= ACadVersion.AC1009)
		{
			this.setHeader("$PSLTSCALE", psltscale);
			this.setHeader("$TREEDEPTH", treeDepth);
		}
		//Unknown, aspect ratio
		r.Skip(2 + 8);
		this.setHeader("$LUNITS", r.ReadInt16());
		this.setHeader("$LUPREC", r.ReadInt16());
		//AXISMODE, AXISUNIT
		r.Skip(2 + 16);
		this.setHeader("$SKETCHINC", r.ReadDouble());
		this.setHeader("$FILLETRAD", r.ReadDouble());
		this.setHeader("$AUNITS", r.ReadInt16());
		this.setHeader("$AUPREC", r.ReadInt16());
		this._fileHeader.CurrentTextStyleIndex = r.ReadInt16();
		this.setHeader("$OSMODE", r.ReadInt16());
		this.setHeader("$ATTMODE", r.ReadInt16());
		this.setHeader("$MENU", r.ReadFixedString(15));

		this.setHeader("$DIMSCALE", r.ReadDouble());
		this.setHeader("$DIMASZ", r.ReadDouble());
		this.setHeader("$DIMEXO", r.ReadDouble());
		this.setHeader("$DIMDLI", r.ReadDouble());
		this.setHeader("$DIMEXE", r.ReadDouble());
		this.setHeader("$DIMTP", r.ReadDouble());
		this.setHeader("$DIMTM", r.ReadDouble());
		this.setHeader("$DIMTXT", r.ReadDouble());
		this.setHeader("$DIMCEN", r.ReadDouble());
		this.setHeader("$DIMTSZ", r.ReadDouble());
		this.setHeader("$DIMTOL", r.ReadByte() != 0);
		this.setHeader("$DIMLIM", r.ReadByte() != 0);
		this.setHeader("$DIMTIH", r.ReadByte() != 0);
		this.setHeader("$DIMTOH", r.ReadByte() != 0);
		this.setHeader("$DIMSE1", r.ReadByte() != 0);
		this.setHeader("$DIMSE2", r.ReadByte() != 0);
		this.setHeader("$DIMTAD", (short)r.ReadByte());

		this.setHeader("$LIMCHECK", r.ReadInt16());
		//Second part of the menu name
		r.Skip(45);
		this.setHeader("$ELEVATION", r.ReadDouble());
		this.setHeader("$THICKNESS", r.ReadDouble());
		//VIEWDIR and the view points
		r.Skip(7 * 24);
		//Flag 3d
		r.Skip(2);
		this.setHeader("$BLIPMODE", r.ReadInt16());
		this.setHeader("$DIMZIN", (short)r.ReadByte());
		this.setHeader("$DIMRND", r.ReadDouble());
		this.setHeader("$DIMDLE", r.ReadDouble());
		this.setHeader("$DIMBLK", r.ReadFixedString(33));
		//CIRCLEZOOM, COORDS
		r.Skip(2 + 2);
		this._fileHeader.CurrentColor = r.ReadInt16();
		this._fileHeader.CurrentLineTypeIndex = r.ReadInt16();
		this.setHeader("$TDCREATE", readJulianDate(r));
		this.setHeader("$TDUPDATE", readJulianDate(r));
		this.setHeader("$TDINDWG", readJulianDate(r));
		this.setHeader("$TDUSRTIMER", readJulianDate(r));
		this.setHeader("$USRTIMER", r.ReadInt16());
		//FASTZOOM
		r.Skip(2);
		this.setHeader("$SKPOLY", r.ReadInt16());
		//Unknown date
		r.Skip(14);
		this.setHeader("$ANGBASE", r.ReadDouble());
		this.setHeader("$ANGDIR", r.ReadInt16());
		this.setHeader("$PDMODE", r.ReadInt16());
		this.setHeader("$PDSIZE", r.ReadDouble());
		this.setHeader("$PLINEWID", r.ReadDouble());
		for (int i = 1; i <= 5; i++)
		{
			this.setHeader($"$USERI{i}", r.ReadInt16());
		}
		for (int i = 1; i <= 5; i++)
		{
			this.setHeader($"$USERR{i}", r.ReadDouble());
		}
		this.setHeader("$DIMALT", r.ReadByte() != 0);
		this.setHeader("$DIMALTD", (short)r.ReadByte());
		this.setHeader("$DIMASO", (short)r.ReadByte());
		this.setHeader("$DIMSHO", (short)r.ReadByte());
		this.setHeader("$DIMPOST", r.ReadFixedString(16));
		this.setHeader("$DIMAPOST", r.ReadFixedString(16));
		this.setHeader("$DIMALTF", r.ReadDouble());
		this.setHeader("$DIMLFAC", r.ReadDouble());
		this.setHeader("$SPLINESEGS", r.ReadInt16());
		this.setHeader("$SPLFRAME", r.ReadInt16());
		//ATTREQ, ATTDIA
		r.Skip(2 + 2);
		this.setHeader("$CHAMFERA", r.ReadDouble());
		this.setHeader("$CHAMFERB", r.ReadDouble());
		this.setHeader("$MIRRTEXT", r.ReadInt16());

		this._fileHeader.Ucss = this.readTableDescriptor(nameof(this._fileHeader.Ucss));
		short codePage = r.ReadInt16();
		CodePage drawingCodePage = this.defaultCodePage();
		if (codePage > 0 && CadUtils.GetCodePage(codePage) != CodePage.Unknown)
		{
			//Same numbering as in the later versions
			drawingCodePage = CadUtils.GetCodePage(codePage);
		}

		this._reader.Encoding = this.getEncoding(drawingCodePage);
		string codePageName = CadUtils.GetCodePageName(drawingCodePage);
		if (!string.IsNullOrEmpty(codePageName))
		{
			this.setHeader("$DWGCODEPAGE", codePageName.ToUpperInvariant());
		}

		this.setXYZ("$UCSORG", r.ReadXYZ());
		this.setXYZ("$UCSXDIR", r.ReadXYZ());
		this.setXYZ("$UCSYDIR", r.ReadXYZ());
		//TARGET, LENSLENGTH, VIEWTWIST, FRONTZ, BACKZ, VIEWMODE
		r.Skip(24 + 8 + 8 + 8 + 8 + 2);
		this.setHeader("$DIMTOFL", r.ReadByte() != 0);
		this.setHeader("$DIMBLK1", r.ReadFixedString(33));
		this.setHeader("$DIMBLK2", r.ReadFixedString(33));
		this.setHeader("$DIMSAH", r.ReadByte() != 0);
		this.setHeader("$DIMTIX", r.ReadByte() != 0);
		this.setHeader("$DIMSOXD", r.ReadByte() != 0);
		this.setHeader("$DIMTVP", r.ReadDouble());
		//Unknown string
		r.Skip(33);
		//HANDLING, HANDSEED
		r.Skip(2 + 8);
		this.setHeader("$SURFU", r.ReadInt16());
		this.setHeader("$SURFV", r.ReadInt16());
		this.setHeader("$SURFTYPE", r.ReadInt16());
		this.setHeader("$SURFTAB1", r.ReadInt16());
		this.setHeader("$SURFTAB2", r.ReadInt16());

		this._fileHeader.VPorts = this.readTableDescriptor(nameof(this._fileHeader.VPorts));
		//FLATLAND
		r.Skip(2);
		this.setHeader("$SPLINETYPE", r.ReadInt16());
		//UCSICON
		r.Skip(2);
		this._fileHeader.CurrentUcsIndex = r.ReadInt16();

		if (this._version == ACadVersion.AC1006)
		{
			//Only the last R10 drawings have the application ids
			if (this._fileHeader.HeaderVariableCount >= 160 && r.CanRead(10 + 2))
			{
				this._fileHeader.AppIds = this.readTableDescriptor(nameof(this._fileHeader.AppIds));
				this.setHeader("$WORLDVIEW", r.ReadInt16());
			}

			return;
		}

		this._fileHeader.AppIds = this.readTableDescriptor(nameof(this._fileHeader.AppIds));
		this.setHeader("$WORLDVIEW", r.ReadInt16());
		//Unknown
		r.Skip(2 + 2);
		this._fileHeader.DimensionStyles = this.readTableDescriptor(nameof(this._fileHeader.DimensionStyles));
		//Unknown
		r.Skip(5);
		this.setHeader("$DIMCLRD", new Color(r.ReadInt16()));
		this.setHeader("$DIMCLRE", new Color(r.ReadInt16()));
		this.setHeader("$DIMCLRT", new Color(r.ReadInt16()));
		this.setHeader("$SHADEDGE", r.ReadInt16());
		this.setHeader("$SHADEDIF", r.ReadInt16());
		//Unknown
		r.Skip(2);
		this.setHeader("$UNITMODE", r.ReadInt16());
		//Unit ratios and names
		r.Skip(4 * 8 + 4 * 32);
		this.setHeader("$DIMTFAC", r.ReadDouble());
		this.setXYZ("$PUCSORG", r.ReadXYZ());
		this.setXYZ("$PUCSXDIR", r.ReadXYZ());
		this.setXYZ("$PUCSYDIR", r.ReadXYZ());
		this._fileHeader.CurrentPaperUcsIndex = r.ReadInt16();
		this.setHeader("$TILEMODE", r.ReadInt16());
		this.setHeader("$PLIMCHECK", r.ReadInt16());
		//Unknown
		r.Skip(2);
		this.setXYZ("$PEXTMIN", r.ReadXYZ());
		this.setXYZ("$PEXTMAX", r.ReadXYZ());
		this.setXY("$PLIMMIN", r.ReadXY());
		this.setXY("$PLIMMAX", r.ReadXY());
		this.setXYZ("$PINSBASE", r.ReadXYZ());
		this._fileHeader.ViewportEntityHeaders = this.readTableDescriptor(nameof(this._fileHeader.ViewportEntityHeaders));
		this.setHeader("$MAXACTVP", r.ReadInt16());
		this.setHeader("$DIMGAP", r.ReadDouble());
		this.setHeader("$PELEVATION", r.ReadDouble());

		if (this._fileHeader.HeaderVariableCount >= 205)
		{
			//R12
			this.setHeader("$VISRETAIN", r.ReadInt16());
		}
	}

	private static double readJulianDate(PreR13ByteReader r)
	{
		int days = r.ReadInt32();
		int milliseconds = r.ReadInt32();
		return days + milliseconds / 86400000.0;
	}

	private void setHeader(string name, params object[] values)
	{
		this._headerValues.Add((name, values));
	}

	private void setXY(string name, CSMath.XY value)
	{
		this.setHeader(name, value.X, value.Y);
	}

	private void setXYZ(string name, CSMath.XYZ value)
	{
		this.setHeader(name, value.X, value.Y, value.Z);
	}

	private void applyHeaderVariables(CadHeader header)
	{
		foreach ((string name, object[] values) in this._headerValues)
		{
			try
			{
				header.SetValue(name, values);
			}
			catch (Exception ex)
			{
				this.notify($"Invalid value for header variable {name}", NotificationType.Warning, ex);
			}
		}

		if (this._document == null)
		{
			return;
		}

		//Variables that reference table entries by their index
		if (this.tryGetEntry(this._layers, this._fileHeader.CurrentLayerIndex, out Layer layer))
		{
			header.CurrentLayerName = layer.Name;
		}

		if (this.tryGetEntry(this._textStyles, this._fileHeader.CurrentTextStyleIndex, out TextStyle style))
		{
			header.CurrentTextStyleName = style.Name;
		}

		header.CurrentEntityColor = this.toColor(this._fileHeader.CurrentColor);

		LineType lineType = this.getLineType(this._fileHeader.CurrentLineTypeIndex);
		if (lineType != null)
		{
			header.CurrentLineTypeName = lineType.Name;
		}
	}
}
