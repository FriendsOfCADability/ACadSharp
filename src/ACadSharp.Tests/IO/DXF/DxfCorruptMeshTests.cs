using ACadSharp.Entities;
using ACadSharp.IO;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ACadSharp.Tests.IO.DXF;

/// <summary>
/// A MESH whose face size in the file is far larger than its face list. The count was used as array size: 2e9 allocated
/// 8 GB and took 45 s instead of under a second.
/// </summary>
public class DxfCorruptMeshTests
{
	private static string corruptFile()
	{
		string path = Path.Combine(TestVariables.SamplesFolder, "sample_AC1032_ascii.dxf");
		string[] lines = File.ReadAllLines(path);
		// the face list of the first MESH entity: group 93 (the size of the list), then group 90 with the size of the first face
		int entities = Array.FindIndex(lines, l => l.Trim() == "ENTITIES");
		int subclass = Array.FindIndex(lines, entities, l => l.Trim() == "AcDbSubDMesh");
		int list = Array.FindIndex(lines, subclass, l => l.Trim() == "93");
		Assert.True(entities > 0 && subclass > 0 && list > 0, "no MESH in the sample");
		Assert.Equal("90", lines[list + 2].Trim());
		lines[list + 3] = "200000000"; // an int[2e8] is 800 MB
		return string.Join("\n", lines);
	}

	[Fact]
	public void FaceSizeLargerThanTheFaceListIsRejected()
	{
		string content = corruptFile();
		using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
		long before = GC.GetTotalAllocatedBytes(true);
		CadDocument doc;
		using (DxfReader reader = new DxfReader(stream))
		{
			doc = reader.Read();
		}
		long allocated = GC.GetTotalAllocatedBytes(true) - before;
		Assert.True(allocated < 500_000_000, $"reading allocated {allocated} bytes");
		Assert.DoesNotContain(doc.Entities.OfType<Mesh>(), m => m.Faces.Any(f => f.Length > 1000));
	}
}
