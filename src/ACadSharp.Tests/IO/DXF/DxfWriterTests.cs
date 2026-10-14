using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Tests.Common;
using ACadSharp.XData;
using CSMath;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Tests.IO.DXF;

public class DxfWriterTests : IOTestsBase
{
	public DxfWriterTests(ITestOutputHelper output) : base(output) { }

	[Theory]
	[MemberData(nameof(Versions))]
	public void WriteEmptyAsciiTest(ACadVersion version)
	{
		if (version < ACadVersion.AC1015)
			// Not implemented
			return;

		CadDocument doc = new CadDocument();
		doc.Header.Version = version;

		string path = Path.Combine(TestVariables.OutputSamplesFolder, $"out_empty_sample_{version}_ascii.dxf");

		using (var wr = new DxfWriter(path, doc, false))
		{
			wr.OnNotification += this.onNotification;
			wr.Write();
		}

		this._output.WriteLine("Writer successful");

		using (var re = new DxfReader(path, this.onNotification))
		{
			CadDocument readed = re.Read();
		}
	}

	[Theory]
	[MemberData(nameof(Versions))]
	public void WriteEmptyBinaryTest(ACadVersion version)
	{
		if (version < ACadVersion.AC1015)
			// Not implemented
			return;

		CadDocument doc = new CadDocument();
		doc.Header.Version = version;

		string path = Path.Combine(TestVariables.OutputSamplesFolder, $"out_empty_sample_{version}_binary.dxf");

		using (var wr = new DxfWriter(path, doc, true))
		{
			wr.OnNotification += this.onNotification;
			wr.Write();
		}

		this._output.WriteLine("Writer successful");

		using (var re = new DxfReader(path, this.onNotification))
		{
			CadDocument readed = re.Read();
		}
	}

	[Theory]
	[MemberData(nameof(Versions))]
	public void WriteDocumentWithEntitiesTest(ACadVersion version)
	{
		if (version < ACadVersion.AC1015)
			// Not implemented
			return;

		CadDocument doc = new CadDocument();
		doc.Header.Version = version;

		List<Entity> entities = new List<Entity>
		{
			EntityFactory.Create<Point>(),
			EntityFactory.Create<Line>(),
			EntityFactory.Create<Polyline2D>(),
			EntityFactory.Create<Polyline3D>(),
			EntityFactory.Create<Line>(),
			EntityFactory.Create<Arc>(),
			EntityFactory.Create<LwPolyline>(),
		};


		doc.Entities.AddRange(entities);

		string path = Path.Combine(TestVariables.OutputSamplesFolder, $"out_sample_{version}_ascii.dxf");

		using (var wr = new DxfWriter(path, doc, false))
		{
			wr.OnNotification += this.onNotification;
			wr.Write();
		}
	}

	[Fact]
	public void WriteXDataLayerByNameTest()
	{
		CadDocument doc = new CadDocument();
		AppId app = new AppId("MY_APP");
		Layer layer = new Layer("xdata_layer");
		doc.AppIds.Add(app);
		doc.Layers.Add(layer);

		Line line = new Line(XYZ.Zero, new XYZ(10, 10, 0));
		line.ExtendedData.Add(app, new List<ExtendedDataRecord> { new ExtendedDataLayer(layer.Handle) });
		doc.Entities.Add(line);

		byte[] data;
		using (MemoryStream ms = new MemoryStream())
		{
			DxfWriter.Write(ms, doc, false, notification: this.onNotification);
			data = ms.ToArray();
		}

		//Dxf references the layer by name with code 1003, not by handle with code 1005
		string[] lines = Encoding.ASCII.GetString(data).Split('\n').Select(l => l.Trim()).ToArray();
		int appIndex = Enumerable.Range(1, lines.Length - 1).Single(i => lines[i - 1] == "1001" && lines[i] == app.Name);
		Assert.Equal("1003", lines[appIndex + 1]);
		Assert.Equal(layer.Name, lines[appIndex + 2]);

		CadDocument readed = DxfReader.Read(new MemoryStream(data), this.onNotification);
		Line result = readed.GetCadObject<Line>(line.Handle);
		Assert.True(result.ExtendedData.TryGet(app.Name, out ExtendedData edata));
		ExtendedDataLayer record = Assert.IsType<ExtendedDataLayer>(Assert.Single(edata.Records));
		Assert.Equal(layer.Name, record.ResolveReference(readed).Name);
	}

	[Fact]
	public void WriteXDataLayerNotFoundTest()
	{
		CadDocument doc = new CadDocument();
		AppId app = new AppId("MY_APP");
		doc.AppIds.Add(app);

		Line line = new Line(XYZ.Zero, new XYZ(10, 10, 0));
		line.ExtendedData.Add(app, new List<ExtendedDataRecord> { new ExtendedDataLayer(0xFFFFFF) });
		doc.Entities.Add(line);

		CadDocument readed;
		using (MemoryStream ms = new MemoryStream())
		{
			DxfWriter.Write(ms, doc, false, notification: this.onNotification);
			readed = DxfReader.Read(new MemoryStream(ms.ToArray()), this.onNotification);
		}

		//A layer that cannot be resolved is replaced by the default layer, the record is kept
		Line result = readed.GetCadObject<Line>(line.Handle);
		Assert.True(result.ExtendedData.TryGet(app.Name, out ExtendedData edata));
		ExtendedDataLayer record = Assert.IsType<ExtendedDataLayer>(Assert.Single(edata.Records));
		Assert.Equal(Layer.DefaultName, record.ResolveReference(readed).Name);
	}
}
