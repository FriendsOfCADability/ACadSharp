using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACadSharp.IO.DWG.PreR13;

internal partial class DwgPreR13Reader
{
	private enum EntityType : byte
	{
		Line = 1,
		Point = 2,
		Circle = 3,
		Shape = 4,
		Repeat = 5,
		EndRepeat = 6,
		Text = 7,
		Arc = 8,
		Trace = 9,
		Load = 10,
		Solid = 11,
		Block = 12,
		EndBlock = 13,
		Insert = 14,
		AttributeDefinition = 15,
		Attribute = 16,
		Seqend = 17,
		Jump = 18,
		Polyline = 19,
		Vertex = 20,
		Line3D = 21,
		Face3D = 22,
		Dimension = 23,
		Viewport = 24,
	}

	/// <summary>
	/// Common data at the start of every entity.
	/// </summary>
	private class EntityHeader
	{
		public byte? Color { get; set; }

		/// <summary>
		/// Position of the entity data after the common data.
		/// </summary>
		public int DataStart { get; set; }

		public List<ExtendedDataRecord> ExtendedData { get; set; }

		public List<int> ExtendedDataApps { get; set; }

		public byte Flags { get; set; }

		public double Elevation { get; set; }

		/// <summary>
		/// The entity is followed by ATTRIB entities and a SEQEND.
		/// </summary>
		public bool HasAttributes { get { return (this.Flags & 0x80) != 0; } }

		/// <summary>
		/// For LINE, POINT, 3DLINE and 3DFACE the points are stored without z, for the other entities the elevation is stored.
		/// </summary>
		public bool HasElevationFlag { get { return (this.Flags & 0x04) != 0; } }

		public bool IsDeleted { get { return (this.RawType & 0x80) != 0; } }

		/// <summary>
		/// In R11 and later the entities in paper space store the viewport they belong to,
		/// the entities of the blocks keep it from the space they were created in.
		/// </summary>
		public bool IsPaperSpace { get; set; }

		public short LayerIndex { get; set; }

		public int? LineTypeIndex { get; set; }

		/// <summary>
		/// Flags for the optional fields of each entity type.
		/// </summary>
		public ushort Options { get; set; }

		public byte RawType { get; set; }

		public int Size { get; set; }

		public int Start { get; set; }

		public double Thickness { get; set; }

		public EntityType Type { get { return (EntityType)(this.RawType & 0x7F); } }

		public bool Has(int option)
		{
			return (this.Options & option) != 0;
		}

		public override string ToString()
		{
			return $"{this.Type} at {this.Start}, size {this.Size}";
		}
	}

	/// <summary>
	/// Entity being built from several records: POLYLINE with its VERTEX records, INSERT with its ATTRIB records.
	/// </summary>
	private class ComplexEntity
	{
		public Entity Entity { get; set; }

		public EntityHeader Header { get; set; }

		public bool IsPaperSpace { get; set; }
	}

	/// <summary>
	/// Destination of the entities read from a sequence of records.
	/// </summary>
	private class EntityOwner
	{
		public BlockRecord Block { get; set; }

		/// <summary>
		/// The sequence is a block definition from BLOCK to ENDBLK, otherwise it is the entities section
		/// whose entities go to the model space or to the paper space.
		/// </summary>
		public bool IsBlockDefinition { get; set; }
	}

	private int _blocksEnd { get { return this._fileHeader.BlocksStart + this._fileHeader.BlocksSize; } }

	private int _extrasEnd { get { return this._fileHeader.ExtrasStart + this._fileHeader.ExtrasSize; } }

	private bool _hasEntityCrc { get { return this._version >= ACadVersion.AC1009; } }

	private void readBlocks()
	{
		for (int i = 0; i < this._blocks.Count; i++)
		{
			BlockRecord record = this._blocks[i];
			if (record == null)
			{
				continue;
			}

			int start = this.resolveBlockAddress(this._blockAddresses[i]);
			if (start < 0)
			{
				this.notify($"Address of the block {record.Name} is outside the blocks section", NotificationType.Warning);
				continue;
			}

			try
			{
				this.readEntitySequence(start, -1, new EntityOwner { Block = record, IsBlockDefinition = true });
			}
			catch (Exception ex)
			{
				if (!this._configuration.Failsafe)
				{
					throw;
				}

				this.notify($"Error reading the block {record.Name}", NotificationType.Error, ex);
			}
		}
	}

	private void readEntities()
	{
		if (this._fileHeader.EntitiesStart <= 0 || this._fileHeader.EntitiesEnd <= this._fileHeader.EntitiesStart)
		{
			return;
		}

		this.readEntitySequence(this._fileHeader.EntitiesStart, this._fileHeader.EntitiesEnd, new EntityOwner { Block = this._document.ModelSpace });
	}

	private int resolveBlockAddress(uint raw)
	{
		uint flags = raw >> 24;
		int address = (int)(raw & 0x00FFFFFF);

		if ((flags & _blocksSectionFlag) != 0 || address < this._fileHeader.BlocksStart)
		{
			address += this._fileHeader.BlocksStart;
		}

		if (address < this._fileHeader.BlocksStart || address >= this._blocksEnd)
		{
			return -1;
		}

		return address;
	}

	private int resolveJumpAddress(uint raw)
	{
		uint flags = raw >> 24;
		int address = (int)(raw & 0x00FFFFFF);

		if ((flags & _extrasSectionFlag) != 0)
		{
			return this._fileHeader.ExtrasStart + address;
		}

		if ((flags & _blocksSectionFlag) != 0)
		{
			return this._fileHeader.BlocksStart + address;
		}

		return address;
	}

	private bool isInSection(int position)
	{
		return (position >= this._fileHeader.EntitiesStart && position < this._fileHeader.EntitiesEnd)
			|| (position >= this._fileHeader.BlocksStart && position < this._blocksEnd)
			|| (this._fileHeader.ExtrasStart > 0 && position >= this._fileHeader.ExtrasStart && position < this._extrasEnd);
	}

	/// <summary>
	/// Reads the entity records starting at a position, following the JUMP records.
	/// </summary>
	/// <param name="start">Position of the first record.</param>
	/// <param name="end">Position where the sequence ends, -1 for a block that ends with its ENDBLK record.</param>
	/// <param name="owner">Destination of the entities.</param>
	private void readEntitySequence(int start, int end, EntityOwner owner)
	{
		HashSet<int> visited = new HashSet<int>();
		ComplexEntity complex = null;
		int position = start;
		//Position to continue at when a part of the sequence that was moved to the extra entities section ends without a JUMP back
		int returnPosition = -1;

		while (position != end)
		{
			if (!this.isInSection(position) || !visited.Add(position))
			{
				if (returnPosition > 0 && returnPosition != position && visited.Add(returnPosition))
				{
					position = returnPosition;
					returnPosition = -1;
					continue;
				}

				if (end > 0)
				{
					this.notify($"Entity sequence interrupted at {position}", NotificationType.Warning);
				}

				break;
			}

			EntityHeader header = this.readEntityHeader(position);
			if (header == null)
			{
				break;
			}

			int next = position + header.Size;

			if (header.Type == EntityType.Jump)
			{
				if (!header.IsDeleted)
				{
					this._reader.Position = position + 4;
					returnPosition = next;
					next = this.resolveJumpAddress(this._reader.ReadUInt32());
				}

				position = next;
				continue;
			}

			if (header.IsDeleted)
			{
				position = next;
				continue;
			}

			if (owner.IsBlockDefinition && header.Type == EntityType.Block && position != start)
			{
				this.notify($"The block {owner.Block.Name} has no end, the next block starts at {position}", NotificationType.Warning);
				break;
			}

			try
			{
				bool endOfBlock = this.processEntity(header, owner, ref complex);
				if (endOfBlock)
				{
					break;
				}
			}
			catch (Exception ex)
			{
				if (!this._configuration.Failsafe)
				{
					throw;
				}

				this.notify($"Error reading the entity {header}", NotificationType.Error, ex);
			}

			position = next;

			//The extra entities section was entered with a JUMP, its end leads back to the main sequence
			if (position == this._extrasEnd && returnPosition > 0)
			{
				position = returnPosition;
				returnPosition = -1;
			}
		}

		this.closeComplexEntity(owner, ref complex);
	}

	/// <summary>
	/// Reads one entity and adds it to its owner.
	/// </summary>
	/// <returns>True if the entity is the ENDBLK of a block.</returns>
	private bool processEntity(EntityHeader header, EntityOwner owner, ref ComplexEntity complex)
	{
		this._reader.Position = header.DataStart;

		switch (header.Type)
		{
			case EntityType.Block:
			case EntityType.EndBlock:
				this.closeComplexEntity(owner, ref complex);
				if (!owner.IsBlockDefinition)
				{
					this.notify($"Block definition entity {header} outside of the blocks section", NotificationType.Warning);
					return false;
				}

				if (header.Type == EntityType.Block)
				{
					this.readBlockBegin(header, owner.Block);
					return false;
				}

				return true;
			case EntityType.Seqend:
				this.closeComplexEntity(owner, ref complex);
				return false;
			case EntityType.Vertex:
				this.readVertex(header, complex);
				return false;
			case EntityType.Attribute:
				this.readAttribute(header, complex);
				return false;
		}

		this.closeComplexEntity(owner, ref complex);

		Entity entity = this.readEntity(header);
		if (entity == null)
		{
			return false;
		}

		this.checkEntityEnd(header);
		this.applyCommonData(entity, header);

		bool isComplex = entity is IPolyline
			|| (entity is Insert && header.HasAttributes);

		if (isComplex)
		{
			complex = new ComplexEntity { Entity = entity, Header = header, IsPaperSpace = header.IsPaperSpace };
		}
		else
		{
			this.addEntity(owner, entity, header.IsPaperSpace);
		}

		return false;
	}

	private void closeComplexEntity(EntityOwner owner, ref ComplexEntity complex)
	{
		if (complex == null)
		{
			return;
		}

		this.addEntity(owner, complex.Entity, complex.IsPaperSpace);
		complex = null;
	}

	private void addEntity(EntityOwner owner, Entity entity, bool isPaperSpace)
	{
		BlockRecord block = owner.Block;
		if (!owner.IsBlockDefinition && isPaperSpace)
		{
			block = this._document.PaperSpace;
		}

		block.Entities.Add(entity);
	}

	private EntityHeader readEntityHeader(int position)
	{
		PreR13ByteReader r = this._reader;

		r.Position = position;
		if (!r.CanRead(4))
		{
			this.notify($"Entity at {position} is beyond the end of the drawing", NotificationType.Warning);
			return null;
		}

		EntityHeader header = new EntityHeader();
		header.Start = position;
		header.RawType = r.ReadByte();
		header.Flags = r.ReadByte();
		header.Size = r.ReadInt16();

		if (header.Size < 4 || !r.CanRead(header.Size - 4))
		{
			this.notify($"Invalid size {header.Size} of the entity at {position}", NotificationType.Warning);
			return null;
		}

		if (header.Type == EntityType.Jump)
		{
			return header;
		}

		header.LayerIndex = r.ReadInt16();
		header.Options = r.ReadUInt16();

		byte extra = 0;
		if (this._version >= ACadVersion.AC1009 && (header.Flags & 0x40) != 0)
		{
			extra = r.ReadByte();
			if ((extra & 0x02) != 0)
			{
				this.readExtendedData(header);
			}
		}

		if ((header.Flags & 0x01) != 0)
		{
			header.Color = r.ReadByte();
		}

		if ((header.Flags & 0x02) != 0)
		{
			if (this._version >= ACadVersion.AC1009)
			{
				header.LineTypeIndex = r.ReadInt16();
			}
			else
			{
				header.LineTypeIndex = r.ReadByte();
			}
		}

		//LINE, POINT, 3DLINE and 3DFACE use the flag for the points without z, there is no elevation
		bool hasElevation = header.HasElevationFlag
			&& header.Type != EntityType.Line
			&& header.Type != EntityType.Point
			&& header.Type != EntityType.Line3D
			&& header.Type != EntityType.Face3D;
		bool hasThickness = (header.Flags & 0x08) != 0;

		if (this._version >= ACadVersion.AC1009)
		{
			//R11 stores the thickness before the elevation, R10 the other way round
			if (hasThickness)
			{
				header.Thickness = r.ReadDouble();
			}

			if (hasElevation)
			{
				header.Elevation = r.ReadDouble();
			}
		}
		else
		{
			if (hasElevation)
			{
				header.Elevation = r.ReadDouble();
			}

			if (hasThickness)
			{
				header.Thickness = r.ReadDouble();
			}
		}

		if ((header.Flags & 0x20) != 0)
		{
			//Handle, the documents get new handles
			byte length = r.ReadByte();
			r.Skip(length);
		}

		if ((extra & 0x04) != 0)
		{
			//Paper space viewport of the entity
			r.Skip(2);
			header.IsPaperSpace = true;
		}

		header.DataStart = r.Position;
		return header;
	}

	private void readExtendedData(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;
		ushort size = r.ReadUInt16();
		int end = r.Position + size;

		header.ExtendedData = new List<ExtendedDataRecord>();
		header.ExtendedDataApps = new List<int>();

		while (r.Position < end)
		{
			byte code = r.ReadByte();
			switch (code)
			{
				case 0:
					byte length = r.ReadByte();
					header.ExtendedData.Add(new ExtendedDataString(r.ReadFixedString(length)));
					break;
				case 1:
					//Application, starts the records of the application
					header.ExtendedDataApps.Add(header.ExtendedData.Count);
					header.ExtendedData.Add(new ExtendedDataInteger16(r.ReadInt16()));
					break;
				case 2:
					header.ExtendedData.Add(new ExtendedDataControlString(r.ReadByte() != 0));
					break;
				case 3:
					short layerIndex = r.ReadInt16();
					ulong layerHandle = this.tryGetEntry(this._layers, layerIndex, out Layer layer) ? layer.Handle : 0;
					header.ExtendedData.Add(new ExtendedDataLayer(layerHandle));
					break;
				case 4:
					byte chunkLength = r.ReadByte();
					header.ExtendedData.Add(new ExtendedDataBinaryChunk(r.ReadBytes(chunkLength)));
					break;
				case 5:
					byte[] handle = r.ReadBytes(8);
					ulong value = 0;
					foreach (byte b in handle)
					{
						value = (value << 8) | b;
					}
					header.ExtendedData.Add(new ExtendedDataHandle(value));
					break;
				case 10:
					header.ExtendedData.Add(new ExtendedDataCoordinate(r.ReadXYZ()));
					break;
				case 11:
					header.ExtendedData.Add(new ExtendedDataWorldCoordinate(r.ReadXYZ()));
					break;
				case 12:
					header.ExtendedData.Add(new ExtendedDataDisplacement(r.ReadXYZ()));
					break;
				case 13:
					header.ExtendedData.Add(new ExtendedDataDirection(r.ReadXYZ()));
					break;
				case 40:
					header.ExtendedData.Add(new ExtendedDataReal(r.ReadDouble()));
					break;
				case 41:
					header.ExtendedData.Add(new ExtendedDataDistance(r.ReadDouble()));
					break;
				case 42:
					header.ExtendedData.Add(new ExtendedDataScale(r.ReadDouble()));
					break;
				case 70:
					header.ExtendedData.Add(new ExtendedDataInteger16(r.ReadInt16()));
					break;
				case 71:
					header.ExtendedData.Add(new ExtendedDataInteger32(r.ReadInt32()));
					break;
				default:
					this.notify($"Unknown extended data code {code + 1000} in the entity at {header.Start}", NotificationType.Warning);
					header.ExtendedData = null;
					header.ExtendedDataApps = null;
					r.Position = end;
					return;
			}
		}

		r.Position = end;
	}

	/// <summary>
	/// Reports the entities whose fields do not match their size, a sign of an unknown optional field.
	/// </summary>
	private void checkEntityEnd(EntityHeader header)
	{
		int expected = header.Start + header.Size - (this._hasEntityCrc ? 2 : 0);
		if (this._reader.Position != expected)
		{
			this.notify($"The fields of the entity {header} end at {this._reader.Position} instead of {expected}, options {header.Options:X4}", NotificationType.None);
		}
	}

	private void applyCommonData(Entity entity, EntityHeader header)
	{
		if (this.tryGetEntry(this._layers, header.LayerIndex, out Layer layer))
		{
			entity.Layer = layer;
		}

		if (header.Color.HasValue)
		{
			entity.Color = this.toColor(header.Color.Value);
		}

		if (header.LineTypeIndex.HasValue)
		{
			LineType lineType = this.getLineType(header.LineTypeIndex.Value);
			if (lineType != null)
			{
				entity.LineType = lineType;
			}
		}

		if (header.ExtendedData != null && header.ExtendedData.Count > 0)
		{
			this.applyExtendedData(entity, header);
		}
	}

	private void applyExtendedData(Entity entity, EntityHeader header)
	{
		for (int i = 0; i < header.ExtendedDataApps.Count; i++)
		{
			int first = header.ExtendedDataApps[i];
			int last = i + 1 < header.ExtendedDataApps.Count ? header.ExtendedDataApps[i + 1] : header.ExtendedData.Count;

			short appIndex = (short)((ExtendedDataInteger16)header.ExtendedData[first]).Value;
			if (!this.tryGetEntry(this._appIds, appIndex, out AppId app))
			{
				this.notify($"Application {appIndex} of the extended data not found", NotificationType.Warning);
				continue;
			}

			List<ExtendedDataRecord> records = header.ExtendedData.GetRange(first + 1, last - first - 1);
			if (entity.ExtendedData.ContainsKey(app))
			{
				entity.ExtendedData.Get(app.Name).Records.AddRange(records);
			}
			else
			{
				entity.ExtendedData.Add(app, records);
			}
		}
	}

	private Color toColor(int value)
	{
		if (value == 0)
		{
			return Color.ByBlock;
		}

		if (value > 0 && value < 256)
		{
			return new Color((short)value);
		}

		return Color.ByLayer;
	}

	/// <summary>
	/// Gets the line type of an index, outside of the table the index means BYLAYER or BYBLOCK.
	/// </summary>
	private LineType getLineType(int index)
	{
		if (this.tryGetEntry(this._lineTypes, index, out LineType lineType))
		{
			return lineType;
		}

		//R11 uses 32767 for BYLAYER and 32766 for BYBLOCK, R10 stores the index of the entities in a byte with 255 for BYBLOCK
		bool byBlock = index == short.MaxValue - 1
			|| (this._version < ACadVersion.AC1009 && index == byte.MaxValue);

		return this._document?.LineTypes[byBlock ? LineType.ByBlockName : LineType.ByLayerName];
	}

	private XYZ readPoint(EntityHeader header)
	{
		XY xy = this._reader.ReadXY();
		return new XYZ(xy.X, xy.Y, header.Elevation);
	}

	/// <summary>
	/// Reads a point that has a z coordinate unless the flag says otherwise (LINE, POINT, 3DLINE, 3DFACE).
	/// </summary>
	private XYZ readOptional3D(EntityHeader header)
	{
		XY xy = this._reader.ReadXY();
		double z = header.HasElevationFlag ? 0 : this._reader.ReadDouble();
		return new XYZ(xy.X, xy.Y, z);
	}

	private Entity readEntity(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		switch (header.Type)
		{
			case EntityType.Line:
			case EntityType.Line3D:
				return this.readLine(header);
			case EntityType.Point:
				Point point = new Point();
				point.Location = this.readOptional3D(header);
				point.Thickness = header.Thickness;
				if (header.Has(0x01))
				{
					point.Normal = r.ReadXYZ();
				}
				if (header.Has(0x02))
				{
					point.Rotation = r.ReadDouble();
				}
				return point;
			case EntityType.Circle:
				Circle circle = new Circle();
				circle.Center = this.readPoint(header);
				circle.Radius = r.ReadDouble();
				circle.Thickness = header.Thickness;
				if (header.Has(0x01))
				{
					circle.Normal = r.ReadXYZ();
				}
				return circle;
			case EntityType.Arc:
				Arc arc = new Arc();
				arc.Center = this.readPoint(header);
				arc.Radius = r.ReadDouble();
				arc.StartAngle = r.ReadDouble();
				arc.EndAngle = r.ReadDouble();
				arc.Thickness = header.Thickness;
				if (header.Has(0x01))
				{
					arc.Normal = r.ReadXYZ();
				}
				return arc;
			case EntityType.Shape:
				return this.readShape(header);
			case EntityType.Text:
				return this.readText(header);
			case EntityType.Trace:
			case EntityType.Solid:
				Solid solid = new Solid();
				solid.FirstCorner = this.readPoint(header);
				solid.SecondCorner = this.readPoint(header);
				solid.ThirdCorner = this.readPoint(header);
				solid.FourthCorner = this.readPoint(header);
				solid.Thickness = header.Thickness;
				if (header.Has(0x01))
				{
					solid.Normal = r.ReadXYZ();
				}
				return solid;
			case EntityType.Insert:
				return this.readInsert(header);
			case EntityType.AttributeDefinition:
				AttributeDefinition definition = new AttributeDefinition();
				this.readAttributeData(header, definition, true);
				return definition;
			case EntityType.Polyline:
				return this.readPolyline(header);
			case EntityType.Face3D:
				Face3D face = new Face3D();
				face.FirstCorner = this.readOptional3D(header);
				face.SecondCorner = this.readOptional3D(header);
				face.ThirdCorner = this.readOptional3D(header);
				face.FourthCorner = this.readOptional3D(header);
				if (header.Has(0x01))
				{
					face.Flags = (InvisibleEdgeFlags)r.ReadInt16();
				}
				return face;
			case EntityType.Dimension:
				return this.readDimension(header);
			case EntityType.Viewport:
				XYZ center = r.ReadXYZ();
				double width = r.ReadDouble();
				double height = r.ReadDouble();
				short id = r.ReadInt16();

				//The viewport 1 shows the paper, the document already has it
				Viewport viewport = id == 1 && header.IsPaperSpace
					? this._document.PaperSpace.Viewports.FirstOrDefault(v => v.RepresentsPaper)
					: null;
				bool isPaper = viewport != null;

				viewport ??= new Viewport();
				viewport.Center = center;
				viewport.Width = width;
				viewport.Height = height;

				if (isPaper)
				{
					this.applyCommonData(viewport, header);
					return null;
				}

				return viewport;
			default:
				this.notify($"Entity {header} not supported", NotificationType.NotImplemented);
				return null;
		}
	}

	private Line readLine(EntityHeader header)
	{
		Line line = new Line();
		if (header.Type == EntityType.Line3D && !header.HasElevationFlag)
		{
			line.StartPoint = this._reader.ReadXYZ();
			line.EndPoint = this._reader.ReadXYZ();
		}
		else
		{
			line.StartPoint = this.readOptional3D(header);
			line.EndPoint = this.readOptional3D(header);
		}

		line.Thickness = header.Thickness;

		if (header.Has(0x01))
		{
			line.Normal = this._reader.ReadXYZ();
		}

		return line;
	}

	private Shape readShape(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		XYZ insertion = this.readPoint(header);
		double size = r.ReadDouble();
		byte number = r.ReadByte();

		double rotation = header.Has(0x01) ? r.ReadDouble() : 0;
		int styleIndex = header.Has(0x02) ? r.ReadByte() : 0;

		Shape shape = new Shape();
		if (this.tryGetEntry(this._textStyles, styleIndex, out TextStyle style) && style.IsShapeFile)
		{
			shape.ShapeStyle = style;
		}
		else
		{
			this.notify($"Shape file {styleIndex} of the shape at {header.Start} not found", NotificationType.Warning);
		}

		shape.InsertionPoint = insertion;
		shape.Size = size;
		shape.ShapeIndex = number;
		shape.Rotation = rotation;
		shape.Thickness = header.Thickness;

		if (header.Has(0x04))
		{
			shape.RelativeXScale = r.ReadDouble();
		}

		if (header.Has(0x08))
		{
			shape.ObliqueAngle = r.ReadDouble();
		}

		return shape;
	}

	private TextEntity readText(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		TextEntity text = new TextEntity();
		text.InsertPoint = this.readPoint(header);
		text.Height = r.ReadDouble();
		text.Value = r.ReadText();
		text.Thickness = header.Thickness;

		if (header.Has(0x01))
		{
			text.Rotation = r.ReadDouble();
		}

		if (header.Has(0x02))
		{
			text.WidthFactor = r.ReadDouble();
		}

		if (header.Has(0x04))
		{
			text.ObliqueAngle = r.ReadDouble();
		}

		if (header.Has(0x08))
		{
			this.setTextStyle(text, r.ReadByte());
		}
		else
		{
			this.setTextStyle(text, 0);
		}

		if (header.Has(0x10))
		{
			text.Mirror = (TextMirrorFlag)r.ReadByte();
		}

		if (header.Has(0x20))
		{
			text.HorizontalAlignment = (TextHorizontalAlignment)r.ReadByte();
		}

		if (header.Has(0x40))
		{
			text.AlignmentPoint = this.readPoint(header);
		}

		if (header.Has(0x80))
		{
			text.Normal = r.ReadXYZ();
		}

		if (header.Has(0x100))
		{
			text.VerticalAlignment = (TextVerticalAlignmentType)r.ReadByte();
		}

		return text;
	}

	private void setTextStyle(TextEntity text, int index)
	{
		if (this.tryGetEntry(this._textStyles, index, out TextStyle style))
		{
			text.Style = style;
		}
	}

	/// <summary>
	/// Reads the data of ATTDEF and ATTRIB, their optional fields are shifted by one bit compared to TEXT.
	/// </summary>
	private void readAttributeData(EntityHeader header, AttributeBase attribute, bool isDefinition)
	{
		PreR13ByteReader r = this._reader;

		attribute.InsertPoint = this.readPoint(header);
		attribute.Height = r.ReadDouble();
		attribute.Value = r.ReadText();

		if (isDefinition)
		{
			((AttributeDefinition)attribute).Prompt = r.ReadText();
		}

		attribute.Tag = r.ReadText();
		attribute.Flags = (AttributeFlags)r.ReadByte();
		attribute.Thickness = header.Thickness;

		if (header.Has(0x02))
		{
			attribute.Rotation = r.ReadDouble();
		}

		if (header.Has(0x04))
		{
			attribute.WidthFactor = r.ReadDouble();
		}

		if (header.Has(0x08))
		{
			attribute.ObliqueAngle = r.ReadDouble();
		}

		this.setTextStyle(attribute, header.Has(0x10) ? r.ReadByte() : 0);

		if (header.Has(0x20))
		{
			attribute.Mirror = (TextMirrorFlag)r.ReadByte();
		}

		if (header.Has(0x40))
		{
			attribute.HorizontalAlignment = (TextHorizontalAlignment)r.ReadByte();
		}

		if (header.Has(0x80))
		{
			attribute.AlignmentPoint = this.readPoint(header);
		}

		if (header.Has(0x100))
		{
			attribute.Normal = r.ReadXYZ();
		}

		if (header.Has(0x200))
		{
			attribute.VerticalAlignment = (TextVerticalAlignmentType)r.ReadByte();
		}
	}

	private void readAttribute(EntityHeader header, ComplexEntity complex)
	{
		if (complex?.Entity is not Insert insert)
		{
			this.notify($"Attribute at {header.Start} without insert", NotificationType.Warning);
			return;
		}

		AttributeEntity attribute = new AttributeEntity();
		this.readAttributeData(header, attribute, false);
		this.checkEntityEnd(header);
		this.applyCommonData(attribute, header);

		insert.Attributes.Add(attribute);
	}

	private void readBlockBegin(EntityHeader header, BlockRecord record)
	{
		XYZ basePoint = this.readPoint(header);

		if (header.Has(0x02))
		{
			string path = this._reader.ReadText();
			if (record != null)
			{
				record.BlockEntity.XRefPath = path;
			}
		}

		if (record == null)
		{
			return;
		}

		record.BlockEntity.BasePoint = basePoint;

		//The block entity carries the layer of the block
		if (this.tryGetEntry(this._layers, header.LayerIndex, out Layer layer))
		{
			record.BlockEntity.Layer = layer;
		}
	}

	private Insert readInsert(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		short blockIndex = r.ReadInt16();
		XYZ insertPoint = this.readPoint(header);

		if (!this.tryGetEntry(this._blocks, blockIndex, out BlockRecord block))
		{
			this.notify($"Block {blockIndex} of the insert at {header.Start} not found", NotificationType.Warning);
			return null;
		}

		//The public constructor creates the attributes from the definitions of the block, the drawing has its own
		Insert insert = new Insert();
		insert.Block = block;
		insert.InsertPoint = insertPoint;

		if (header.Has(0x01))
		{
			insert.XScale = r.ReadDouble();
		}

		if (header.Has(0x02))
		{
			insert.YScale = r.ReadDouble();
		}

		if (header.Has(0x04))
		{
			insert.Rotation = r.ReadDouble();
		}

		if (header.Has(0x08))
		{
			insert.ZScale = r.ReadDouble();
		}

		if (header.Has(0x10))
		{
			insert.ColumnCount = r.ReadUInt16();
		}

		if (header.Has(0x20))
		{
			insert.RowCount = r.ReadUInt16();
		}

		if (header.Has(0x40))
		{
			insert.ColumnSpacing = r.ReadDouble();
		}

		if (header.Has(0x80))
		{
			insert.RowSpacing = r.ReadDouble();
		}

		if (header.Has(0x100))
		{
			insert.Normal = r.ReadXYZ();
		}

		return insert;
	}

	private Entity readPolyline(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		PolylineFlags flags = header.Has(0x01) ? (PolylineFlags)r.ReadByte() : 0;
		double startWidth = header.Has(0x02) ? r.ReadDouble() : 0;
		double endWidth = header.Has(0x04) ? r.ReadDouble() : 0;
		XYZ normal = header.Has(0x08) ? r.ReadXYZ() : XYZ.AxisZ;
		short mCount = header.Has(0x10) ? r.ReadInt16() : (short)0;
		short nCount = header.Has(0x20) ? r.ReadInt16() : (short)0;
		short mDensity = header.Has(0x40) ? r.ReadInt16() : (short)0;
		short nDensity = header.Has(0x80) ? r.ReadInt16() : (short)0;
		short curveType = header.Has(0x100) ? r.ReadInt16() : (short)0;

		//Unknown data of the polylines in the extra entities section
		this._reader.Position = header.Start + header.Size - (this._hasEntityCrc ? 2 : 0);

		Entity polyline;
		if (flags.HasFlag(PolylineFlags.PolyfaceMesh))
		{
			polyline = new PolyfaceMesh();
		}
		else if (flags.HasFlag(PolylineFlags.PolygonMesh))
		{
			PolygonMesh mesh = new PolygonMesh();
			mesh.MVertexCount = mCount;
			mesh.NVertexCount = nCount;
			mesh.MSmoothSurfaceDensity = mDensity;
			mesh.NSmoothSurfaceDensity = nDensity;
			polyline = mesh;
		}
		else if (flags.HasFlag(PolylineFlags.Polyline3D))
		{
			polyline = new Polyline3D();
		}
		else
		{
			Polyline2D polyline2D = new Polyline2D();
			polyline2D.Elevation = header.Elevation;
			polyline = polyline2D;
		}

		switch (polyline)
		{
			case Polyline<Vertex2D> p:
				this.setPolylineData(p, flags, startWidth, endWidth, normal, curveType, header);
				break;
			case Polyline<Vertex3D> p:
				this.setPolylineData(p, flags, startWidth, endWidth, normal, curveType, header);
				break;
			case Polyline<PolygonMeshVertex> p:
				this.setPolylineData(p, flags, startWidth, endWidth, normal, curveType, header);
				break;
			case Polyline<VertexFaceMesh> p:
				this.setPolylineData(p, flags, startWidth, endWidth, normal, curveType, header);
				break;
		}

		return polyline;
	}

	private void setPolylineData<T>(Polyline<T> polyline, PolylineFlags flags, double startWidth, double endWidth, XYZ normal, short curveType, EntityHeader header)
		where T : Entity, IVertex
	{
		polyline.Flags = flags;
		polyline.StartWidth = startWidth;
		polyline.EndWidth = endWidth;
		polyline.Normal = normal;
		polyline.Thickness = header.Thickness;
		polyline.SmoothSurface = (SmoothSurfaceType)curveType;
	}

	private void readVertex(EntityHeader header, ComplexEntity complex)
	{
		PreR13ByteReader r = this._reader;

		if (complex?.Entity is not IPolyline)
		{
			this.notify($"Vertex at {header.Start} without polyline", NotificationType.Warning);
			return;
		}

		//The face records of a polyface mesh have no location
		XYZ location = XYZ.Zero;
		if (!header.Has(0x4000))
		{
			location = this.readPoint(header);
		}

		double startWidth = header.Has(0x01) ? r.ReadDouble() : 0;
		double endWidth = header.Has(0x02) ? r.ReadDouble() : 0;
		double bulge = header.Has(0x04) ? r.ReadDouble() : 0;
		VertexFlags flags = header.Has(0x08) ? (VertexFlags)r.ReadByte() : 0;
		double tangent = header.Has(0x10) ? r.ReadDouble() : 0;
		short[] indexes = new short[4];
		for (int i = 0; i < indexes.Length; i++)
		{
			if (header.Has(0x20 << i))
			{
				indexes[i] = r.ReadInt16();
			}
		}

		this.checkEntityEnd(header);

		Vertex vertex;
		switch (complex.Entity)
		{
			case PolyfaceMesh mesh:
				if (flags.HasFlag(VertexFlags.PolyFaceMeshVertex) && !flags.HasFlag(VertexFlags.PolygonMesh3D) || header.Has(0x4000))
				{
					VertexFaceRecord face = new VertexFaceRecord();
					face.Index1 = indexes[0];
					face.Index2 = indexes[1];
					face.Index3 = indexes[2];
					face.Index4 = indexes[3];
					this.applyCommonData(face, header);
					mesh.Faces.Add(face);
					return;
				}

				VertexFaceMesh faceVertex = new VertexFaceMesh();
				mesh.Vertices.Add(faceVertex);
				vertex = faceVertex;
				break;
			case PolygonMesh polygonMesh:
				PolygonMeshVertex meshVertex = new PolygonMeshVertex();
				polygonMesh.Vertices.Add(meshVertex);
				vertex = meshVertex;
				break;
			case Polyline3D polyline3D:
				Vertex3D vertex3D = new Vertex3D();
				polyline3D.Vertices.Add(vertex3D);
				vertex = vertex3D;
				break;
			case Polyline2D polyline2D:
				Vertex2D vertex2D = new Vertex2D();
				polyline2D.Vertices.Add(vertex2D);
				vertex = vertex2D;
				break;
			default:
				return;
		}

		vertex.Location = location;
		vertex.StartWidth = startWidth;
		vertex.EndWidth = endWidth;
		vertex.Bulge = bulge;
		vertex.Flags = flags;
		vertex.CurveTangent = tangent;
		this.applyCommonData(vertex, header);
	}

	private Dimension readDimension(EntityHeader header)
	{
		PreR13ByteReader r = this._reader;

		short blockIndex = r.ReadInt16();
		XYZ definitionPoint = r.ReadXYZ();
		XYZ textMiddlePoint = this.readPoint(header);
		XYZ insertionPoint = header.Has(0x01) ? this.readPoint(header) : XYZ.Zero;
		DimensionType flags = header.Has(0x02) ? (DimensionType)r.ReadByte() : DimensionType.Linear;
		string text = header.Has(0x04) ? r.ReadText() : string.Empty;
		XYZ point13 = header.Has(0x08) ? r.ReadXYZ() : XYZ.Zero;
		XYZ point14 = header.Has(0x10) ? r.ReadXYZ() : XYZ.Zero;
		XYZ point15 = header.Has(0x20) ? r.ReadXYZ() : XYZ.Zero;
		XYZ point16 = header.Has(0x40) ? (this._version >= ACadVersion.AC1009 ? this.readPoint(header) : r.ReadXYZ()) : XYZ.Zero;
		double leaderLength = header.Has(0x80) ? r.ReadDouble() : 0;
		double rotation = header.Has(0x100) ? r.ReadDouble() : 0;
		double obliqueAngle = header.Has(0x200) ? r.ReadDouble() : 0;
		double textRotation = header.Has(0x400) ? r.ReadDouble() : 0;
		XYZ normal = header.Has(0x4000) ? r.ReadXYZ() : XYZ.AxisZ;
		int styleIndex = header.Has(0x8000) ? r.ReadInt16() : -1;

		Dimension dimension;
		switch ((DimensionType)((int)flags & 0x0F))
		{
			case DimensionType.Aligned:
				dimension = new DimensionAligned
				{
					FirstPoint = point13,
					SecondPoint = point14,
					ExtLineRotation = obliqueAngle,
				};
				break;
			case DimensionType.Angular:
				dimension = new DimensionAngular2Line
				{
					FirstPoint = point13,
					SecondPoint = point14,
					AngleVertex = point15,
					DimensionArc = point16,
				};
				break;
			case DimensionType.Diameter:
				dimension = new DimensionDiameter
				{
					AngleVertex = point15,
					LeaderLength = leaderLength,
				};
				break;
			case DimensionType.Radius:
				dimension = new DimensionRadius
				{
					AngleVertex = point15,
					LeaderLength = leaderLength,
				};
				break;
			case DimensionType.Angular3Point:
				dimension = new DimensionAngular3Pt
				{
					FirstPoint = point13,
					SecondPoint = point14,
					AngleVertex = point15,
				};
				break;
			case DimensionType.Ordinate:
				dimension = new DimensionOrdinate
				{
					FeatureLocation = point13,
					LeaderEndpoint = point14,
				};
				break;
			default:
				dimension = new DimensionLinear
				{
					FirstPoint = point13,
					SecondPoint = point14,
					Rotation = rotation,
					ExtLineRotation = obliqueAngle,
				};
				break;
		}

		dimension.Flags = flags;
		dimension.DefinitionPoint = definitionPoint;
		dimension.TextMiddlePoint = textMiddlePoint;
		dimension.InsertionPoint = insertionPoint;
		dimension.Text = text;
		dimension.TextRotation = textRotation;
		dimension.Normal = normal;

		if (this.tryGetEntry(this._blocks, blockIndex, out BlockRecord block))
		{
			dimension.Block = block;
		}

		if (this.tryGetEntry(this._dimensionStyles, styleIndex, out DimensionStyle style))
		{
			dimension.Style = style;
		}

		return dimension;
	}
}
