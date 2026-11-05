using ACadSharp.Exceptions;
using System;
using System.IO;

namespace ACadSharp.IO.DWG;

// Variation of the algorithm LZ77 used in 2004 DWG files.
internal static class DwgLZ77AC18Decompressor
{
	public static Stream Decompress(Stream compressed, long decompressedSize)
	{
		//Create a new stream
		MemoryStream memoryStream = HugeMemoryStream.Create(decompressedSize);

		//Decompress the stream
		DecompressToDest(compressed, memoryStream);
		memoryStream.Position = 0L;

		return memoryStream;
	}

	public static void DecompressToDest(Stream src, Stream dst)
	{
		var tempBuf = new byte[128];
		int opcode1 = readByte(src);

		if (opcode1 > 0x11)
		{
			opcode1 = copy(opcode1 - 17, src, dst, ref tempBuf);
		}

		if ((opcode1 & 0xF0) == 0)
		{
			opcode1 = copy(literalCount(opcode1, src) + 3, src, dst, ref tempBuf);
		}

		//0x11 : Terminates the input stream.
		while (opcode1 != 0x11)
		{
			//0x00 – 0x0F : Not used, because this would be mistaken for a Literal Length in some situations.

			//Offset backwards from the current location in the decompressed data stream, where the “compressed” bytes should be copied from.
			int compOffset = 0;
			//Number of “compressed” bytes that are to be copied to this location from a previous location in the uncompressed data stream.
			int compressedBytes = 0;

			if (opcode1 < 0x10 || opcode1 >= 0x40)
			{
				compressedBytes = (opcode1 >> 4) - 1;
				//Read the next byte(call it opcode2):
				byte opcode2 = readByte(src);
				compOffset = ((opcode1 >> 2 & 3) | (opcode2 << 2)) + 1;
			}
			//0x12 – 0x1F
			else if (opcode1 < 0x20)
			{
				compressedBytes = readCompressedBytes(opcode1, 0b0111, src);
				compOffset = (opcode1 & 8) << 11;
				opcode1 = twoByteOffset(ref compOffset, 0x4000, src);
			}
			//0x20
			else if (opcode1 >= 0x20)
			{
				compressedBytes = readCompressedBytes(opcode1, 0b00011111, src);
				opcode1 = twoByteOffset(ref compOffset, 1, src);
			}

			long position = dst.Position;
			// a back reference must point into the data decompressed so far; with an offset of 0 the copy below never ended
			if (compOffset <= 0 || compOffset > position)
			{
				throw new DwgException($"Invalid back reference in compressed data: offset {compOffset} at {position}");
			}
			if (tempBuf.Length < compressedBytes)
			{
				tempBuf = new byte[compressedBytes];
			}
			dst.Position = position - compOffset;
			_ = dst.Read(tempBuf, 0, Math.Min(compressedBytes, compOffset));
			dst.Position = position;
			while (compressedBytes > 0)
			{
				dst.Write(tempBuf, 0, Math.Min(compressedBytes, compOffset));
				compressedBytes -= compOffset;
			}

			//Number of uncompressed or literal bytes to be copied from the input stream, following the addition of the compressed bytes.
			int litCount = opcode1 & 3;
			//0x00 : litCount is read as the next Literal Length (see format below)
			if (litCount == 0)
			{
				opcode1 = readByte(src);
				if ((opcode1 & 0b11110000) == 0)
				{
					litCount = literalCount(opcode1, src) + 3;
				}
			}

			//Copy as literal
			if (litCount > 0U)
			{
				opcode1 = copy(litCount, src, dst, ref tempBuf);
			}
		}
	}
	
	private static byte copy(int count, Stream src, Stream dst, ref byte[] tempBuf)
	{
		if (tempBuf.Length < count)
		{
			tempBuf = new byte[count];
		}

		int read = 0;
		while (read < count)
		{
			int n = src.Read(tempBuf, read, count - read);
			if (n <= 0)
			{
				throw new DwgException("Unexpected end of compressed data");
			}
			read += n;
		}
		dst.Write(tempBuf, 0, count);

		return readByte(src);
	}

	/// <summary>
	/// The next byte of the compressed data. The end of the stream used to be read as 0xFF and decoded on, which could
	/// produce a back reference with offset 0, an endless loop.
	/// </summary>
	private static byte readByte(Stream src)
	{
		int b = src.ReadByte();
		if (b < 0)
		{
			throw new DwgException("Unexpected end of compressed data");
		}
		return (byte)b;
	}

	private static int literalCount(int code, Stream src)
	{
		int lowbits = code & 0b1111;
		//0x00 : Set the running total to 0x0F, and read the next byte. From this point on, a 0x00 byte adds 0xFF to the running total, and a non-zero byte adds that value to the running total and terminates the process. Add 3 to the final result.
		if (lowbits == 0)
		{
			byte lastByte;
			for (lastByte = readByte(src); lastByte == 0; lastByte = readByte(src))
			{
				lowbits += byte.MaxValue;  //0xFF
			}

			lowbits += 0xF + lastByte;
		}
		return lowbits;
	}

	private static int readCompressedBytes(int opcode1, int validBits, Stream compressed)
	{
		int compressedBytes = opcode1 & validBits;

		if (compressedBytes == 0)
		{
			byte lastByte;

			for (lastByte = readByte(compressed); lastByte == 0; lastByte = readByte(compressed))
			{
				compressedBytes += byte.MaxValue;
			}

			compressedBytes += lastByte + validBits;
		}

		return compressedBytes + 2;
	}

	private static int twoByteOffset(ref int offset, int addedValue, Stream stream)
	{
		int firstByte = readByte(stream);

		offset |= firstByte >> 2;
		offset |= readByte(stream) << 6;
		offset += addedValue;

		return firstByte;
	}
}
