using ACadSharp.Exceptions;
using ACadSharp.IO;
using ACadSharp.IO.DWG;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ACadSharp.Tests.IO.DWG;

/// <summary>
/// Truncated and corrupted DWG files of R2004 and later must be read or rejected with an exception in reasonable time. The
/// sizes, offsets and counts of the section maps came from the file unchecked, and the LZ77 decompressor read the end of the
/// stream as 0xFF: a truncated file could make the reader spin for ever or allocate gigabytes.
/// </summary>
public class DwgCorruptFileTests
{
	public static IEnumerable<object[]> Files()
	{
		foreach (string version in new[] { "AC1018", "AC1021", "AC1024", "AC1027", "AC1032" })
		{
			yield return new object[] { version };
		}
	}

	private static void assertFinishes(byte[] data, string what)
	{
		Task read = Task.Run(() =>
		{
			try
			{
				using DwgReader reader = new DwgReader(new MemoryStream(data));
				reader.Read();
			}
			catch (Exception)
			{
				// rejecting the file is fine
			}
		});
		Assert.True(read.Wait(TimeSpan.FromSeconds(20)), $"reading {what} did not finish");
	}

	[Theory]
	[MemberData(nameof(Files))]
	public void TruncatedFilesFinish(string version)
	{
		byte[] full = File.ReadAllBytes(Path.Combine(TestVariables.SamplesFolder, $"sample_{version}.dwg"));
		for (int i = 1; i < 60; i++)
		{
			int length = (int)((long)full.Length * i / 60);
			byte[] truncated = new byte[length];
			Array.Copy(full, truncated, length);
			assertFinishes(truncated, $"{version} truncated to {length} of {full.Length} bytes");
		}
	}

	[Theory]
	[MemberData(nameof(Files))]
	public void CorruptedFilesFinish(string version)
	{
		byte[] full = File.ReadAllBytes(Path.Combine(TestVariables.SamplesFolder, $"sample_{version}.dwg"));
		Random random = new Random(17);
		for (int i = 0; i < 40; i++)
		{
			byte[] corrupt = (byte[])full.Clone();
			// a few bytes in the first part of the file, where the headers and maps are
			for (int k = 0; k < 4; k++)
			{
				corrupt[0x80 + random.Next(Math.Min(full.Length, 0x8000) - 0x80)] = (byte)random.Next(256);
			}
			assertFinishes(corrupt, $"{version} corruption {i}");
		}
	}

	[Theory]
	[InlineData(new byte[] { 0x14, 1, 2, 3, 0x21 })] // a back reference whose two offset bytes are missing
	[InlineData(new byte[] { 0x14, 1, 2, 3, 0x20, 0x05 })] // the same with an extended length
	[InlineData(new byte[] { 0x14, 1, 2, 3, 0x21, 0xFC })] // only one offset byte
	[InlineData(new byte[] { 0x14, 1, 2 })] // a literal run that ends early
	[InlineData(new byte[] { 0x14, 1, 2, 3, 0x40, 0x10 })] // a back reference before the start of the data
	public void TruncatedCompressedDataThrows(byte[] compressed)
	{
		// the end of the stream was read as 0xFF; after an opcode 0x20 to 0x3F that gave the offset 0, and the copy of the
		// back reference never ended
		Task<Exception> decompress = Task.Run(() => Record.Exception(() => DwgLZ77AC18Decompressor.Decompress(new MemoryStream(compressed), 1000)));
		Assert.True(decompress.Wait(TimeSpan.FromSeconds(20)), "the decompression did not finish");
		Assert.IsType<DwgException>(decompress.Result);
	}

	[Fact]
	public void CompressedDataRoundTrips()
	{
		Random random = new Random(5);
		byte[] data = new byte[0x7400];
		for (int i = 0; i < data.Length; i++)
		{
			// runs and repetitions, so that the compressor writes back references of all lengths
			data[i] = i % 700 < 300 ? (byte)(i % 13) : (byte)random.Next(4);
		}
		MemoryStream compressed = new MemoryStream();
		new DwgLZ77AC18Compressor().Compress(data, 0, data.Length, compressed);
		compressed.Position = 0;
		Stream decompressed = DwgLZ77AC18Decompressor.Decompress(compressed, data.Length);
		byte[] result = new byte[data.Length];
		Assert.Equal(data.Length, decompressed.Read(result, 0, result.Length));
		Assert.Equal(data, result);
	}
}
