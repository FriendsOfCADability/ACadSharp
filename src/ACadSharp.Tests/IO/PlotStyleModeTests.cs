using ACadSharp.Header;
using ACadSharp.IO;
using System.IO;
using Xunit;

namespace ACadSharp.Tests.IO;

/// <summary>
/// The plot style mode (PSTYLEMODE) has to survive writing and reading a drawing.
/// </summary>
public class PlotStyleModeTests
{
	[Theory]
	[InlineData(ACadVersion.AC1015, PlotStyleMode.ColorDependent)]
	[InlineData(ACadVersion.AC1018, PlotStyleMode.ColorDependent)]
	[InlineData(ACadVersion.AC1032, PlotStyleMode.ColorDependent)]
	[InlineData(ACadVersion.AC1032, PlotStyleMode.Named)]
	public void DwgRoundtrip(ACadVersion version, PlotStyleMode mode)
	{
		CadDocument doc = new CadDocument(version);
		doc.Header.PlotStyleMode = mode;

		using MemoryStream stream = new MemoryStream();
		using (DwgWriter writer = new DwgWriter(stream, doc))
		{
			writer.Write();
		}

		// The writer closes the stream.
		CadDocument result = DwgReader.Read(new MemoryStream(stream.ToArray()));

		Assert.Equal(mode, result.Header.PlotStyleMode);
	}

	[Theory]
	[InlineData(PlotStyleMode.ColorDependent)]
	[InlineData(PlotStyleMode.Named)]
	public void DxfRoundtrip(PlotStyleMode mode)
	{
		CadDocument doc = new CadDocument(ACadVersion.AC1032);
		doc.Header.PlotStyleMode = mode;

		using MemoryStream stream = new MemoryStream();
		using (DxfWriter writer = new DxfWriter(stream, doc, false))
		{
			// $PSTYLEMODE is not in the default list of header variables.
			writer.Configuration.WriteAllHeaderVariables = true;
			writer.Write();
		}

		CadDocument result = DxfReader.Read(new MemoryStream(stream.ToArray()));

		Assert.Equal(mode, result.Header.PlotStyleMode);
	}
}
