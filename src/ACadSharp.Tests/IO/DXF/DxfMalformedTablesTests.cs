using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ACadSharp.Tests.IO.DXF;

/// <summary>
/// Files written by third party exporters may hold tables that AutoCad would never write,
/// the reader is expected to report the anomaly and to keep reading.
/// </summary>
public class DxfMalformedTablesTests
{
	[Fact]
	public void DuplicatedDefaultEntryIsIgnored()
	{
		//The layer '0' is a default entry, it cannot be removed nor replaced by the duplicate.
		string dxf = string.Join("\n",
			header(),
			"0", "SECTION",
			"2", "TABLES",
			"0", "TABLE",
			"2", "LAYER",
			"70", "2",
			"0", "LAYER",
			"2", "0",
			"70", "0",
			"62", "7",
			"6", "CONTINUOUS",
			"0", "LAYER",
			"2", "0",
			"70", "0",
			"62", "1",
			"6", "CONTINUOUS",
			"0", "ENDTAB",
			"0", "ENDSEC",
			"0", "EOF");

		CadDocument doc = read(dxf, out List<string> messages);

		Assert.Single(doc.Layers);
		//The entry that was read first is the one that stays in the table
		Assert.Equal(7, doc.Layers[Layer.DefaultName].Color.Index);
		Assert.Contains(messages, m => m.Contains($"Duplicated entry with name {Layer.DefaultName}"));
	}

	[Fact]
	public void DuplicatedVPortEntriesAreKept()
	{
		//A tiled viewport configuration holds several entries named '*Active'
		string dxf = string.Join("\n",
			header(),
			"0", "SECTION",
			"2", "TABLES",
			"0", "TABLE",
			"2", "VPORT",
			"70", "2",
			"0", "VPORT",
			"5", "20",
			"2", VPort.DefaultName,
			"70", "0",
			"0", "VPORT",
			"5", "21",
			"2", VPort.DefaultName,
			"70", "0",
			"0", "ENDTAB",
			"0", "ENDSEC",
			"0", "EOF");

		CadDocument doc = read(dxf, out _);

		Assert.Equal(2, doc.VPorts.Count);
	}

	[Fact]
	public void MissingTableEntriesAreCreated()
	{
		CadDocument doc = read(undefinedReferences(), out List<string> messages);

		Assert.True(doc.Layers.Contains("Kanten"));
		Assert.True(doc.Layers.Contains("Bemassung"));
		Assert.True(doc.TextStyles.Contains("MONOTXT"));

		Assert.Equal("Kanten", doc.Entities.OfType<Line>().Single().Layer.Name);
		Assert.Equal("MONOTXT", doc.Entities.OfType<TextEntity>().Single().Style.Name);

		Assert.Contains(messages, m => m.Contains("Layer with name Kanten"));
	}

	[Fact]
	public void MissingTableEntriesAreNotCreatedWhenDisabled()
	{
		DxfReaderConfiguration configuration = new DxfReaderConfiguration
		{
			CreateMissingTableEntries = false,
		};

		CadDocument doc = read(undefinedReferences(), out List<string> messages, configuration);

		Assert.False(doc.Layers.Contains("Kanten"));
		Assert.False(doc.TextStyles.Contains("MONOTXT"));
		Assert.Equal(Layer.DefaultName, doc.Entities.OfType<Line>().Single().Layer.Name);

		Assert.Contains(messages, m => m.Contains("not found"));
	}

	private static string header()
	{
		return string.Join("\n",
			"0", "SECTION",
			"2", "HEADER",
			"9", "$ACADVER",
			"1", "AC1009",
			"0", "ENDSEC");
	}

	private static CadDocument read(string dxf, out List<string> messages, DxfReaderConfiguration configuration = null)
	{
		List<string> notifications = new List<string>();

		using (MemoryStream stream = new MemoryStream(Encoding.ASCII.GetBytes(dxf)))
		using (DxfReader reader = new DxfReader(stream))
		{
			if (configuration != null)
			{
				reader.Configuration = configuration;
			}

			reader.OnNotification += (sender, e) => notifications.Add(e.Message);

			CadDocument doc = reader.Read();

			messages = notifications;
			return doc;
		}
	}

	/// <summary>
	/// The layers 'Kanten' and 'Bemassung' and the text style 'MONOTXT' are used by the
	/// entities but the tables do not define them.
	/// </summary>
	private static string undefinedReferences()
	{
		return string.Join("\n",
			header(),
			"0", "SECTION",
			"2", "TABLES",
			"0", "TABLE",
			"2", "LAYER",
			"70", "1",
			"0", "LAYER",
			"2", "0",
			"70", "0",
			"62", "7",
			"6", "CONTINUOUS",
			"0", "ENDTAB",
			"0", "ENDSEC",
			"0", "SECTION",
			"2", "ENTITIES",
			"0", "LINE",
			"5", "1F",
			"8", "Kanten",
			"10", "0.0", "20", "0.0", "30", "0.0",
			"11", "10.0", "21", "10.0", "31", "0.0",
			"0", "TEXT",
			"5", "20",
			"8", "Bemassung",
			"10", "0.0", "20", "0.0", "30", "0.0",
			"40", "2.5",
			"1", "R170,3",
			"7", "MONOTXT",
			"0", "ENDSEC",
			"0", "EOF");
	}
}
