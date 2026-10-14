using ACadSharp.Exceptions;
using CSMath;
using System;
using System.Text;

namespace ACadSharp.IO.DWG.PreR13;

/// <summary>
/// Little endian reader over the whole content of a pre-R13 drawing.
/// </summary>
/// <remarks>
/// Drawings older than R13 are byte aligned and small, they are read into memory and accessed by position.
/// </remarks>
internal class PreR13ByteReader
{
	public Encoding Encoding { get; set; }

	public int Length { get { return this._data.Length; } }

	public int Position { get; set; }

	private readonly byte[] _data;

	public PreR13ByteReader(byte[] data, Encoding encoding)
	{
		this._data = data;
		this.Encoding = encoding;
	}

	public bool CanRead(int count)
	{
		return this.Position >= 0 && count >= 0 && this.Position + count <= this._data.Length;
	}

	public byte ReadByte()
	{
		this.ensure(1);
		return this._data[this.Position++];
	}

	public byte[] ReadBytes(int count)
	{
		this.ensure(count);
		byte[] result = new byte[count];
		Array.Copy(this._data, this.Position, result, 0, count);
		this.Position += count;
		return result;
	}

	public double ReadDouble()
	{
		this.ensure(8);
		double value = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(this._data, this.Position));
		this.Position += 8;
		return value;
	}

	/// <summary>
	/// Reads a string stored in a field of fixed size, the text ends at the first null character.
	/// </summary>
	public string ReadFixedString(int size)
	{
		this.ensure(size);
		int length = 0;
		while (length < size && this._data[this.Position + length] != 0)
		{
			length++;
		}

		string value = this.Encoding.GetString(this._data, this.Position, length);
		this.Position += size;
		return value;
	}

	public short ReadInt16()
	{
		this.ensure(2);
		short value = BitConverter.ToInt16(this._data, this.Position);
		this.Position += 2;
		return value;
	}

	public int ReadInt32()
	{
		this.ensure(4);
		int value = BitConverter.ToInt32(this._data, this.Position);
		this.Position += 4;
		return value;
	}

	public sbyte ReadSByte()
	{
		return unchecked((sbyte)this.ReadByte());
	}

	/// <summary>
	/// Reads a string preceded by its length as a 16 bit integer.
	/// </summary>
	public string ReadText()
	{
		short size = this.ReadInt16();
		if (size <= 0)
		{
			return string.Empty;
		}

		return this.ReadFixedString(size);
	}

	public ushort ReadUInt16()
	{
		this.ensure(2);
		ushort value = BitConverter.ToUInt16(this._data, this.Position);
		this.Position += 2;
		return value;
	}

	public uint ReadUInt32()
	{
		this.ensure(4);
		uint value = BitConverter.ToUInt32(this._data, this.Position);
		this.Position += 4;
		return value;
	}

	public XY ReadXY()
	{
		return new XY(this.ReadDouble(), this.ReadDouble());
	}

	public XYZ ReadXYZ()
	{
		return new XYZ(this.ReadDouble(), this.ReadDouble(), this.ReadDouble());
	}

	public void Skip(int count)
	{
		this.ensure(count);
		this.Position += count;
	}

	private void ensure(int count)
	{
		if (!this.CanRead(count))
		{
			throw new DwgException($"Attempt to read {count} bytes at position {this.Position} beyond the end of the drawing ({this._data.Length} bytes).");
		}
	}
}
