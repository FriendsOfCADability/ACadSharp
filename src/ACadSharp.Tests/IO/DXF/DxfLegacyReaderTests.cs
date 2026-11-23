using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ACadSharp.Tests.IO.DXF;

/// <summary>
/// Values that the DXF reader has to keep for R12 files, files without a header section and
/// entities whose group codes belong to a base subclass.
/// </summary>
/// <remarks>
/// Pre R13 files and files without a header (version unknown) have no subclass markers, so the reader
/// has to look up a group code in every subclass of the entity, not only in the last one.
/// Many CAM and laser programs write DXF files like that.
/// </remarks>
public class DxfLegacyReaderTests
{
	private const double Precision = 1e-6;

	private static string samplePath(string name)
	{
		return Path.Combine(TestVariables.SamplesFolder, name);
	}

	private static CadDocument readText(string dxf)
	{
		using MemoryStream stream = new MemoryStream(Encoding.ASCII.GetBytes(dxf));
		using DxfReader reader = new DxfReader(stream);
		return reader.Read();
	}

	private static string dxf(params object[] pairs)
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < pairs.Length; i += 2)
		{
			sb.Append(pairs[i]).Append('\n');
			sb.Append(System.Convert.ToString(pairs[i + 1], System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
		}
		return sb.ToString();
	}

	private static void assertEqual(XYZ expected, XYZ actual)
	{
		Assert.Equal(expected.X, actual.X, Precision);
		Assert.Equal(expected.Y, actual.Y, Precision);
		Assert.Equal(expected.Z, actual.Z, Precision);
	}

	[Theory]
	[InlineData("sample_AC1009_ascii.dxf")]
	[InlineData("sample_AC1009_binary.dxf")]
	public void ArcCenterR12(string file)
	{
		CadDocument doc = DxfReader.Read(samplePath(file));

		List<Arc> arcs = doc.ModelSpace.Entities.OfType<Arc>()
			.Concat(doc.BlockRecords.SelectMany(b => b.Entities).OfType<Arc>())
			.Distinct()
			.ToList();

		// The handles differ between the ascii and the binary file, the radius identifies the arcs.
		assertArc(arcs, 3.044494391, new XYZ(56.35179242595231, 4.697601732518876, 0));
		assertArc(arcs, 34.6699266, new XYZ(438.5833751112772, 14.34058110082566, 0));
		assertArc(arcs, 33.94117108, new XYZ(495.5803833400653, 14.34058110082563, 0));
	}

	private static void assertArc(List<Arc> arcs, double radius, XYZ center)
	{
		Arc arc = Assert.Single(arcs, a => System.Math.Abs(a.Radius - radius) < Precision);
		assertEqual(center, arc.Center);
	}

	[Theory]
	[InlineData("sample_AC1009_ascii.dxf")]
	[InlineData("sample_AC1009_binary.dxf")]
	public void LinearDimensionR12(string file)
	{
		CadDocument doc = DxfReader.Read(samplePath(file));

		DimensionLinear dimension = doc.GetCadObject<DimensionLinear>(0x514);

		Assert.NotNull(dimension);
		assertEqual(new XYZ(330.2890594765795, 2.941179455067986, 0), dimension.FirstPoint);
		assertEqual(new XYZ(364.4872644138525, 37.13938439234094, 0), dimension.SecondPoint);
		Assert.Equal(46.71561671, dimension.Measurement, Precision);
	}

	[Fact]
	public void ArcWithoutHeaderR12Style()
	{
		CadDocument doc = readText(dxf(
			0, "SECTION", 2, "ENTITIES",
			0, "ARC", 8, "0", 10, 12.5, 20, -3.25, 30, 0.0, 39, 2.0, 40, 4.0, 50, 0.0, 51, 90.0,
			0, "ENDSEC", 0, "EOF"));

		Arc arc = doc.Entities.OfType<Arc>().Single();

		assertEqual(new XYZ(12.5, -3.25, 0), arc.Center);
		Assert.Equal(2.0, arc.Thickness, Precision);
		Assert.Equal(4.0, arc.Radius, Precision);
		Assert.Equal(System.Math.PI / 2, arc.EndAngle, Precision);
	}

	[Fact]
	public void ArcWithoutHeaderWithSubclassMarkers()
	{
		CadDocument doc = readText(dxf(
			0, "SECTION", 2, "ENTITIES",
			0, "ARC", 100, "AcDbEntity", 8, "0",
			100, "AcDbCircle", 10, 12.5, 20, -3.25, 30, 0.0, 40, 4.0,
			100, "AcDbArc", 50, 0.0, 51, 90.0,
			0, "ENDSEC", 0, "EOF"));

		Arc arc = doc.Entities.OfType<Arc>().Single();

		assertEqual(new XYZ(12.5, -3.25, 0), arc.Center);
		Assert.Equal(4.0, arc.Radius, Precision);
	}

	[Fact]
	public void MeshWithProxyGraphicsWithoutHeader()
	{
		// Group code 92 is the size of the proxy graphics in AcDbEntity and the vertex count in AcDbSubDMesh.
		CadDocument doc = readText(dxf(
			0, "SECTION", 2, "ENTITIES",
			0, "MESH", 100, "AcDbEntity", 8, "0",
			92, 4, 310, "00000000",
			100, "AcDbSubDMesh", 71, 2, 72, 0, 91, 0,
			92, 3, 10, 0.0, 20, 0.0, 30, 0.0, 10, 1.0, 20, 0.0, 30, 0.0, 10, 0.0, 20, 1.0, 30, 0.0,
			93, 4, 90, 3, 90, 0, 90, 1, 90, 2,
			94, 0, 95, 0, 90, 0,
			0, "ENDSEC", 0, "EOF"));

		Mesh mesh = doc.Entities.OfType<Mesh>().Single();

		Assert.Equal(3, mesh.Vertices.Count);
		assertEqual(new XYZ(1, 0, 0), mesh.Vertices[1]);
		Assert.Single(mesh.Faces);
	}

	[Theory]
	[InlineData("sample_AC1015_ascii.dxf")]
	[InlineData("sample_AC1015_binary.dxf")]
	[InlineData("sample_AC1018_ascii.dxf")]
	[InlineData("sample_AC1024_ascii.dxf")]
	[InlineData("sample_AC1032_ascii.dxf")]
	[InlineData("sample_AC1032_binary.dxf")]
	public void TableEntityScale(string file)
	{
		CadDocument doc = DxfReader.Read(samplePath(file));

		TableEntity table = doc.GetCadObject<TableEntity>(0x528);

		Assert.NotNull(table);
		Assert.Equal(0.03419820494, table.XScale, Precision);
		Assert.Equal(1, table.YScale, Precision);
	}
}
