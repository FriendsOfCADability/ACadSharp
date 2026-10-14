using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Tests.TestModels;
using ACadSharp.XData;
using CSMath;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Tests.IO.DWG;

/// <summary>
/// Drawings older than R13: R10 (AC1006) and R11/R12 (AC1009).
/// </summary>
/// <remarks>
/// The samples come from https://github.com/michal-josef-spacek/CAD-Format-DWG-AC1006 and
/// https://github.com/michal-josef-spacek/CAD-Format-DWG-AC1009, see samples/pre_r13/README.md.
/// </remarks>
public class DwgPreR13ReaderTests
{
	public static TheoryData<FileModel> PreR13Files { get; } = new();

	private readonly ITestOutputHelper _output;

	static DwgPreR13ReaderTests()
	{
		foreach (string file in Directory.GetFiles(samplesFolder, "*.dwg", SearchOption.AllDirectories).OrderBy(f => f))
		{
			PreR13Files.Add(new FileModel(file));
		}
	}

	public DwgPreR13ReaderTests(ITestOutputHelper output)
	{
		this._output = output;
	}

	private static string samplesFolder { get { return Path.Combine(TestVariables.SamplesFolder, "pre_r13"); } }

	[Theory]
	[MemberData(nameof(PreR13Files))]
	public void ReadWithoutNotifications(FileModel test)
	{
		List<NotificationEventArgs> notifications = new();
		DwgReaderConfiguration configuration = new DwgReaderConfiguration { Failsafe = false };

		CadDocument doc = DwgReader.Read(test.Path, configuration, (sender, e) =>
		{
			this._output.WriteLine($"[{e.NotificationType}] {e.Message}");
			notifications.Add(e);
		});

		ACadVersion expected = Path.GetFileName(test.Folder) == "AC1006" ? ACadVersion.AC1006 : ACadVersion.AC1009;
		Assert.Equal(expected, doc.Header.Version);

		//The reader reports the entities whose fields do not match their size
		Assert.Empty(notifications);
	}

	[Theory]
	[MemberData(nameof(PreR13Files))]
	public void ReadHeader(FileModel test)
	{
		using DwgReader reader = new DwgReader(test.Path);

		CadHeader header = reader.ReadHeader();

		Assert.NotNull(header);
		Assert.Equal(1.0, header.LineTypeScale);
		Assert.Null(reader.ReadPreview());
		Assert.NotNull(reader.ReadSummaryInfo());
	}

	[Fact]
	public void ArcR11()
	{
		Arc arc = this.readModelEntities("AC1009", "arc.dwg").OfType<Arc>().Single();

		Assert.Equal(new XYZ(5, 5, 0), arc.Center);
		Assert.Equal(1, arc.Radius);
		Assert.Equal(1.5 * Math.PI, arc.StartAngle, 6);
		Assert.Equal(0, arc.EndAngle, 6);
	}

	[Fact]
	public void CircleElevationThicknessR11()
	{
		Circle circle = this.readModelEntities("AC1009", "circle_elevation_thickness.dwg").OfType<Circle>().Single();

		Assert.Equal(new XYZ(1, 1, 10), circle.Center);
		Assert.Equal(3, circle.Radius);
		Assert.Equal(2, circle.Thickness);
	}

	[Fact]
	public void CircleExtendedDataR11()
	{
		CadDocument doc = this.read("AC1009", "circle_eed.dwg");
		Circle circle = doc.ModelSpace.Entities.OfType<Circle>().Single();

		Assert.Equal(new XYZ(1, 1, 10), circle.Center);

		Assert.Single(circle.ExtendedData);
		List<ExtendedDataRecord> records = circle.ExtendedData.Single().Value.Records;
		Assert.Equal((short)1, Assert.IsType<ExtendedDataInteger16>(records[0]).Value);
		Assert.Equal(50, Assert.IsType<ExtendedDataInteger32>(records[1]).Value);
	}

	[Fact]
	public void CodePage()
	{
		Assert.Equal("ANSI_1250", this.read("AC1009", "line_2d_handle.dwg").Header.CodePage);
		Assert.Equal("ANSI_1252", this.read("AC1009", "line_linetype_eed.dwg").Header.CodePage);
		//R10 does not store a code page
		Assert.Equal("DOS437", this.read("AC1006", "line.dwg").Header.CodePage);
	}

	[Fact]
	public void DimensionR10()
	{
		CadDocument doc = this.read("AC1006", "dimension.dwg");

		DimensionLinear dimension = doc.ModelSpace.Entities.OfType<DimensionLinear>().Single();
		Assert.Equal(new XYZ(0, 0, 0), dimension.FirstPoint);
		Assert.Equal(new XYZ(1, 1, 1), dimension.SecondPoint);
		Assert.NotNull(dimension.Block);
		Assert.True(dimension.Block.IsAnonymous);

		//The block holds the geometry of the dimension
		Assert.Equal(4, dimension.Block.Entities.OfType<Line>().Count());
		Assert.Equal(2, dimension.Block.Entities.OfType<Solid>().Count());
		Assert.Equal("1.0000", dimension.Block.Entities.OfType<TextEntity>().Single().Value);
		Assert.Equal(3, dimension.Block.Entities.OfType<Point>().Count(p => p.Layer.Name == "DEFPOINTS"));

		//BYBLOCK color and line type
		Line line = dimension.Block.Entities.OfType<Line>().First();
		Assert.True(line.Color.IsByBlock);
		Assert.Equal(LineType.ByBlockName, line.LineType.Name, ignoreCase: true);
	}

	[Fact]
	public void DimensionAlignedR11()
	{
		DimensionAligned dimension = this.readModelEntities("AC1009", "dimension_aligned.dwg").OfType<DimensionAligned>().Single();

		Assert.Equal(new XYZ(8, 8, 0), dimension.DefinitionPoint);
		Assert.Equal(new XYZ(7.5, 8.5, 0), dimension.TextMiddlePoint);
		Assert.Equal(new XYZ(6, 8, 0), dimension.FirstPoint);
		Assert.Equal(new XYZ(7, 7, 0), dimension.SecondPoint);
		Assert.Equal("1.4142", dimension.Block.Entities.OfType<TextEntity>().Single().Value);
	}

	[Theory]
	[InlineData("AC1006")]
	[InlineData("AC1009")]
	public void Face3D(string version)
	{
		Face3D face = this.readModelEntities(version, "face3d.dwg").OfType<Face3D>().Single();

		Assert.Equal(new XYZ(0, 0, 0), face.FirstCorner);
		Assert.Equal(new XYZ(1, 1, 0), face.SecondCorner);
		Assert.Equal(new XYZ(2, 2, 0), face.ThirdCorner);
		Assert.Equal(new XYZ(3, 3, 0), face.FourthCorner);
	}

	[Fact]
	public void InsertR10()
	{
		CadDocument doc = this.read("AC1006", "insert_elevation_thickness.dwg");

		Insert insert = doc.ModelSpace.Entities.OfType<Insert>().Single();
		Assert.Equal("BLOCK", insert.Block.Name);
		Assert.Equal(new XYZ(2, 2, 1), insert.InsertPoint);
		Assert.Equal(0.5, insert.XScale);
		Assert.Equal(0.5, insert.YScale);
		Assert.Equal(0.5, insert.ZScale);
		Assert.Equal(Math.PI / 6, insert.Rotation, 6);

		Line line = insert.Block.Entities.OfType<Line>().Single();
		Assert.Equal(2, line.Thickness);
	}

	[Fact]
	public void InsertR11()
	{
		CadDocument doc = this.read("AC1009", "insert.dwg");

		Insert insert = doc.ModelSpace.Entities.OfType<Insert>().Single();
		Assert.Equal("BLOCK1", insert.Block.Name);
		Assert.Equal(new XYZ(6, 1, 3), insert.InsertPoint);
		Assert.Empty(insert.Attributes);

		Line line = insert.Block.Entities.OfType<Line>().Single();
		Assert.Equal(new XYZ(0, 0, -1), line.StartPoint);
		Assert.Equal(new XYZ(1, 1, 0), line.EndPoint);
	}

	[Fact]
	public void JumpToExtraEntitiesR10()
	{
		Polyline2D polyline = this.readModelEntities("AC1006", "jump.dwg").OfType<Polyline2D>().Single();

		Assert.True(polyline.IsClosed);
		Assert.Equal(
			new[] { new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(2, 2, 0), new XYZ(3, 4, 0), new XYZ(1, 2, 0) },
			polyline.Vertices.Select(v => v.Location));
	}

	[Fact]
	public void JumpToExtraEntitiesR11()
	{
		//The polyline was moved to the extra entities section, its vertices stayed in the entities section
		Polyline2D polyline = this.readModelEntities("AC1009", "jump.dwg").OfType<Polyline2D>().Single();

		Assert.True(polyline.IsClosed);
		Assert.Equal(
			new[] { new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(1, 0, 0) },
			polyline.Vertices.Select(v => v.Location));
	}

	[Fact]
	public void Line3DR10()
	{
		Line line = this.readModelEntities("AC1006", "line3d.dwg").OfType<Line>().Single();

		Assert.Equal(new XYZ(0, 0, 0), line.StartPoint);
		Assert.Equal(new XYZ(1, 1, 1), line.EndPoint);
	}

	[Fact]
	public void LineTypeR11()
	{
		Line line = this.readModelEntities("AC1009", "line_linetype_eed.dwg").OfType<Line>().Single();

		Assert.Equal("DOT", line.LineType.Name);
		Assert.NotEmpty(line.LineType.Segments);
	}

	[Fact]
	public void PaperSpaceR11()
	{
		CadDocument doc = this.read("AC1009", "paper_space.dwg");

		Assert.Empty(doc.ModelSpace.Entities);

		Line line = doc.PaperSpace.Entities.OfType<Line>().Single();
		Assert.Equal(152.105353, line.StartPoint.X, 6);
		Assert.Equal(121.454185, line.StartPoint.Y, 6);

		//The paper viewport of the drawing replaces the one of the document
		Assert.Single(doc.PaperSpace.Entities.OfType<Viewport>());
		Assert.False(doc.Header.ShowModelSpace);
	}

	[Fact]
	public void PointR11()
	{
		Point point = this.readModelEntities("AC1009", "point.dwg").OfType<Point>().Single();

		Assert.Equal(new XYZ(1, 2, 3), point.Location);
	}

	[Fact]
	public void PolylineSplineR11()
	{
		Polyline2D polyline = this.readModelEntities("AC1009", "polyline_spline.dwg").OfType<Polyline2D>().Single();

		Assert.True(polyline.Flags.HasFlag(PolylineFlags.SplineFit));
		Assert.Equal(12, polyline.Vertices.Count);
		Assert.Contains(polyline.Vertices, v => v.Flags.HasFlag(VertexFlags.SplineFrameControlPoint));
	}

	[Theory]
	[InlineData("AC1006")]
	[InlineData("AC1009")]
	public void Shape(string version)
	{
		CadDocument doc = this.read(version, "shape.dwg");

		Shape shape = doc.ModelSpace.Entities.OfType<Shape>().Single();
		Assert.Equal(1, shape.Size);
		Assert.NotNull(shape.ShapeStyle);
		Assert.True(shape.ShapeStyle.IsShapeFile);
	}

	[Fact]
	public void SolidR10()
	{
		Solid solid = this.readModelEntities("AC1006", "solid.dwg").OfType<Solid>().Single();

		Assert.Equal(new XYZ(0, 0, 0), solid.FirstCorner);
		Assert.Equal(new XYZ(0, 1, 0), solid.SecondCorner);
		Assert.Equal(new XYZ(1, 1, 0), solid.ThirdCorner);
		Assert.Equal(new XYZ(1, 0, 0), solid.FourthCorner);
	}

	[Theory]
	[InlineData("AC1006", 1, 1, 0.2, TextHorizontalAlignment.Left)]
	[InlineData("AC1009", 1, 4, 0.75, TextHorizontalAlignment.Aligned)]
	public void Text(string version, double x, double y, double height, TextHorizontalAlignment alignment)
	{
		TextEntity text = this.readModelEntities(version, "text.dwg").OfType<TextEntity>().Single();

		Assert.Equal("FOO", text.Value);
		Assert.Equal(new XYZ(x, y, 0), text.InsertPoint);
		Assert.Equal(height, text.Height);
		Assert.Equal(alignment, text.HorizontalAlignment);
		Assert.Equal(TextStyle.DefaultName, text.Style.Name, ignoreCase: true);
	}

	[Fact]
	public void TruncatedDrawingFailsafe()
	{
		byte[] data = File.ReadAllBytes(Path.Combine(samplesFolder, "AC1009", "dimension_aligned.dwg"));

		//Cut inside the blocks section, its start is stored at 0x1C
		int blocksStart = BitConverter.ToInt32(data, 0x1C);
		byte[] truncated = data.Take(blocksStart + 40).ToArray();

		List<NotificationEventArgs> notifications = new();
		using MemoryStream stream = new MemoryStream(truncated);
		CadDocument doc = DwgReader.Read(stream, (sender, e) => notifications.Add(e));

		Assert.NotNull(doc);
		Assert.NotEmpty(notifications);
	}

	private CadDocument read(string version, string file)
	{
		DwgReaderConfiguration configuration = new DwgReaderConfiguration { Failsafe = false };
		return DwgReader.Read(Path.Combine(samplesFolder, version, file), configuration, (sender, e) =>
		{
			this._output.WriteLine($"[{e.NotificationType}] {e.Message}");
		});
	}

	private IEnumerable<Entity> readModelEntities(string version, string file)
	{
		return this.read(version, file).ModelSpace.Entities;
	}
}
